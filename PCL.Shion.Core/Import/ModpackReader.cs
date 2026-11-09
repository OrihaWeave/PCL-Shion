using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Shion.Core.Loaders;

namespace PCL.Shion.Core.Import;

/// <summary>
/// 读取本地整合包压缩文件，识别格式并生成 <see cref="ModpackPlan"/>。支持 Modrinth、MCBBS 与 MultiMC / Prism 格式。
/// </summary>
public static class ModpackReader
{
    private const string ModrinthIndex = "modrinth.index.json";
    private const string McbbsMeta = "mcbbs.packmeta";
    private const string CurseManifest = "manifest.json";
    private const string MultiMcPack = "mmc-pack.json";

    /// <summary>
    /// 读取整合包。
    /// </summary>
    /// <exception cref="NotSupportedException">整合包是 CurseForge 格式，暂不支持。</exception>
    /// <exception cref="InvalidDataException">不是可识别的整合包，或清单内容无效。</exception>
    public static ModpackPlan Read(string archivePath)
    {
        using var archive = OpenArchive(archivePath);
        return Read(archive);
    }

    /// <summary>
    /// 打开整合包压缩文件。没有标记 UTF-8 的文件名先按 UTF-8 解码，出现无法解码的字符时改用 GB18030，
    /// 以兼容国内旧整合包常见的 GBK 文件名。
    /// </summary>
    public static ZipArchive OpenArchive(string archivePath)
    {
        var archive = ZipFile.Open(archivePath, ZipArchiveMode.Read, Encoding.UTF8);
        if (!archive.Entries.Any(entry => entry.FullName.Contains('�')))
            return archive;

        archive.Dispose();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return ZipFile.Open(archivePath, ZipArchiveMode.Read, Encoding.GetEncoding("GB18030"));
    }

    /// <inheritdoc cref="Read(string)"/>
    public static ModpackPlan Read(ZipArchive archive)
    {
        if (archive.GetEntry(ModrinthIndex) is { } modrinth)
            return ReadModrinth(ReadObject(modrinth));
        if (archive.GetEntry(McbbsMeta) is { } mcbbs)
            return ReadMcbbs(ReadObject(mcbbs));
        if (FindMultiMcRoot(archive) is { } multiMcRoot)
            return ReadMultiMc(archive, multiMcRoot);
        if (archive.GetEntry(CurseManifest) is { } manifest)
        {
            var json = ReadObject(manifest);
            if (json["addons"] is JsonArray)
                return ReadMcbbs(json);
            if (json["minecraft"] is JsonObject)
                throw new NotSupportedException("CurseForge modpacks are not supported yet.");
        }

        throw new InvalidDataException("The archive is not a recognized modpack.");
    }

    #region Modrinth

    private static ModpackPlan ReadModrinth(JsonObject index)
    {
        var dependencies = index["dependencies"] as JsonObject
            ?? throw new InvalidDataException("modrinth.index.json has no dependencies.");
        var minecraft = GetString(dependencies, "minecraft")
            ?? throw new InvalidDataException("modrinth.index.json does not name a Minecraft version.");

        var loaders = new List<LoaderVersion>();
        AddLoader(loaders, LoaderKind.Forge, GetString(dependencies, "forge"), minecraft);
        AddLoader(loaders, LoaderKind.NeoForge, GetString(dependencies, "neoforge"), minecraft);
        AddLoader(loaders, LoaderKind.Fabric, GetString(dependencies, "fabric-loader"), minecraft);
        AddLoader(loaders, LoaderKind.Quilt, GetString(dependencies, "quilt-loader"), minecraft);

        var files = new List<ModpackFile>();
        foreach (var node in index["files"] as JsonArray ?? [])
        {
            if (node is not JsonObject file)
                continue;
            // 只在服务端使用的文件不安装到客户端
            if (file["env"] is JsonObject env && GetString(env, "client") == "unsupported")
                continue;

            var path = CheckRelativePath(GetString(file, "path")
                ?? throw new InvalidDataException("A file in modrinth.index.json has no path."));
            var urls = (file["downloads"] as JsonArray ?? [])
                .Select(url => url?.GetValue<string>())
                .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                .Select(url => new Uri(url!))
                .ToList();
            if (urls.Count == 0)
                throw new InvalidDataException($"The file '{path}' in modrinth.index.json has no download URL.");

            var sha1 = file["hashes"] is JsonObject hashes ? GetString(hashes, "sha1")?.ToLowerInvariant() : null;
            var size = file["fileSize"] is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var length) ? length : (long?)null;
            files.Add(new ModpackFile(path, urls, sha1, size));
        }

