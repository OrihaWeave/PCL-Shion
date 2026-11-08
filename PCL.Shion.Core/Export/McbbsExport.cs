using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Shion.Core.Export;

/// <summary>
/// MCBBS 整合包格式（<c>mcbbs.packmeta</c>）的导出内容生成。
/// 该格式由 MCBBS 论坛整合包规范定义，文件放在 <c>overrides</c> 文件夹中，并附带 SHA-1 清单。
/// </summary>
public static class McbbsExport
{
    /// <summary>
    /// 生成 <c>mcbbs.packmeta</c> 的内容。
    /// </summary>
    /// <param name="overridesFolder">准备打包到 <c>overrides</c> 的文件夹，用于生成文件清单。</param>
    /// <param name="name">整合包名称。</param>
    /// <param name="version">整合包版本。</param>
    /// <param name="minecraftVersion">原版游戏版本。</param>
    /// <param name="description">整合包简介。</param>
    /// <param name="author">整合包作者。</param>
    public static string BuildPackMeta(string overridesFolder, string name, string version, string minecraftVersion,
        string? forgeVersion = null, string? neoForgeVersion = null, string? fabricVersion = null,
        string? quiltVersion = null, string? optiFineVersion = null, string description = "", string author = "")
    {
        var addons = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "game",
                ["version"] = minecraftVersion
            }
        };

        void AddAddon(string id, string? addonVersion)
        {
            if (string.IsNullOrWhiteSpace(addonVersion))
                return;
            addons.Add(new JsonObject
            {
                ["id"] = id,
                ["version"] = addonVersion
            });
        }

        AddAddon("forge", forgeVersion);
        AddAddon("neoforge", neoForgeVersion);
        AddAddon("fabric", fabricVersion);
        AddAddon("quilt", quiltVersion);
        AddAddon("optifine", optiFineVersion);

        var files = new JsonArray();
        if (Directory.Exists(overridesFolder))
        {
            foreach (var path in Directory.EnumerateFiles(overridesFolder, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                using var sha1 = SHA1.Create();
                files.Add(new JsonObject
                {
                    ["type"] = "addon",
                    ["force"] = true,
                    ["path"] = Path.GetRelativePath(overridesFolder, path).Replace('\\', '/'),
                    ["hash"] = Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant()
                });
            }
        }

        var result = new JsonObject
        {
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 2,
            ["name"] = name,
            ["version"] = version,
            ["author"] = author,
            ["description"] = description,
            ["url"] = "",
            ["forceUpdate"] = false,
            ["origin"] = new JsonArray(),
            ["addons"] = addons,
            ["libraries"] = new JsonArray(),
            ["files"] = files,
            ["settings"] = new JsonObject
            {
                ["install_mods"] = true,
                ["install_resourcepack"] = true
            },
            ["launchInfo"] = new JsonObject
            {
                ["minMemory"] = 0,
                ["supportJava"] = new JsonArray(),
                ["launchArgument"] = new JsonArray(),
                ["javaArgument"] = new JsonArray()
            }
        };
        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
