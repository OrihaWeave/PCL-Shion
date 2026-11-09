using System.IO.Compression;
using PCL.Shion.Core.Download;
using PCL.Shion.Core.Install;
using PCL.Shion.Core.Instances;
using PCL.Shion.Core.Loaders;
using PCL.Shion.Core.Sources;
using PCL.Shion.Core.Versions;

namespace PCL.Shion.Core.Import;

/// <summary>
/// 按 <see cref="ModpackPlan"/> 把整合包安装为新实例。
/// </summary>
/// <remarks>
/// 先把 overrides 与需要下载的文件放进实例目录，最后安装原版或加载器并写出版本 JSON。
/// 版本 JSON 出现之前实例不会被扫描到，因此用户不会看到装到一半的实例。安装失败时删除整个新建的实例目录。
/// </remarks>
public sealed class ModpackInstaller(DownloadEngine engine, DownloadSource source)
{
    /// <summary>复制 overrides 的阶段名。</summary>
    public const string OverridesStage = "modpack-overrides";

    /// <summary>下载整合包文件的阶段名。</summary>
    public const string FilesStage = "modpack-files";

    /// <summary>
    /// 安装整合包。实例始终开启版本隔离，整合包中的内存与参数设置写入实例设置。
    /// </summary>
    /// <param name="archivePath">整合包文件。</param>
    /// <param name="plan">由 <see cref="ModpackReader.Read(string)"/> 读出的计划。</param>
    /// <param name="minecraftRoot">.minecraft 文件夹。</param>
    /// <param name="instanceName">新实例的名称。</param>
    /// <param name="javaExecutable">运行 Forge 与 NeoForge 安装处理器的 Java。</param>
    /// <exception cref="ArgumentException">实例名无效，或与有加载器时继承的原版版本号相同。</exception>
    /// <exception cref="IOException">同名实例已存在。</exception>
    /// <exception cref="InvalidDataException">整合包中有不安全的路径。</exception>
    /// <exception cref="InstallException">有文件下载失败。</exception>
    public async Task InstallAsync(
        string archivePath,
        ModpackPlan plan,
        string minecraftRoot,
        string instanceName,
        string javaExecutable,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        InstallationSession.ValidateInstanceName(instanceName);
        if (plan.Loader is not null && string.Equals(instanceName, plan.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The instance name must differ from the Minecraft version it inherits from.", nameof(instanceName));

        var instanceDirectory = Path.GetFullPath(Path.Combine(minecraftRoot, "versions", instanceName));
        if (Directory.Exists(instanceDirectory) || File.Exists(instanceDirectory))
            throw new IOException($"Instance '{instanceName}' already exists.");

        Directory.CreateDirectory(instanceDirectory);
        try
        {
            progress?.Report(new InstallProgress(OverridesStage, null));
            using (var archive = ModpackReader.OpenArchive(archivePath))
                ExtractOverrides(archive, plan.OverrideFolders, instanceDirectory, cancellationToken);

            if (plan.Files.Count > 0)
            {
                var items = plan.Files
                    .Select(file => new DownloadItem(file.Urls, ResolveTarget(instanceDirectory, file.Path), file.Size, file.Sha1))
                    .ToList();
                var stage = progress is null
                    ? null
                    : new Progress<DownloadProgress>(value => progress.Report(new InstallProgress(FilesStage, value)));
                progress?.Report(new InstallProgress(FilesStage, null));
                var result = await engine.DownloadAsync(items, stage, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                    throw new InstallException(result);
            }

            await InstallGameAsync(plan, minecraftRoot, instanceName, javaExecutable, progress, cancellationToken).ConfigureAwait(false);

            var settings = InstanceStore.Load(minecraftRoot, instanceName);
            settings.Isolated = true;
            if (plan.MemoryMb is { } memory)
                settings.MaxMemoryMb = memory;
            if (plan.JvmArguments is { } jvmArguments)
                settings.ExtraJvmArguments = jvmArguments;
            if (plan.GameArguments is { } gameArguments)
                settings.ExtraGameArguments = gameArguments;
            InstanceStore.Save(minecraftRoot, instanceName, settings);
        }
        catch
        {
            DeleteQuietly(instanceDirectory);
            throw;
        }
    }

    private async Task InstallGameAsync(
        ModpackPlan plan,
        string minecraftRoot,
        string instanceName,
        string javaExecutable,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (plan.Loader is { } loader)
        {
            await new LoaderInstaller(engine, source)
                .InstallAsync(minecraftRoot, loader, instanceName, javaExecutable, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var manifest = await VersionManifestClient.FetchAsync(engine.Http, source, cancellationToken).ConfigureAwait(false);
        var version = manifest.Versions.FirstOrDefault(item => item.Id == plan.MinecraftVersion)
                      ?? throw new ArgumentException($"Minecraft version '{plan.MinecraftVersion}' is not in the version manifest.", nameof(plan));
        await new GameInstaller(engine, source)
            .InstallVanillaAsync(minecraftRoot, version, instanceName, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 把压缩包中 <paramref name="folders"/> 下的文件按顺序复制到实例目录，后复制的覆盖先复制的。
    /// </summary>
    /// <exception cref="InvalidDataException">压缩包中有跳出实例目录的路径。</exception>
    internal static void ExtractOverrides(ZipArchive archive, IReadOnlyList<string> folders, string instanceDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var folder in folders)
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(folder, StringComparison.Ordinal) || name.Length == folder.Length)
                    continue;

                var relative = name[folder.Length..];
                if (relative.EndsWith('/'))
                {
                    Directory.CreateDirectory(ResolveTarget(instanceDirectory, relative.TrimEnd('/')));
                    continue;
                }

                var target = ResolveTarget(instanceDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
    }

    /// <summary>
    /// 把整合包中的相对路径转换为实例目录下的绝对路径，并确认结果没有跳出实例目录。
    /// </summary>
    internal static string ResolveTarget(string instanceDirectory, string relativePath)
    {
        var checkedPath = ModpackReader.CheckRelativePath(relativePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, checkedPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException($"The modpack contains an unsafe path: '{relativePath}'.");
        return target;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
