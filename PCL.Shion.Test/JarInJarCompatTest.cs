using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Shion.Test;

[TestClass]
public sealed class JarInJarCompatTest
{
    private static byte[] Jar(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }

        return buffer.ToArray();
    }

    private static (string, byte[]) Text(string name, string text) => (name, Encoding.UTF8.GetBytes(text));

    [TestMethod]
    public void EmbeddedModsFollowTheHostAndSatisfyDependencies()
    {
        var directory = Directory.CreateTempSubdirectory("shion-jij-");
        try
        {
            var host = Path.Combine(directory.FullName, "fabric-api.jar");
            File.WriteAllBytes(host, Jar(
                Text("fabric.mod.json", """{ "id": "fabric-api", "version": "0.100" }"""),
                ("META-INF/jars/base.jar", Jar(Text("fabric.mod.json",
                    """{ "id": "fabric-api-base", "version": "0.4", "depends": { "minecraft": "1.21.x" } }""")))));
            var user = Path.Combine(directory.FullName, "user.jar");
            File.WriteAllBytes(user, Jar(Text("fabric.mod.json",
                """{ "id": "user", "depends": { "fabric-api-base": "*" } }""")));

            var hostFile = new ModLocalComp.LocalCompFile(host, true);
            var userFile = new ModLocalComp.LocalCompFile(user, true);
            var embedded = hostFile.EmbeddedMods!.Single();
            Assert.AreEqual("fabric-api-base", embedded.ModId);
            Assert.AreEqual("0.4", embedded.Version);
            Assert.AreEqual("fabric", embedded.JijLoader);
            Assert.AreEqual("1.21.x", embedded.JijTargetMcVersion);
            Assert.AreSame(hostFile, embedded.Host);

            var index = new ModJarInJarIndex([hostFile, userFile], "1.21.1");
            var dependency = ModJarInJarIndex.BuildOwnDependencies(userFile, null).Single();
            Assert.AreEqual(JijDepStatus.Bundled, index.Analyze(userFile, dependency));

            // 外层文件被禁用后，内嵌的 Mod 也不再加载
            File.Move(host, host + ".disabled");
            var disabledHost = new ModLocalComp.LocalCompFile(host + ".disabled", true);
            Assert.AreEqual(ModLocalComp.LocalCompFile.LocalFileStatus.Disabled, disabledHost.EmbeddedMods!.Single().State);
            index = new ModJarInJarIndex([disabledHost, userFile], "1.21.1");
            Assert.AreEqual(JijDepStatus.Disabled, index.Analyze(userFile, dependency));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
