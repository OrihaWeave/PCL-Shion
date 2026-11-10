using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Download;
using PCL.Shion.Core.Import;
using PCL.Shion.Core.Install;
using PCL.Shion.Core.Instances;
using PCL.Shion.Core.Loaders;
using PCL.Shion.Core.Minecraft;
using PCL.Shion.Core.Sources;
using PCL.Shion.Core.Test.Loaders.Fabric;
using PCL.Shion.Core.Versions;

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

    private async Task InstallAsync(string archive, ModpackPlan? plan = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var engine = new DownloadEngine(new DownloadOptions { RetryDelay = TimeSpan.Zero }, _server);
        await new ModpackInstaller(engine, DownloadSource.Official)
            .InstallAsync(archive, plan ?? ModpackReader.Read(archive), _root.RootPath, "My Pack", "java", progress, cancellationToken);
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

    private static void AddEntry(string archivePath, string name, string content)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    [TestMethod]
    [DataRow("PCL Shion/instance.json")]
    [DataRow("pcl SHION/INSTANCE.JSON")]
    [DataRow("./PCL Shion/instance.json")]
    [DataRow("PCL Shion\\instance.json")]
    [DataRow("PCL Shion./instance.json")]
    [DataRow("PCL Shion /instance.json")]
    [DataRow("PCL Shion/instance.json.bak")]
    [DataRow("PCL Shion")]
    [DataRow("PCL Shion/")]
    public async Task RejectsLauncherSettingsFromOverrides(string relativePath)
    {
        var archive = CreatePack(Convert.ToHexStringLower(SHA1.HashData(ModJar)));
        AddEntry(archive, "client-overrides/" + relativePath,
            """{"PRELAUNCHCOMMAND":"marker","postExitCommand":"marker","wrapperCommand":"marker","javaExecutable":"marker","environmentVariables":{"JAVA_TOOL_OPTIONS":"marker"}}""");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => InstallAsync(archive));

        Assert.IsFalse(Directory.Exists(_root.GetPath("versions/My Pack")));
    }

    [TestMethod]
    [DataRow("PCL Shion/instance.json")]
    [DataRow("./pcl SHION/instance.json.bak")]
    [DataRow("PCL Shion.\\instance.json")]
    public async Task RejectsLauncherSettingsFromDownloadListBeforeRequest(string path)
    {
        var archive = CreatePack(Convert.ToHexStringLower(SHA1.HashData(ModJar)));
        var plan = new ModpackPlan {
            Format = ModpackFormat.Modrinth, MinecraftVersion = "1.21.1", OverrideFolders = [],
            Files = [new ModpackFile(path, [new Uri(ModUrl)], null, null)]
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => InstallAsync(archive, plan));

        Assert.AreEqual(0, _server.Requests.Count);
        Assert.IsFalse(Directory.Exists(_root.GetPath("versions/My Pack")));
    }

    [TestMethod]
    public async Task ClientOverridesWinOverDownloadedAndCommonFiles()
    {
        var archive = CreatePack(Convert.ToHexStringLower(SHA1.HashData(ModJar)));
        AddEntry(archive, "overrides/mods/example.jar", "common override");
        AddEntry(archive, "client-overrides/mods/example.jar", "client override");

        await InstallAsync(archive);

        Assert.AreEqual("client override", File.ReadAllText(_root.GetPath("versions/My Pack/mods/example.jar")));
    }

    [TestMethod]
    public async Task OwnsInstallationBeforeAnyProgressAndHidesPackVersionJson()
    {
        var archive = CreatePack(Convert.ToHexStringLower(SHA1.HashData(ModJar)));
        AddEntry(archive, "overrides/My Pack.json", FabricTestData.InstalledVanillaJson);
        var progressObserved = false;
        var versionObserved = false;
        var progress = new InlineProgress(value => {
            if (value.Download is not null) return;
            progressObserved = true;
            Assert.ThrowsExactly<IOException>(() => InstallationSession.Begin(_root.RootPath, "My Pack"));
        });
        _server.OnRequest = _ => {
            if (!File.Exists(_root.GetPath("versions/My Pack/My Pack.json"))) return;
            versionObserved = true;
            Assert.IsFalse(InstanceScanner.Scan(_root.RootPath).Any(instance => instance.Name == "My Pack"));
        };

        await InstallAsync(archive, progress: progress);

        Assert.IsTrue(progressObserved);
        Assert.IsTrue(versionObserved);
        Assert.IsFalse(InstallationSession.IsActive(_root.GetPath("versions/My Pack")));
        var installed = InstanceScanner.Scan(_root.RootPath).Single(instance => instance.Name == "My Pack");
        Assert.AreEqual(installed.Directory, InstanceManager.GetGameDirectory(_root.RootPath, installed, InstanceStore.Load(_root.RootPath, "My Pack")));
    }

    [TestMethod]
    public async Task RefusesCompetingSessionWithoutCreatingOrCleaningItsDirectory()
    {
        var archive = CreatePack(Convert.ToHexStringLower(SHA1.HashData(ModJar)));
        using var owner = InstallationSession.Begin(_root.RootPath, "My Pack");

        await Assert.ThrowsExactlyAsync<IOException>(() => InstallAsync(archive));

        Assert.IsFalse(Directory.Exists(_root.GetPath("versions/My Pack")));
        _root.WriteFile("versions/My Pack/owner.txt", "owned by other installation");
        await Assert.ThrowsExactlyAsync<IOException>(() => InstallAsync(archive));
        Assert.AreEqual("owned by other installation", File.ReadAllText(_root.GetPath("versions/My Pack/owner.txt")));
    }

    [TestMethod]
    public async Task CancellationCleansOnlyNewInstanceAndReleasesSession()
    {
        var archive = CreatePack(Convert.ToHexStringLower(SHA1.HashData(ModJar)));
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancel.Cancel());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => InstallAsync(archive, progress: progress, cancellationToken: cancel.Token));

        Assert.IsFalse(Directory.Exists(_root.GetPath("versions/My Pack")));
        Assert.IsFalse(InstallationSession.IsActive(_root.GetPath("versions/My Pack")));
        Assert.IsTrue(File.Exists(_root.GetPath("versions/1.21.1/1.21.1.json")));
        await InstallAsync(archive);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreservesSupportedMcbbsSettingsWithoutImportingLauncherConfiguration(bool withLoader)
    {
        var archive = _root.GetPath("settings.zip");
        using (ZipFile.Open(archive, ZipArchiveMode.Create)) { }
        AddEntry(archive, "mcbbs.packmeta", $$"""
            { "addons": [ { "id": "game", "version": "1.21.1" } {{(withLoader ? ", { \"id\": \"fabric\", \"version\": \"0.16.9\" }" : "")}} ],
              "launchInfo": { "minMemory": 4096, "javaArgument": ["-Dreview=ok"], "launchArgument": ["--demo"] } }
            """);
        AddEntry(archive, "overrides/config/normal.txt", "normal");

        await InstallAsync(archive);

        var settings = InstanceStore.Load(_root.RootPath, "My Pack");
        Assert.IsTrue(settings.Isolated);
        Assert.AreEqual(4096, settings.MaxMemoryMb);
        Assert.AreEqual("-Dreview=ok", settings.ExtraJvmArguments);
        Assert.AreEqual("--demo", settings.ExtraGameArguments);
        Assert.IsNull(settings.PreLaunchCommand);
        Assert.IsNull(settings.PostExitCommand);
        Assert.IsNull(settings.WrapperCommand);
        Assert.IsNull(settings.JavaExecutable);
        Assert.AreEqual(0, settings.EnvironmentVariables.Count);
        Assert.AreEqual("normal", File.ReadAllText(_root.GetPath("versions/My Pack/config/normal.txt")));
        Assert.AreEqual(withLoader ? "fabric" : null, VersionResolver.Resolve(_root.RootPath, "My Pack").Loader);
        Assert.AreEqual("1.21.1", VersionResolver.Resolve(_root.RootPath, "My Pack").MinecraftVersion);
    }

    private sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NestedInstallersRejectWrongOrDisposedSession(bool disposed)
    {
        using var owner = InstallationSession.Begin(_root.RootPath, disposed ? "My Pack" : "Other Pack");
        if (disposed) owner.Dispose();
        using var engine = new DownloadEngine(new DownloadOptions { RetryDelay = TimeSpan.Zero }, _server);
        var version = new RemoteVersion("1.21.1", "release", DateTimeOffset.UtcNow, new Uri(FabricTestData.VanillaJsonUrl), null);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new GameInstaller(engine, DownloadSource.Official)
            .InstallVanillaInSessionAsync(_root.RootPath, version, "My Pack", owner, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new LoaderInstaller(engine, DownloadSource.Official)
            .InstallInSessionAsync(_root.RootPath, FabricTestData.FabricLoader, "My Pack", "java", owner, null, CancellationToken.None));

        Assert.AreEqual(0, _server.Requests.Count);
        Assert.IsFalse(Directory.Exists(_root.GetPath("versions/My Pack")));
    }
}
