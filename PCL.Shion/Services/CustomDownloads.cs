using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Logging;
using Avalonia.Threading;
using PCL.Shion.Core.Download;
using PCL.Shion.Core.IO;
using PCL.Shion.Core.Platform;
using PCL.Shion.UI;

namespace PCL.Shion.Services;

/// <summary>百宝箱多线程下载任务的状态。</summary>
public enum CustomDownloadState
{
    Waiting,
    Downloading,
    Paused,
    Failed,
    Completed
}

/// <summary>
///     百宝箱中的一个下载任务。只在 UI 线程上读写；<see cref="Changed" /> 也在 UI 线程上触发。
/// </summary>
public sealed class CustomDownloadTask
{
    private CancellationTokenSource? _cancel;

    public CustomDownloadTask(string url, string targetPath, int connections, string? expectedHash)
    {
        Id = Guid.NewGuid().ToString("N");
        Url = url;
        TargetPath = targetPath;
        Connections = Math.Clamp(connections, 1, SegmentedDownloader.MaxConnections);
        ExpectedHash = string.IsNullOrWhiteSpace(expectedHash) ? null : expectedHash.Trim();
    }

    public string Id { get; init; }

    public string Url { get; }

    public string TargetPath { get; }

    public string FileName => Path.GetFileName(TargetPath);

    public int Connections { get; }

    public string? ExpectedHash { get; }

    public CustomDownloadState State { get; internal set; } = CustomDownloadState.Waiting;

    public SegmentedDownloadProgress? Progress { get; internal set; }

    /// <summary>失败原因，已经转成面向用户的文字。</summary>
    public string? Error { get; internal set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public bool IsActive => State is CustomDownloadState.Waiting or CustomDownloadState.Downloading;

    /// <summary>状态或进度变化。</summary>
    public event Action<CustomDownloadTask>? Changed;

    internal CancellationTokenSource BeginRun()
    {
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        return _cancel;
    }

    internal void RequestPause() => _cancel?.Cancel();

    internal void RaiseChanged() => Changed?.Invoke(this);
}

/// <summary>
///     百宝箱多线程下载器的任务列表：用 <see cref="SegmentedDownloader" /> 分段下载，限制同时进行的任务数，
///     并把任务列表保存到设置目录，启动器重启后未完成的任务以“已暂停”状态恢复，可以继续下载。
/// </summary>
public static class CustomDownloads
{
    /// <summary>同时进行的任务数上限，其余任务排队等待。</summary>
    public const int MaxRunningTasks = 3;

    private static readonly string StorePath = Path.Combine(
        SpecialFolders.Get(Environment.SpecialFolder.ApplicationData), "PCL Shion", "custom-downloads.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static bool _loaded;

    /// <summary>全部任务，新任务在前。只在 UI 线程上访问。</summary>
    public static ObservableCollection<CustomDownloadTask> Tasks { get; } = [];

    /// <summary>读取保存的任务列表。重复调用不会重复读取。</summary>
    public static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            var stored = JsonFileStore.Read<List<StoredTask>>(StorePath, Json).Value ?? [];
            foreach (var item in stored)
            {
                var task = new CustomDownloadTask(item.Url, item.TargetPath, item.Connections, item.ExpectedHash)
                {
                    Id = item.Id,
                    CreatedAt = item.CreatedAt
                };
                task.State = item.Completed ? CustomDownloadState.Completed : CustomDownloadState.Paused;
                task.Error = item.Error;
                Tasks.Add(task);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "读取自定义下载任务列表失败");
        }
    }

    /// <summary>添加并开始一个任务。同一目标已有进行中的任务时返回 null。</summary>
    public static CustomDownloadTask? Add(string url, string targetPath, int connections, string? expectedHash)
    {
        EnsureLoaded();
        var fullPath = Path.GetFullPath(targetPath);
        if (Tasks.Any(t => t.IsActive && PathEquals(t.TargetPath, fullPath)))
            return null;
        // 同一目标的旧记录（已完成、失败或暂停）由新任务取代
        foreach (var old in Tasks.Where(t => PathEquals(t.TargetPath, fullPath)).ToList())
            Tasks.Remove(old);

        var task = new CustomDownloadTask(url, fullPath, connections, expectedHash);
        Tasks.Insert(0, task);
        Save();
        Pump();
        return task;
    }

