using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Export;

namespace PCL.Shion.Core.Test.Export;

[TestClass]
public sealed class McbbsExportTest
{
    private string _root = "";

    [TestInitialize]
    public void CreateFolder()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-shion-mcbbs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "example.jar"), "mod");
        File.WriteAllText(Path.Combine(_root, "options.txt"), "options");
    }

    [TestCleanup]
    public void DeleteFolder()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [TestMethod]
    public void BuildsMcbbsManifest()
    {
        var json = McbbsExport.BuildPackMeta(_root, "整合包", "1.0.0", "1.20.1",
            forgeVersion: "47.2.0", fabricVersion: "0.15.11", description: "简介", author: "作者");
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.AreEqual("minecraftModpack", root["manifestType"]!.GetValue<string>());
        Assert.AreEqual(2, root["manifestVersion"]!.GetValue<int>());
        Assert.AreEqual("整合包", root["name"]!.GetValue<string>());
        Assert.AreEqual("1.0.0", root["version"]!.GetValue<string>());
        Assert.AreEqual("简介", root["description"]!.GetValue<string>());
        Assert.AreEqual("作者", root["author"]!.GetValue<string>());

        var addons = root["addons"]!.AsArray();
        Assert.AreEqual(3, addons.Count);
        Assert.AreEqual("game", addons[0]!["id"]!.GetValue<string>());
        Assert.AreEqual("1.20.1", addons[0]!["version"]!.GetValue<string>());
        Assert.AreEqual("forge", addons[1]!["id"]!.GetValue<string>());
        Assert.AreEqual("47.2.0", addons[1]!["version"]!.GetValue<string>());
        Assert.AreEqual("fabric", addons[2]!["id"]!.GetValue<string>());
    }

    [TestMethod]
    public void ListsOverridesWithSha1()
    {
        var files = JsonNode.Parse(McbbsExport.BuildPackMeta(_root, "Pack", "1.0.0", "1.20.1"))!["files"]!.AsArray();

        Assert.AreEqual(2, files.Count);
        var paths = files.Select(file => file!["path"]!.GetValue<string>()).ToList();
        CollectionAssert.AreEqual(new[] { "mods/example.jar", "options.txt" }, paths);
        foreach (var file in files)
        {
            Assert.AreEqual("addon", file!["type"]!.GetValue<string>());
            Assert.IsTrue(file["force"]!.GetValue<bool>());
            Assert.AreEqual(40, file["hash"]!.GetValue<string>().Length);
        }
    }
}
