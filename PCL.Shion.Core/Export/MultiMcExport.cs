using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Shion.Core.Export;

/// <summary>
/// MultiMC / Prism 实例格式的导出内容生成。
/// 导出包在根目录放置 <c>instance.cfg</c>、<c>mmc-pack.json</c> 与 <c>.minecraft</c> 文件夹。
/// </summary>
public static class MultiMcExport
{
    /// <summary>
    /// 生成 <c>mmc-pack.json</c> 的内容。
    /// 组件 UID 与版本号需要能被 MultiMC 的元数据服务识别；无法确定的组件应传入 <see langword="null"/> 而不是占位文本。
    /// </summary>
    public static string BuildPackManifest(string minecraftVersion, string? forgeVersion = null,
        string? neoForgeVersion = null, string? fabricVersion = null, string? quiltVersion = null,
        string? optiFineVersion = null)
    {
        var components = new JsonArray
        {
            new JsonObject
            {
                ["uid"] = "net.minecraft",
                ["version"] = minecraftVersion,
                ["important"] = true
            }
        };

        void AddComponent(string uid, string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return;
            components.Add(new JsonObject
            {
                ["uid"] = uid,
                ["version"] = version
            });
        }

        AddComponent("net.minecraftforge", forgeVersion);
        AddComponent("net.neoforged", neoForgeVersion);
        AddComponent("net.fabricmc.fabric-loader", fabricVersion);
        AddComponent("org.quiltmc.quilt-loader", quiltVersion);
        AddComponent("optifine.OptiFine", optiFineVersion);

        var result = new JsonObject
        {
            ["formatVersion"] = 1,
            ["components"] = components
        };
        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 生成 <c>instance.cfg</c> 的内容。仅写入 MultiMC 识别实例所必需的字段，避免把 PCL 的启动设置带进其他启动器。
    /// </summary>
    public static string BuildInstanceConfig(string name, string minecraftVersion)
    {
        // INI 值不能包含换行，否则会把一行拆成无法解析的多行。
        var safeName = name.Replace("\r", " ").Replace("\n", " ");
        return string.Join("\r\n",
            "InstanceType=OneSix",
            $"name={safeName}",
            $"IntendedVersion={minecraftVersion}",
            "iconKey=default",
            "");
    }
}
