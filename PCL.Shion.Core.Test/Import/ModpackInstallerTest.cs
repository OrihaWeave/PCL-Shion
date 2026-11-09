using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Download;
using PCL.Shion.Core.Import;
using PCL.Shion.Core.Instances;
using PCL.Shion.Core.Minecraft;
using PCL.Shion.Core.Sources;
using PCL.Shion.Core.Test.Loaders.Fabric;

namespace PCL.Shion.Core.Test.Import;

[TestClass]
public sealed class ModpackInstallerTest
{
    private const string ModUrl = "https://cdn.modrinth.com/data/abc/versions/1/example.jar";
    private static readonly byte[] ModJar = "example mod"u8.ToArray();

    private FakeMinecraftRoot _root = null!;
    private FakeMetaHandler _server = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = new FakeMinecraftRoot();
        _server = FabricTestData.CreateServer();
        _server.Add(ModUrl, ModJar);
        _root.WriteVersion(FabricTestData.MinecraftVersion, FabricTestData.InstalledVanillaJson);
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        _root.Dispose();
    }

    private string CreatePack(string sha1)
    {
        var path = _root.GetPath("pack.mrpack");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Write("modrinth.index.json", $$"""
            {
              "formatVersion": 1, "game": "minecraft", "name": "Pack", "versionId": "1.0",
              "dependencies": { "minecraft": "1.21.1", "fabric-loader": "0.16.9" },
              "files": [ { "path": "mods/example.jar", "hashes": { "sha1": "{{sha1}}" }, "downloads": ["{{ModUrl}}"] } ]
            }
            """);
        Write("overrides/config/example.toml", "enabled = true");
        Write("client-overrides/options.txt", "lang:zh_cn");
        return path;
    }

    private async Task InstallAsync(string archive)
    {
        using var engine = new DownloadEngine(new DownloadOptions { RetryDelay = TimeSpan.Zero }, _server);
        await new ModpackInstaller(engine, DownloadSource.Official)
            .InstallAsync(archive, ModpackReader.Read(archive), _root.RootPath, "My Pack", "java", null, CancellationToken.None);
    }

    [TestMethod]
    public async Task InstallsModrinthPackAsIsolatedFabricInstance()
    {
        var archive = CreatePack(Convert.ToHexString(SHA1.HashData(ModJar)).ToLowerInvariant());

        await InstallAsync(archive);

        var version = VersionResolver.Resolve(_root.RootPath, "My Pack");
        Assert.AreEqual("fabric", version.Loader);
        Assert.AreEqual("1.21.1", version.MinecraftVersion);
        CollectionAssert.AreEqual(ModJar, File.ReadAllBytes(_root.GetPath("versions/My Pack/mods/example.jar")));
        Assert.AreEqual("enabled = true", File.ReadAllText(_root.GetPath("versions/My Pack/config/example.toml")));
        Assert.AreEqual("lang:zh_cn", File.ReadAllText(_root.GetPath("versions/My Pack/options.txt")));
        Assert.IsTrue(InstanceStore.Load(_root.RootPath, "My Pack").Isolated);
    }

    [TestMethod]
    public async Task RemovesInstanceWhenADownloadFails()
    {
        var archive = CreatePack(new string('0', 40));

        await Assert.ThrowsExactlyAsync<PCL.Shion.Core.Install.InstallException>(() => InstallAsync(archive));

        Assert.IsFalse(Directory.Exists(_root.GetPath("versions/My Pack")));
        Assert.IsTrue(File.Exists(_root.GetPath("versions/1.21.1/1.21.1.json")));
    }
}
