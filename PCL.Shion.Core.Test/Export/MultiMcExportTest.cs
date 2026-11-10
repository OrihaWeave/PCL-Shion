using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Export;

namespace PCL.Shion.Core.Test.Export;

[TestClass]
public sealed class MultiMcExportTest
{
    [TestMethod]
    public void BuildsComponentsInOrder()
    {
        var json = MultiMcExport.BuildPackManifest("1.20.1", forgeVersion: "47.2.0", fabricVersion: "0.15.11");
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.AreEqual(1, root["formatVersion"]!.GetValue<int>());
        var components = root["components"]!.AsArray();
        Assert.AreEqual(3, components.Count);
        Assert.AreEqual("net.minecraft", components[0]!["uid"]!.GetValue<string>());
        Assert.AreEqual("1.20.1", components[0]!["version"]!.GetValue<string>());
        Assert.IsTrue(components[0]!["important"]!.GetValue<bool>());
        Assert.AreEqual("net.minecraftforge", components[1]!["uid"]!.GetValue<string>());
        Assert.AreEqual("47.2.0", components[1]!["version"]!.GetValue<string>());
        Assert.AreEqual("net.fabricmc.fabric-loader", components[2]!["uid"]!.GetValue<string>());
        Assert.AreEqual("0.15.11", components[2]!["version"]!.GetValue<string>());
    }

    [TestMethod]
    public void SkipsEmptyLoaderVersions()
    {
        var components = JsonNode.Parse(MultiMcExport.BuildPackManifest("1.21.1", forgeVersion: " ",
            neoForgeVersion: "21.1.77"))!["components"]!.AsArray();

        Assert.AreEqual(2, components.Count);
        Assert.AreEqual("net.neoforged", components[1]!["uid"]!.GetValue<string>());
    }

    [TestMethod]
    public void WritesInstanceConfigWithoutLineBreaks()
    {
        var config = MultiMcExport.BuildInstanceConfig("测试\r\n整合包", "1.20.1");

        Assert.AreNotEqual(-1, config.IndexOf("InstanceType=OneSix", StringComparison.Ordinal));
        Assert.AreNotEqual(-1, config.IndexOf("name=测试  整合包", StringComparison.Ordinal));
        Assert.AreNotEqual(-1, config.IndexOf("IntendedVersion=1.20.1", StringComparison.Ordinal));
        Assert.AreEqual(5, config.Split("\r\n").Length);
    }
}
