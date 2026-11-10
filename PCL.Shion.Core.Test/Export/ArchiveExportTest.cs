using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Export;

namespace PCL.Shion.Core.Test.Export;

[TestClass]
public sealed class ArchiveExportTest
{
    private string _root = "";

    [TestInitialize]
    public void CreateFolder() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "shion-export-" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(_root, true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExportsVanillaWithoutSelectedFiles(bool multiMc)
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "options.txt"), "not selected");
        var pack = Path.Combine(_root, "pack");
        var overrides = Path.Combine(pack, multiMc ? ".minecraft" : "overrides");
        ModpackExporter.CopyOverrides(game, overrides, [], false);
        var metadata = multiMc ? "mmc-pack.json" : "mcbbs.packmeta";
        File.WriteAllText(Path.Combine(pack, metadata), multiMc
            ? MultiMcExport.BuildPackManifest("1.21.1")
            : McbbsExport.BuildPackMeta(overrides, "Vanilla", "1", "1.21.1"));
        var destination = Path.Combine(_root, "output.zip");

        ModpackExporter.CreateArchive(pack, destination);

        using var zip = ZipFile.OpenRead(destination);
        Assert.IsNotNull(zip.GetEntry(metadata));
        Assert.IsFalse(zip.Entries.Any(entry => entry.FullName.EndsWith("options.txt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FailedCompressionPreservesExistingArchiveAndCleansTemporaryFile()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var lockedFile = Path.Combine(source, "locked.txt");
        File.WriteAllText(lockedFile, "content");
        var destination = Path.Combine(_root, "output.zip");
        File.WriteAllText(destination, "previous archive");
        using var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsExactly<IOException>(() => ModpackExporter.CreateArchive(source, destination));

        Assert.AreEqual("previous archive", File.ReadAllText(destination));
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length);
    }

    [TestMethod]
    public void ReplacesExistingArchiveOnlyAfterSuccessfulCompression()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "content.txt"), "new content");
        var destination = Path.Combine(_root, "output.zip");
        File.WriteAllText(destination, "previous archive");

        ModpackExporter.CreateArchive(source, destination);

        using var zip = ZipFile.OpenRead(destination);
        using var reader = new StreamReader(zip.GetEntry("content.txt")!.Open());
        Assert.AreEqual("new content", reader.ReadToEnd());
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length);
    }
}
