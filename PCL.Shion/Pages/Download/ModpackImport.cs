using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using PCL.Shion.Core.Import;
using PCL.Shion.Localization;
using PCL.Shion.UI;

namespace PCL.Shion.Pages.Download;

/// <summary>
/// 把本地整合包导入为当前 Minecraft 文件夹下的新实例：选择文件、确认实例名与无法自动处理的内容，然后在后台安装。
/// </summary>
public static class ModpackImport
{
    /// <summary>
    /// 开始导入。<paramref name="archivePath"/> 为空时先让用户选择文件。
    /// 本方法在安装开始后返回，安装结果通过提示告知用户。
    /// </summary>
    public static async Task StartAsync(string? archivePath = null)
    {
        try
        {
            if (string.IsNullOrEmpty(archivePath))
            {
                archivePath = await SystemDialogs.SelectFileAsync(
                    Lang.Text("Minecraft.Download.Modpack.FileDialog.Filter"),
                    Lang.Text("Minecraft.Download.Modpack.FileDialog.Title"));
                if (string.IsNullOrEmpty(archivePath))
                    return;
            }

            if (archivePath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                Hint.Show(Lang.Text("Minecraft.Download.Modpack.RarUnsupported"), HintType.Error);
                return;
            }

            var path = archivePath;
            ModpackPlan plan;
            try
            {
                plan = await Task.Run(() => ModpackReader.Read(path));
            }
            catch (NotSupportedException)
            {
                Hint.Show(Lang.Text("Minecraft.Download.Modpack.CurseForgeUnsupported"), HintType.Error);
                return;
            }
            catch (InvalidDataException ex)
            {
                LogWrapper.Error(ex, "读取整合包失败");
                Hint.Show(Lang.Text("Minecraft.Download.Modpack.UnknownArchiveStructure"), HintType.Error);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogWrapper.Error(ex, "打开整合包失败");
                Hint.Show(Lang.Text("Minecraft.Download.Modpack.UnsupportedArchive"), HintType.Error);
                return;
            }

            var root = App.Services.Settings.MinecraftRoot;
            var versionsFolder = Path.Combine(root, "versions");
            var defaultName = SanitizeName(plan.Name ?? Path.GetFileNameWithoutExtension(path));
            var nameValidator = new FolderNameValidator(versionsFolder);
            var name = await MsgBox.InputAsync(Lang.Text("Minecraft.Download.Modpack.InputInstanceName"),
                defaultInput: defaultName,
                validate: input =>
                {
                    var result = nameValidator.Validate(input);
                    if (!result.IsValid)
                        return result.Errors[0].ErrorMessage;
                    return plan.Loader is not null && string.Equals(input, plan.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
                        ? Lang.Text("Minecraft.Download.Modpack.InstanceNameSameAsVersion")
                        : null;
                });
            if (string.IsNullOrEmpty(name))
                return;

            if (plan.Issues.Count > 0)
            {
                var lines = string.Join(Environment.NewLine, plan.Issues.Select(DescribeIssue).Distinct().Select(line => "- " + line));
                var choice = await MsgBox.ShowAsync(
                    Lang.Text("Minecraft.Download.Modpack.Issues.Message", lines),
                    Lang.Text("Minecraft.Download.Modpack.Issues.Title"),
                    Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true);
                if (choice != 1)
                    return;
            }

            Hint.Show(Lang.Text("Minecraft.Download.Modpack.Started", name));
            _ = InstallAsync(path, plan, root, name);
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "导入整合包失败");
            Hint.Show(Lang.Text("Minecraft.Download.Modpack.Error.OperationFailed"), HintType.Error);
        }
    }

    private static async Task InstallAsync(string archivePath, ModpackPlan plan, string minecraftRoot, string name)
    {
        var services = App.Services;
        try
        {
            // TODO(shion): Forge 与 NeoForge 的处理器需要 Java，与普通安装一样暂时使用设置中的默认 Java
            var java = services.Settings.DefaultJavaExecutable ?? "java";
            await new ModpackInstaller(services.Engine, services.Source)
                .InstallAsync(archivePath, plan, minecraftRoot, name, java, null, services.ShutdownToken)
                .ConfigureAwait(false);
            services.NotifyInstancesChanged();
            Dispatcher.UIThread.Post(() =>
                Hint.Show(Lang.Text("Minecraft.Download.Modpack.Succeeded", name), HintType.Success));
        }
        catch (OperationCanceledException) when (services.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "安装整合包失败");
            var message = ex is InvalidDataException
                ? Lang.Text("Minecraft.Download.Modpack.PathOutsideInstance.Title")
                : ex.Message;
            Dispatcher.UIThread.Post(() =>
                Hint.Show(Lang.Text("Minecraft.Download.Modpack.Failed", name, message), HintType.Error));
        }
    }

    private static string DescribeIssue(ModpackIssue issue) => issue.Kind switch
    {
        ModpackIssueKind.OptiFine => Lang.Text("Minecraft.Download.Modpack.Issue.OptiFine", issue.Detail),
        ModpackIssueKind.JsonPatch => Lang.Text("Minecraft.Download.Modpack.Issue.JsonPatch", issue.Detail),
        ModpackIssueKind.CurseForgeFile => Lang.Text("Minecraft.Download.Modpack.Issue.CurseForgeFile", issue.Detail),
        _ => Lang.Text("Minecraft.Download.Modpack.Issue.UnsupportedComponent", issue.Detail)
    };

    /// <summary>
    /// 把整合包名称整理为可用的默认实例名：替换文件名中不允许的字符与 Minecraft 不允许的 <c>!</c>、<c>;</c>。
    /// </summary>
    internal static string SanitizeName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '!', ';', '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var result = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return result.Length == 0 ? "Modpack" : result;
    }
}
