using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Export;
using PCL.Shion.Core.Import;
using PCL.Shion.Core.Loaders;

namespace PCL.Shion.Core.Test.Import;

[TestClass]
public sealed class ModpackReaderTest
{
    private string _root = "";

    [TestInitialize]
    public void CreateFolder()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-shion-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteFolder()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private string CreateArchive(Dictionary<string, string> entries, Encoding? nameEncoding = null)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create, nameEncoding))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return path;
    }

    [TestMethod]
    public void ReadsModrinthPack()
    {
        var path = CreateArchive(new()
        {
            ["modrinth.index.json"] = """
                {
                  "formatVersion": 1, "game": "minecraft", "name": "Pack", "versionId": "2.0",
                  "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.11" },
                  "files": [
                    { "path": "mods/a.jar", "hashes": { "sha1": "ABCDEF" }, "downloads": ["https://cdn.modrinth.com/a.jar"], "fileSize": 12 },
                    { "path": "mods/server.jar", "env": { "client": "unsupported", "server": "required" }, "downloads": ["https://cdn.modrinth.com/s.jar"] }
                  ]
                }
                """,
            ["overrides/config/a.toml"] = "a"
        });

        var plan = ModpackReader.Read(path);

        Assert.AreEqual(ModpackFormat.Modrinth, plan.Format);
        Assert.AreEqual("Pack", plan.Name);
        Assert.AreEqual("2.0", plan.Version);
        Assert.AreEqual("1.20.1", plan.MinecraftVersion);
        Assert.AreEqual(new LoaderVersion(LoaderKind.Fabric, "0.15.11", "1.20.1", true), plan.Loader);
        CollectionAssert.AreEqual(new[] { "overrides/", "client-overrides/" }, plan.OverrideFolders.ToArray());
        Assert.AreEqual(1, plan.Files.Count);
        Assert.AreEqual("mods/a.jar", plan.Files[0].Path);
        Assert.AreEqual("abcdef", plan.Files[0].Sha1);
        Assert.AreEqual(12L, plan.Files[0].Size);
    }

    [TestMethod]
    public void RejectsModrinthPathTraversal()
    {
        var path = CreateArchive(new()
        {
            ["modrinth.index.json"] = """
                { "dependencies": { "minecraft": "1.20.1" },
                  "files": [ { "path": "../evil.jar", "downloads": ["https://cdn.modrinth.com/e.jar"] } ] }
                """
        });

        Assert.ThrowsExactly<InvalidDataException>(() => ModpackReader.Read(path));
    }

    [TestMethod]
    public void ReadsExportedMcbbsPack()
    {
        var overrides = Path.Combine(_root, "overrides");
        Directory.CreateDirectory(overrides);
        var meta = McbbsExport.BuildPackMeta(overrides, "整合包", "1.0.0", "1.20.1",
            forgeVersion: "47.2.0", optiFineVersion: "HD_U_I6");
        var path = CreateArchive(new() { ["mcbbs.packmeta"] = meta });

        var plan = ModpackReader.Read(path);

        Assert.AreEqual(ModpackFormat.Mcbbs, plan.Format);
        Assert.AreEqual("整合包", plan.Name);
        Assert.AreEqual(new LoaderVersion(LoaderKind.Forge, "47.2.0", "1.20.1", true), plan.Loader);
        CollectionAssert.AreEqual(new[] { "overrides/" }, plan.OverrideFolders.ToArray());
        Assert.AreEqual(1, plan.Issues.Count);
        Assert.AreEqual(new ModpackIssue(ModpackIssueKind.OptiFine, "HD_U_I6"), plan.Issues[0]);
    }

    [TestMethod]
    public void ReadsMcbbsLaunchInfoAndFullForgeVersion()
    {
        var path = CreateArchive(new()
        {
            ["manifest.json"] = """
                {
                  "manifestType": "minecraftModpack", "name": "Old",
                  "addons": [ { "id": "game", "version": "1.12.2" }, { "id": "forge", "version": "1.12.2-14.23.5.2860" } ],
                  "files": [ { "type": "curse", "projectID": 1, "fileID": 2, "fileName": "jei.jar" } ],
                  "launchInfo": { "minMemory": 4096, "javaArgument": ["-Dfoo=1", " -Dbar=2 "], "launchArgument": [] }
                }
                """
        });

        var plan = ModpackReader.Read(path);

        Assert.AreEqual(ModpackFormat.Mcbbs, plan.Format);
        Assert.AreEqual("14.23.5.2860", plan.Loader!.Version);
        Assert.AreEqual(4096, plan.MemoryMb);
        Assert.AreEqual("-Dfoo=1 -Dbar=2", plan.JvmArguments);
        Assert.IsNull(plan.GameArguments);
        Assert.AreEqual(new ModpackIssue(ModpackIssueKind.CurseForgeFile, "jei.jar"), plan.Issues.Single());
    }

    [TestMethod]
    public void RejectsCurseForgePack()
    {
        var path = CreateArchive(new()
        {
            ["manifest.json"] = """{ "minecraft": { "version": "1.20.1", "modLoaders": [] }, "files": [] }"""
        });

        Assert.ThrowsExactly<NotSupportedException>(() => ModpackReader.Read(path));
    }

    [TestMethod]
    public void ReadsExportedMultiMcPackInSubfolder()
    {
        var path = CreateArchive(new()
        {
            ["Pack/mmc-pack.json"] = MultiMcExport.BuildPackManifest("1.21.1", neoForgeVersion: "21.1.77"),
            ["Pack/instance.cfg"] = MultiMcExport.BuildInstanceConfig("我的整合包", "1.21.1")
                                    + "OverrideMemory=true\r\nMaxMemAlloc=6144\r\nOverrideJavaArgs=true\r\nJvmArgs=-XX:+UseG1GC\r\n",
            ["Pack/patches/custom.json"] = "{}",
            ["Pack/.minecraft/options.txt"] = "o"
        });

        var plan = ModpackReader.Read(path);

        Assert.AreEqual(ModpackFormat.MultiMc, plan.Format);
        Assert.AreEqual("我的整合包", plan.Name);
        Assert.AreEqual("1.21.1", plan.MinecraftVersion);
        Assert.AreEqual(new LoaderVersion(LoaderKind.NeoForge, "21.1.77", "1.21.1", true), plan.Loader);
        CollectionAssert.AreEqual(new[] { "Pack/minecraft/", "Pack/.minecraft/" }, plan.OverrideFolders.ToArray());
        Assert.AreEqual(6144, plan.MemoryMb);
        Assert.AreEqual("-XX:+UseG1GC", plan.JvmArguments);
        Assert.AreEqual(new ModpackIssue(ModpackIssueKind.JsonPatch, "custom.json"), plan.Issues.Single());
    }

    [TestMethod]
    public void ReportsUnsupportedMultiMcComponents()
    {
        var path = CreateArchive(new()
        {
            ["mmc-pack.json"] = """
                { "formatVersion": 1, "components": [
                  { "uid": "org.lwjgl3", "version": "3.3.1" },
                  { "uid": "net.minecraft", "version": "1.12.2" },
                  { "uid": "net.minecraftforge", "version": "0.2.4-alpha" },
                  { "uid": "com.mumfrey.liteloader", "version": "1.12.2-SNAPSHOT", "cachedName": "LiteLoader" }
                ] }
                """
        });

        var plan = ModpackReader.Read(path);

        Assert.IsNull(plan.Loader);
        CollectionAssert.AreEqual(new[]
        {
            new ModpackIssue(ModpackIssueKind.UnsupportedComponent, "Cleanroom 0.2.4-alpha"),
            new ModpackIssue(ModpackIssueKind.UnsupportedComponent, "LiteLoader 1.12.2-SNAPSHOT")
        }, plan.Issues.ToArray());
    }

    [TestMethod]
    public void RejectsUnknownArchive()
    {
        var path = CreateArchive(new() { ["readme.txt"] = "hi" });

        Assert.ThrowsExactly<InvalidDataException>(() => ModpackReader.Read(path));
    }

    [TestMethod]
    public void DecodesGbkFileNames()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = CreateArchive(new()
        {
            ["mcbbs.packmeta"] = """{ "addons": [ { "id": "game", "version": "1.20.1" } ] }""",
            ["overrides/资源包/说明.txt"] = "x"
        }, Encoding.GetEncoding("GB18030"));

        using var archive = ModpackReader.OpenArchive(path);

        Assert.IsTrue(archive.Entries.Any(entry => entry.FullName == "overrides/资源包/说明.txt"));
    }

    [TestMethod]
    public void ExtractsOverridesInOrder()
    {
        var path = CreateArchive(new()
        {
            ["overrides/options.txt"] = "common",
            ["overrides/mods/a.jar"] = "a",
            ["client-overrides/options.txt"] = "client",
            ["other/ignored.txt"] = "x"
        });
        var target = Path.Combine(_root, "instance");

        using (var archive = ModpackReader.OpenArchive(path))
            ModpackInstaller.ExtractOverrides(archive, ["overrides/", "client-overrides/"], target, CancellationToken.None);

        Assert.AreEqual("client", File.ReadAllText(Path.Combine(target, "options.txt")));
        Assert.AreEqual("a", File.ReadAllText(Path.Combine(target, "mods", "a.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "ignored.txt")));
    }

    [TestMethod]
    public void RejectsOverridesThatEscapeInstance()
    {
        var path = CreateArchive(new() { ["overrides/../../evil.txt"] = "x" });
        var target = Path.Combine(_root, "instance");

        using var archive = ModpackReader.OpenArchive(path);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ModpackInstaller.ExtractOverrides(archive, ["overrides/"], target, CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [TestMethod]
    public async Task InstallRefusesExistingInstanceWithoutTouchingIt()
    {
        var path = CreateArchive(new() { ["mcbbs.packmeta"] = """{ "addons": [ { "id": "game", "version": "1.20.1" } ] }""" });
        var existing = Path.Combine(_root, "mc", "versions", "Pack");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
        using var engine = new Download.DownloadEngine();

        await Assert.ThrowsExactlyAsync<IOException>(() => new ModpackInstaller(engine, Sources.DownloadSource.Official)
            .InstallAsync(path, ModpackReader.Read(path), Path.Combine(_root, "mc"), "Pack", "java", null, CancellationToken.None));
        Assert.IsTrue(File.Exists(Path.Combine(existing, "keep.txt")));
    }
}
