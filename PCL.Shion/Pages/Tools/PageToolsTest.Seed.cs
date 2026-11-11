using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Platform.Storage;
using PCL.Shion.Controls;
using PCL.Shion.Core.Instances;
using PCL.Shion.Core.Saves;
using PCL.Shion.Core.Worlds;
using PCL.Shion.Localization;
using PCL.Shion.Pages.Instance.Saves;
using PCL.Shion.Pages.Launch;
using PCL.Shion.UI;

namespace PCL.Shion.Pages.Tools;

/// <summary>
///     百宝箱中的世界种子查看：列出当前实例的存档，或选择任意世界文件夹，只读地显示 level.dat 中的种子与基础信息。
/// </summary>
public partial class PageToolsTest
{
    private static readonly SaveManager SeedSaveManager = new();
    private bool _seedInitialized;
    private bool _seedListLoading;
    private CancellationTokenSource? _seedCancel;

    private void InitializeSeedViewer()
    {
        if (!_seedInitialized)
        {
            _seedInitialized = true;
            CmbSeedWorld.SelectionChanged += (_, _) =>
            {
                if (!_seedListLoading && CmbSeedWorld.SelectedItem is MyComboBoxItem { Tag: string folder })
                    _ = ShowSeedAsync(folder);
            };
            BtnSeedBrowse.Click += async (_, _) => await BrowseSeedWorldAsync();
        }

        _ = LoadSeedWorldsAsync();
    }

    /// <summary>列出启动页当前实例的存档。</summary>
    private async Task LoadSeedWorldsAsync()
    {
        var instance = PageLaunchLeft.McInstanceSelected;
        var root = App.Services.Settings.MinecraftRoot;
        List<(string Folder, string Name)> worlds = [];
        try
        {
            if (instance is not null)
                worlds = await Task.Run(() =>
                {
                    var gameDirectory = InstanceManager.GetGameDirectory(root, instance, InstanceStore.Load(root, instance.Name));
                    var saves = Path.Combine(gameDirectory, "saves");
                    return WorldScanner.ScanWorlds(gameDirectory)
                        .OrderByDescending(world => world.LastPlayed)
                        .Select(world => (Path.Combine(saves, world.FolderName), world.DisplayName))
                        .ToList();
                });
        }
        catch (Exception ex)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(null, "{Message}：{Error}", "读取存档列表失败", ex);
        }

        _seedListLoading = true;
        try
        {
            CmbSeedWorld.Items.Clear();
            foreach (var (folder, name) in worlds)
                CmbSeedWorld.Items.Add(new MyComboBoxItem { Content = name, Tag = folder });
            CmbSeedWorld.IsEnabled = worlds.Count > 0;
            CmbSeedWorld.SelectedIndex = -1;
        }
        finally
        {
            _seedListLoading = false;
        }

        if (worlds.Count == 0)
            ShowSeedHint(Lang.Text(instance is null ? "Tools.Seed.NoInstance" : "Tools.Seed.NoWorlds"));
        else
            CmbSeedWorld.SelectedIndex = 0;
    }

    private async Task BrowseSeedWorldAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Lang.Text("Tools.Seed.Browse.Title"),
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder)
            await ShowSeedAsync(folder);
    }

    private void ShowSeedHint(string text)
    {
        PanSeedInfo.IsVisible = false;
        LabSeedHint.Text = text;
        LabSeedHint.IsVisible = true;
        CardSeed.TriggerForceResize();
    }

    /// <summary>读取并显示世界信息。只读取，不会修改 level.dat。</summary>
    private async Task ShowSeedAsync(string folder)
    {
        _seedCancel?.Cancel();
        _seedCancel?.Dispose();
        _seedCancel = new CancellationTokenSource();
        var token = _seedCancel.Token;
        SaveInfo save;
        try
        {
            if (!File.Exists(Path.Combine(folder, "level.dat")))
            {
                ShowSeedHint(Lang.Text("Tools.Seed.Error.NoLevelDat", folder));
                return;
            }

            save = await SeedSaveManager.LoadSaveAsync(folder, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(null, "{Message}：{Error}", "读取 level.dat 失败", ex);
            ShowSeedHint(Lang.Text("Tools.Seed.Error.Unreadable", ex.Message));
            return;
        }

        if (token.IsCancellationRequested)
            return;
        LabSeedHint.IsVisible = false;
        PanSeedInfo.Children.Clear();
        PanSeedInfo.RowDefinitions.Clear();
        AddSeedRow(Lang.Text("Instance.Saves.Info.LevelName"), new TextBlock { Text = save.LevelName });
        AddSeedRow(Lang.Text("Instance.Saves.Info.Seed"), CreateSeedControl(save.Seed, save.VersionName));
        AddSeedRow(Lang.Text("Instance.Saves.Info.Version"), new TextBlock
        {
            Text = save.VersionName is null ? Lang.Text("Tools.Seed.UnknownVersion") : $"{save.VersionName} ({save.VersionId})"
        });
        AddSeedRow(Lang.Text("Instance.Saves.Info.GameMode"), new TextBlock
        {
            Text = PageInstanceSavesInfo.GameModeName(save.GameMode)
        });
        AddSeedRow(Lang.Text("Instance.Saves.Info.AllowCommands"), new TextBlock
        {
            Text = Lang.Text(save.AllowCommands
                ? "Instance.Saves.Info.AllowCommands.Allowed"
                : "Instance.Saves.Info.AllowCommands.NotAllowed")
        });
        if (save.Spawn is { } spawn)
            AddSeedRow(Lang.Text("Instance.Saves.Info.SpawnPoint"),
                new TextBlock { Text = $"{spawn.X:F0} / {spawn.Y:F0} / {spawn.Z:F0}" });
        PanSeedInfo.IsVisible = true;
        CardSeed.TriggerForceResize();
    }

    private Control CreateSeedControl(long? seed, string? versionName)
    {
        if (seed is not { } value)
            return new TextBlock { Text = Lang.Text("Instance.Saves.Info.GetFailed") };
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var copy = new MyTextButton { Text = text };
        ToolTip.SetTip(copy, Lang.Text("Tools.Seed.Copy"));
        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                                ?? throw new InvalidOperationException("Clipboard is unavailable.");
                await clipboard.SetTextAsync(text);
                Hint.Show(Lang.Text("Tools.Seed.Copied"), HintType.Success);
            }
            catch (Exception ex)
            {
                LogError(ex, "复制种子失败");
                Hint.Show(Lang.Text("Tools.Test.Error.OperationFailed"), HintType.Error);
            }
        };
        stack.Children.Add(copy);
        var chunkbase = new MyIconButton { SvgIcon = "lucide/external-link", Width = 22d, Height = 22d };
        ToolTip.SetTip(chunkbase, Lang.Text("Instance.Saves.Info.Chunkbase.ToolTip"));
        chunkbase.Click += (_, _) => PageInstanceSavesInfo.OpenChunkbase(text, versionName);
        stack.Children.Add(chunkbase);
        return stack;
    }

    private void AddSeedRow(string head, Control content)
    {
        var row = PanSeedInfo.RowDefinitions.Count;
        PanSeedInfo.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var headBlock = new TextBlock { Text = head, Margin = new Thickness(0d, 3d), VerticalAlignment = VerticalAlignment.Center };
        content.Margin = new Thickness(0d, 3d);
        content.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(headBlock, row);
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 2);
        PanSeedInfo.Children.Add(headBlock);
        PanSeedInfo.Children.Add(content);
    }
}
