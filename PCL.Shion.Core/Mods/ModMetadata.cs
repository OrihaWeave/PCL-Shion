namespace PCL.Shion.Core.Mods;

/// <summary>
/// 从 jar 内的元数据文件读出的信息。
/// </summary>
internal sealed record ModMetadata(
    string? Id,
    string? Name,
    string? Version,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<ModDependency> Dependencies,
    IReadOnlyList<string> Provides)
{
    public static ModMetadata Empty { get; } = new(null, null, null, [], [], []);

    /// <summary>内嵌（Jar-in-Jar）的 jar 及其元数据，按在压缩包中的顺序排列。</summary>
    public IReadOnlyList<NestedMod> Nested { get; init; } = [];
}

/// <summary>内嵌在另一个 jar 中的 Mod。</summary>
/// <param name="EntryName">在外层 jar 中的路径，例如 META-INF/jars/fabric-api-base.jar。</param>
/// <param name="Metadata">内嵌 jar 的元数据。</param>
internal sealed record NestedMod(string EntryName, ModMetadata Metadata);
