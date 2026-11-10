using PCL.Shion.Core.Platform;

namespace PCL.Shion.Core.Install;

/// <summary>
/// 当前进程正在安装的实例。扫描时不显示尚未写完的版本，且同一实例不能被两个安装任务同时修改。
/// 只记录活动会话，不写持久标记，因此取消或进程意外结束不会使实例永久隐藏。
/// </summary>
internal sealed class InstallationSession : IDisposable
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> Active = new(
        PlatformInfo.Os == OsKind.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;
    private int _disposed;

    private InstallationSession(string directory)
    {
        _directory = directory;
    }

    public static InstallationSession Begin(string minecraftRoot, string instanceName)
    {
        ValidateInstanceName(instanceName);
        var directory = Path.GetFullPath(Path.Combine(minecraftRoot, "versions", instanceName));
        lock (Gate)
        {
            if (!Active.Add(directory))
                throw new IOException($"Instance '{instanceName}' is already being installed.");
        }
        return new InstallationSession(directory);
    }

    public static bool IsActive(string directory)
    {
        lock (Gate)
            return Active.Contains(Path.GetFullPath(directory));
    }

    /// <summary>复合安装只能复用自己持有的目标会话，不能跳过另一实例的互斥。</summary>
    internal void EnsureOwns(string minecraftRoot, string instanceName)
    {
        var directory = Path.GetFullPath(Path.Combine(minecraftRoot, "versions", instanceName));
        if (Volatile.Read(ref _disposed) != 0 || !Active.Comparer.Equals(_directory, directory))
            throw new InvalidOperationException("The installation session does not own this instance.");
    }

    internal static void ValidateInstanceName(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName) || instanceName is "." or ".."
            || instanceName.IndexOfAny(['/', '\\', ':']) >= 0
            || instanceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The instance name must be a single file name.", nameof(instanceName));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (Gate)
            Active.Remove(_directory);
    }
}