        return new ModpackPlan
        {
            Format = ModpackFormat.Modrinth,
            Name = GetString(index, "name"),
            Version = GetString(index, "versionId"),
            MinecraftVersion = minecraft,
            Loader = SingleLoader(loaders),
            OverrideFolders = ["overrides/", "client-overrides/"],
            Files = files
        };
    }

    #endregion

    #region MCBBS

    private static ModpackPlan ReadMcbbs(JsonObject meta)
    {
        var addons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in meta["addons"] as JsonArray ?? [])
        {
            if (node is JsonObject addon && GetString(addon, "id") is { } id && GetString(addon, "version") is { } version)
                addons[id] = version;
        }

        if (!addons.TryGetValue("game", out var minecraft))
            throw new InvalidDataException("The MCBBS modpack does not name a Minecraft version.");

        var issues = new List<ModpackIssue>();
        var loaders = new List<LoaderVersion>();
        AddLoader(loaders, LoaderKind.Forge, addons.GetValueOrDefault("forge"), minecraft);
        AddLoader(loaders, LoaderKind.NeoForge, addons.GetValueOrDefault("neoforge"), minecraft);
        AddLoader(loaders, LoaderKind.Fabric, addons.GetValueOrDefault("fabric"), minecraft);
        AddLoader(loaders, LoaderKind.Quilt, addons.GetValueOrDefault("quilt"), minecraft);
        if (addons.TryGetValue("optifine", out var optiFine))
            issues.Add(new ModpackIssue(ModpackIssueKind.OptiFine, optiFine));
        foreach (var (id, version) in addons)
        {
            if (id is not ("game" or "forge" or "neoforge" or "fabric" or "quilt" or "optifine"))
                issues.Add(new ModpackIssue(ModpackIssueKind.UnsupportedComponent, $"{id} {version}"));
        }

        // addon 类型的文件已经打包在 overrides 中；curse 类型需要通过 CurseForge 下载，暂不支持
        foreach (var node in meta["files"] as JsonArray ?? [])
        {
            if (node is JsonObject file && GetString(file, "type") == "curse")
                issues.Add(new ModpackIssue(ModpackIssueKind.CurseForgeFile,
                    GetString(file, "fileName") ?? $"{file["projectID"]}/{file["fileID"]}"));
        }

        string? jvmArguments = null, gameArguments = null;
        int? memory = null;
        if (meta["launchInfo"] is JsonObject launchInfo)
        {
            jvmArguments = JoinArguments(launchInfo["javaArgument"]);
            gameArguments = JoinArguments(launchInfo["launchArgument"]);
            if (launchInfo["minMemory"] is JsonValue memoryValue && memoryValue.TryGetValue<int>(out var minMemory) && minMemory > 0)
                memory = minMemory;
        }

        return new ModpackPlan
        {
            Format = ModpackFormat.Mcbbs,
            Name = GetString(meta, "name"),
            Version = GetString(meta, "version"),
            MinecraftVersion = minecraft,
            Loader = SingleLoader(loaders),
            OverrideFolders = ["overrides/"],
            JvmArguments = jvmArguments,
            GameArguments = gameArguments,
            MemoryMb = memory,
            Issues = issues
        };
    }

    private static string? JoinArguments(JsonNode? node)
    {
        var arguments = (node as JsonArray ?? [])
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null)
            .Where(text => !string.IsNullOrEmpty(text))
            .ToList();
        return arguments.Count == 0 ? null : string.Join(' ', arguments);
    }

    #endregion

    #region MultiMC

    /// <summary>
    /// 找到 <c>mmc-pack.json</c> 所在的文件夹：压缩包根目录，或根目录下唯一的一层文件夹。返回以 <c>/</c> 结尾的前缀，根目录为空字符串。
    /// </summary>
    private static string? FindMultiMcRoot(ZipArchive archive)
    {
        if (archive.GetEntry(MultiMcPack) is not null)
            return "";
        var candidates = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(name => name.EndsWith("/" + MultiMcPack, StringComparison.Ordinal) && name.Count(c => c == '/') == 1)
            .Select(name => name[..^MultiMcPack.Length])
            .Distinct()
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static ModpackPlan ReadMultiMc(ZipArchive archive, string root)
    {
        var pack = ReadObject(archive.GetEntry(root + MultiMcPack)!);
        var config = archive.GetEntry(root + "instance.cfg") is { } configEntry
            ? ParseInstanceConfig(ReadText(configEntry))
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? minecraft = null;
        var issues = new List<ModpackIssue>();
        var loaders = new List<(LoaderKind Kind, string Version)>();
        foreach (var node in pack["components"] as JsonArray ?? [])
        {
            if (node is not JsonObject component || GetString(component, "uid") is not { } uid)
                continue;
            var version = GetString(component, "version") ?? GetString(component, "cachedVersion");
            switch (uid)
            {
                case "net.minecraft":
                    minecraft = version;
                    break;
                // 由主组件自动带出的依赖，不需要单独处理
                case "org.lwjgl" or "org.lwjgl3" or "net.fabricmc.intermediary" or "org.quiltmc.hashed":
                    break;
                case "net.minecraftforge" when version is not null && version.StartsWith("0.", StringComparison.Ordinal):
                    issues.Add(new ModpackIssue(ModpackIssueKind.UnsupportedComponent, $"Cleanroom {version}"));
                    break;
                case "net.minecraftforge" when version is not null:
                    loaders.Add((LoaderKind.Forge, version));
                    break;
                case "net.neoforged" when version is not null:
                    loaders.Add((LoaderKind.NeoForge, version));
                    break;
                case "net.fabricmc.fabric-loader" when version is not null:
                    loaders.Add((LoaderKind.Fabric, version));
                    break;
                case "org.quiltmc.quilt-loader" when version is not null:
                    loaders.Add((LoaderKind.Quilt, version));
                    break;
                default:
                    var name = GetString(component, "cachedName") ?? uid;
                    var detail = version is null ? name : $"{name} {version}";
                    issues.Add(new ModpackIssue(
                        uid.Contains("optifine", StringComparison.OrdinalIgnoreCase) ? ModpackIssueKind.OptiFine : ModpackIssueKind.UnsupportedComponent,
                        detail));
                    break;
            }
        }

        if (minecraft is null)
            throw new InvalidDataException("mmc-pack.json does not name a Minecraft version.");

        // JSON Patches 可以任意修改版本 JSON，无法可靠地转换，只提示用户
        var patchesPrefix = root + "patches/";
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith(patchesPrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                issues.Add(new ModpackIssue(ModpackIssueKind.JsonPatch, name[patchesPrefix.Length..]));
        }

        var jvmArguments = IsTrue(config, "OverrideJavaArgs") && config.TryGetValue("JvmArgs", out var args) && !string.IsNullOrWhiteSpace(args)
            ? args.Trim()
            : null;
        int? memory = IsTrue(config, "OverrideMemory") && config.TryGetValue("MaxMemAlloc", out var maxMemory)
                      && int.TryParse(maxMemory, out var megabytes) && megabytes > 0
            ? megabytes
            : null;

        return new ModpackPlan
        {
            Format = ModpackFormat.MultiMc,
            Name = config.TryGetValue("name", out var instanceName) && !string.IsNullOrWhiteSpace(instanceName) ? instanceName.Trim() : null,
            MinecraftVersion = minecraft,
            Loader = SingleLoader(loaders.Select(loader => CreateLoader(loader.Kind, loader.Version, minecraft)).ToList()),
            // Prism 与新版 MultiMC 使用 .minecraft，很早的版本使用 minecraft
            OverrideFolders = [root + "minecraft/", root + ".minecraft/"],
            JvmArguments = jvmArguments,
            MemoryMb = memory,
            Issues = issues
        };
    }

    /// <summary>
    /// 解析 MultiMC 的 <c>instance.cfg</c>：每行一个 <c>键=值</c>，忽略节名与注释。
    /// </summary>
    internal static Dictionary<string, string> ParseInstanceConfig(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] is '[' or '#' or ';')
                continue;
            var separator = line.IndexOf('=');
            if (separator > 0)
                result[line[..separator].Trim()] = line[(separator + 1)..];
        }

        return result;
    }

    private static bool IsTrue(Dictionary<string, string> config, string key) =>
        config.TryGetValue(key, out var value) && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region 通用

    private static void AddLoader(List<LoaderVersion> loaders, LoaderKind kind, string? version, string minecraft)
    {
        if (!string.IsNullOrWhiteSpace(version))
            loaders.Add(CreateLoader(kind, version.Trim(), minecraft));
    }

    /// <summary>
    /// 创建加载器版本。部分整合包把 Forge / NeoForge 版本写成 <c>原版-加载器</c> 的完整形式，这里去掉原版前缀。
    /// </summary>
    private static LoaderVersion CreateLoader(LoaderKind kind, string version, string minecraft)
    {
        if (kind is LoaderKind.Forge or LoaderKind.NeoForge && version.StartsWith(minecraft + "-", StringComparison.Ordinal))
            version = version[(minecraft.Length + 1)..];
        var stable = !version.Contains("beta", StringComparison.OrdinalIgnoreCase)
                     && !version.Contains("alpha", StringComparison.OrdinalIgnoreCase);
        return new LoaderVersion(kind, version, minecraft, stable);
    }

    private static LoaderVersion? SingleLoader(List<LoaderVersion> loaders) => loaders.Count switch
    {
        0 => null,
        1 => loaders[0],
        _ => throw new InvalidDataException("The modpack requires more than one mod loader: "
                                            + string.Join(", ", loaders.Select(loader => loader.Kind)))
    };

    /// <summary>
    /// 检查整合包中的相对路径：不能是绝对路径，也不能通过 <c>..</c> 跳出实例目录。返回以 <c>/</c> 分隔的路径。
    /// </summary>
    /// <exception cref="InvalidDataException">路径不安全。</exception>
    internal static string CheckRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Length == 0 || normalized[0] == '/' || Path.IsPathRooted(normalized) || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is ".." or ""))
            throw new InvalidDataException($"The modpack contains an unsafe path: '{path}'.");
        return normalized;
    }

    private static JsonObject ReadObject(ZipArchiveEntry entry)
    {
        try
        {
            return JsonNode.Parse(ReadText(entry)) as JsonObject
                   ?? throw new InvalidDataException($"{entry.FullName} is not a JSON object.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException($"{entry.FullName} is not valid JSON.", ex);
        }
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? GetString(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    #endregion
}
