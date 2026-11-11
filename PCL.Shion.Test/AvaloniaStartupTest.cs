using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Controls;
using PCL.Shion.Core.Accounts;
using PCL.Shion.Pages.Instance;
using PCL.Shion.Pages.Launch;
using PCL.Shion.Pages.Setup;
using PCL.Shion.Services;

namespace PCL.Shion.Test;

[TestClass]
[DoNotParallelize]
public sealed class AvaloniaStartupTest
{
    [TestMethod]
    public void RealApplicationCreatesMainWindowAndSettingsPagesWithoutWpf()
    {
        // 不打开窗口，避免触发首页联网、账号访问与外部进程；仍加载实际应用的 XAML、主题与控件。
        var directory = Path.Combine(Path.GetTempPath(), "shion-headless-" + Guid.NewGuid().ToString("N"));
        var loader = App.SettingsLoader;
        App.SettingsLoader = () => AppSettings.Load(Path.Combine(directory, "settings.json"));
        var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown };
        try
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithLifetime(lifetime);
            Assert.IsInstanceOfType<MainWindow>(lifetime.MainWindow);
            Assert.IsNotNull(lifetime.MainWindow.Content);
            lifetime.MainWindow.Measure(new Size(900, 550));
            lifetime.MainWindow.Arrange(new Rect(0, 0, 900, 550));
            Assert.IsTrue(lifetime.MainWindow.Bounds.Width > 0);
            Assert.IsNotNull(new PageSetupLaunch().Content);
            Assert.IsNotNull(new PageSetupLog().Content);
            Assert.IsNotNull(new PageSetupLauncherMisc().Content);
            var overall = new PageInstanceOverall();
            Assert.IsTrue(overall.FindControl<MyButton>("BtnManageScript")!.IsEnabled);
            Assert.IsTrue(overall.FindControl<MyButton>("BtnManageTest")!.IsEnabled);
            var tools = new PCL.Shion.Pages.Tools.PageToolsTest();
            Assert.IsNotNull(tools.FindControl<MyComboBox>("CmbDownloadConnections"));
            Assert.IsNotNull(tools.FindControl<MyTextBox>("TextDownloadHash"));
            Assert.IsNotNull(tools.FindControl<StackPanel>("PanDownloadTasks"));
            AssertEscapeCancelsLoginOnce(lifetime.MainWindow);
            AssertLaunchLoadingTextIsClipped();
            // 整合包导入使用的提示必须在语言文件中存在，缺失时 Lang 会返回键名本身或 !键名!
            foreach (var key in new[] { "Minecraft.Download.Modpack.Issues.Message", "Minecraft.Download.Modpack.CurseForgeUnsupported",
                         "Minecraft.Download.Modpack.Succeeded", "Instance.Export.MultiMcWarning",
                         "Instance.Overall.Manage.ExportLaunchScript.ToolTip", "Instance.Overall.Manage.TestGame.ToolTip",
                         "Instance.Overall.Script.FileName", "Instance.Overall.Script.FileFilter.Shell",
                         "Tools.Download.Connections", "Tools.Download.Hash", "Tools.Download.State.PausedResumable",
                         "Tools.Download.Error.HashMismatch" })
                Assert.IsFalse(PCL.Shion.Localization.Lang.Text(key).Contains(key), key);
        }
        finally
        {
            App.Services?.Dispose();
            App.SettingsLoader = loader;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    ///     启动面板的加载动画只留 50px 高度，溢出的“加载中”文字必须被裁剪，否则会与下方的启动标题重叠。
    /// </summary>
    private static void AssertLaunchLoadingTextIsClipped()
    {
        var loading = new PageLaunchLeft().FindControl<MyLoading>("LoadLaunching")!;
        loading.Measure(new Size(300, double.PositiveInfinity));
        loading.Arrange(new Rect(0, 0, 300, loading.Height));
        var text = loading.FindControl<TextBlock>("LabText")!;
        Assert.IsTrue(text.Bounds.Top >= loading.Height, "加载文字应位于动画区域之外");
        Assert.IsTrue(loading.ClipToBounds);
    }

    private static void AssertEscapeCancelsLoginOnce(Window window)
    {
        var completions = 0;
        var converter = new MyMsgLoginConverter
        {
            Device = new DeviceCodeInfo("unused-device", "unused-code", "https://example.invalid/verify", null, 300, 5),
            DeviceCodePoll = _ => throw new InvalidOperationException("Canceled login must not poll the network."),
            CompletionHandler = _ => completions++
        };
        var dialog = new MyMsgLogin(converter);
        var panel = window.FindControl<Panel>("PanMsg")!;
        panel.Children.Add(dialog);
        try
        {
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            window.RaiseEvent(key);
            Assert.IsTrue(key.Handled);
            Assert.IsTrue(converter.IsExited);
            Assert.IsInstanceOfType<OperationCanceledException>(converter.Result);
            Assert.AreEqual(1, completions);
            dialog.Cancel();
            dialog.OpenVerificationWebsite(); // Closed dialogs must never reopen an external browser.
            Assert.AreEqual(1, completions);
        }
        finally
        {
            dialog.Cancel();
            panel.Children.Remove(dialog);
        }
    }
}
