using PCL.Shion.Core.Loaders;

namespace PCL.Shion.Core.Import;

/// <summary>
/// 整合包的格式。
/// </summary>
public enum ModpackFormat
{
    /// <summary>Modrinth 整合包（<c>modrinth.index.json</c>）。</summary>
    Modrinth,

    /// <summary>MCBBS 整合包（<c>mcbbs.packmeta</c>，或带 <c>addons</c> 的 <c>manifest.json</c>）。</summary>
    Mcbbs,

    /// <summary>MultiMC / Prism 实例（<c>mmc-pack.json</c>）。</summary>
    MultiMc
}

/// <summary>
/// 整合包中需要联网下载的文件。
/// </summary>
/// <param name="Path">相对于实例游戏目录的路径，使用 <c>/</c> 分隔。</param>
/// <param name="Urls">按优先顺序排列的下载地址。</param>
/// <param name="Sha1">小写十六进制的 SHA-1，没有时为 null。</param>
/// <param name="Size">文件大小，没有时为 null。</param>
public sealed record ModpackFile(string Path, IReadOnlyList<Uri> Urls, string? Sha1, long? Size);

/// <summary>
/// 无法自动处理的内容的种类。
/// </summary>
public enum ModpackIssueKind
{
    /// <summary>OptiFine：启动器尚不能自动安装，需要导入后手动添加。</summary>
    OptiFine,

    /// <summary>启动器不支持的组件或加载器，如 LiteLoader、Cleanroom。</summary>
    UnsupportedComponent,

    /// <summary>MultiMC 的 JSON Patch，会被忽略。</summary>
    JsonPatch,

    /// <summary>需要通过 CurseForge 项目编号下载的文件，会被跳过。</summary>
    CurseForgeFile
}

/// <summary>
/// 一项无法自动处理的内容。
/// </summary>
/// <param name="Kind">种类。</param>
/// <param name="Detail">具体内容，如组件名称与版本、补丁文件名。</param>
public sealed record ModpackIssue(ModpackIssueKind Kind, string Detail);

/// <summary>
/// 从整合包读出的安装计划。只描述要做什么，不修改任何文件。
/// </summary>
public sealed class ModpackPlan
{
    /// <summary>整合包格式。</summary>
    public required ModpackFormat Format { get; init; }

    /// <summary>整合包名称，没有时为 null。</summary>
    public string? Name { get; init; }

    /// <summary>整合包版本，没有时为 null。</summary>
    public string? Version { get; init; }

    /// <summary>原版 Minecraft 版本号。</summary>
    public required string MinecraftVersion { get; init; }

    /// <summary>要安装的模组加载器，纯原版时为 null。</summary>
    public LoaderVersion? Loader { get; init; }

    /// <summary>
    /// 要复制到实例游戏目录的压缩包文件夹，按顺序复制，后复制的覆盖先复制的。每项以 <c>/</c> 结尾。
    /// </summary>
    public required IReadOnlyList<string> OverrideFolders { get; init; }

    /// <summary>需要联网下载的文件。</summary>
    public IReadOnlyList<ModpackFile> Files { get; init; } = [];

    /// <summary>追加的 JVM 参数，没有时为 null。</summary>
    public string? JvmArguments { get; init; }

    /// <summary>追加的游戏参数，没有时为 null。</summary>
    public string? GameArguments { get; init; }

    /// <summary>建议分配的最大内存（MB），没有时为 null。</summary>
    public int? MemoryMb { get; init; }

    /// <summary>
    /// 无法自动处理的内容，例如 OptiFine 或 MultiMC 的 JSON Patches。导入前应展示给用户确认。
    /// </summary>
    public IReadOnlyList<ModpackIssue> Issues { get; init; } = [];
}