    /// <summary>暂停正在进行或排队的任务；已下载的数据保留，可以继续。</summary>
    public static void Pause(CustomDownloadTask task)
    {
        if (task.State == CustomDownloadState.Waiting)
        {
            task.State = CustomDownloadState.Paused;
            task.RaiseChanged();
            Save();
        }
        else if (task.State == CustomDownloadState.Downloading)
        {
            task.RequestPause();
        }
    }

    /// <summary>继续暂停或失败的任务。</summary>
    public static void Resume(CustomDownloadTask task)
    {
        if (task.State is not (CustomDownloadState.Paused or CustomDownloadState.Failed))
            return;
        task.State = CustomDownloadState.Waiting;
        task.Error = null;
        task.RaiseChanged();
        Save();
        Pump();
    }

    /// <summary>从列表中移除任务；未完成的任务会先停止并删除已下载的临时数据。</summary>
    public static void Remove(CustomDownloadTask task)
    {
        var wasCompleted = task.State == CustomDownloadState.Completed;
        task.RequestPause();
        Tasks.Remove(task);
        Save();
        if (!wasCompleted)
            _ = Task.Run(async () =>
            {
                // 等正在进行的下载响应取消、关闭文件后再删除
                await Task.Delay(500);
                SegmentedDownloader.DeletePartial(task.TargetPath);
            });
    }

    private static void Pump()
    {
        var running = Tasks.Count(t => t.State == CustomDownloadState.Downloading);
        // 先添加的任务先开始
        foreach (var task in Tasks.Where(t => t.State == CustomDownloadState.Waiting).Reverse().ToList())
        {
            if (running >= MaxRunningTasks)
                break;
            running++;
            _ = RunAsync(task);
        }
    }

    private static async Task RunAsync(CustomDownloadTask task)
    {
        var cancel = task.BeginRun();
        task.State = CustomDownloadState.Downloading;
        task.Error = null;
        task.RaiseChanged();
        // 启动器退出时同样按暂停处理，已下载的数据留到下次继续
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, App.Services.ShutdownToken);
        var token = linked.Token;
        var progress = new Progress<SegmentedDownloadProgress>(value =>
        {
            if (task.State != CustomDownloadState.Downloading)
                return;
            task.Progress = value;
            task.RaiseChanged();
        });
        try
        {
            await Task.Run(() => SegmentedDownloader.DownloadAsync(
                App.Services.Engine.Http, new Uri(task.Url), task.TargetPath,
                new SegmentedDownloadOptions { Connections = task.Connections, ExpectedHash = task.ExpectedHash },
                progress, token), CancellationToken.None);
            task.State = CustomDownloadState.Completed;
        }
        catch (OperationCanceledException)
        {
            task.State = CustomDownloadState.Paused;
        }
        catch (Exception ex)
        {
            Log(ex, "自定义下载失败：" + task.Url);
            task.State = CustomDownloadState.Failed;
            task.Error = Describe(ex);
        }

        Dispatcher.UIThread.Post(() =>
        {
            task.RaiseChanged();
            if (task.State == CustomDownloadState.Completed)
                Hint.Show(Localization.Lang.Text("Tools.Test.CustomDownload.Finished",
                    Localization.Lang.Text("Tools.Test.CustomDownload.LoaderName", task.FileName)), HintType.Success);
            Save();
            Pump();
        });
    }

    private static string Describe(Exception ex) => ex switch
    {
        DownloadHashMismatchException mismatch => Localization.Lang.Text("Tools.Download.Error.HashMismatch",
            mismatch.Expected, mismatch.Actual),
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status} {status}",
        _ => ex.Message
    };

    private static void Save()
    {
        try
        {
            var stored = Tasks.Select(t => new StoredTask
            {
                Id = t.Id,
                Url = t.Url,
                TargetPath = t.TargetPath,
                Connections = t.Connections,
                ExpectedHash = t.ExpectedHash,
                Completed = t.State == CustomDownloadState.Completed,
                Error = t.Error,
                CreatedAt = t.CreatedAt
            }).ToList();
            JsonFileStore.Save(StorePath, stored, Json);
        }
        catch (Exception ex)
        {
            Log(ex, "保存自定义下载任务列表失败");
        }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static void Log(Exception ex, string message) =>
        Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(null, "{Message}：{Error}", message, ex);

    private sealed class StoredTask
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public int Connections { get; set; } = 8;
        public string? ExpectedHash { get; set; }
        public bool Completed { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
