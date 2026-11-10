using System.Text;

namespace PCL.Shion.Core.Online;

/// <summary>
/// 资源文件的命名与存放位置。
/// </summary>
public static class CompNaming
{
    /// <summary>
    /// 资源种类在实例中对应的子文件夹，没有则为空字符串。
    /// </summary>
    public static string GetSubFolder(CompType type) => type switch
    {
        CompType.Mod => "mods",
        CompType.ResourcePack => "resourcepacks",
        CompType.Shader => "shaderpacks",
        CompType.World => "saves",
        _ => ""
    };

    /// <summary>
    /// 生成资源文件的保存名。若有中文名，按 <paramref name="nameFormat"/> 组合中文名与原文件名：
    /// 0 为【中文】原名，1 为 [中文] 原名，2 为 中文-原名，3 为 原名-中文，其他为原名。
    /// </summary>
    /// <param name="project">工程。</param>
    /// <param name="file">文件。</param>
    /// <param name="chineseName">中文名，为 null 或空时不使用。</param>
    /// <param name="nameFormat">命名格式。</param>
    public static string GetFileName(CompProject project, CompFile file, string? chineseName = null, int nameFormat = 0)
    {
        string fileName;
        if (string.IsNullOrEmpty(chineseName) || chineseName == project.RawName)
        {
            fileName = file.FileName;
        }
        else
        {
            fileName = Combine(file.FileName, SanitizeChineseName(chineseName), nameFormat);
        }

        if (file.Type == CompType.Mod)
            fileName = fileName.Replace("~", "-");
        return SanitizeFileName(fileName);
    }

    /// <summary>
    /// 按命名格式组合原文件名与中文名。格式 3 把中文名放在扩展名之前（xxx-中文名.jar），
    /// 否则扩展名会被夹在中间，保存出的 Mod 无法被游戏识别（对应上游 PCL-CE #3680）。
    /// </summary>
    internal static string Combine(string? fileName, string cleanName, int nameFormat)
    {
        fileName ??= "";
        switch (nameFormat)
        {
            case 0:
                return $"【{cleanName}】{fileName}";
            case 1:
                return $"[{cleanName}] {fileName}";
            case 2:
                return $"{cleanName}-{fileName}";
            case 3:
            {
                var extension = Path.GetExtension(fileName);
                var stem = fileName[..^extension.Length];
                return string.IsNullOrWhiteSpace(stem) ? cleanName + extension : $"{stem}-{cleanName}{extension}";
            }
            default:
                return fileName;
        }
    }

    /// <summary>
    /// 把文件名中的非法字符替换为全角字符或下划线，并去掉首尾空白。
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "download";

        var sanitized = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
        {
            sanitized.Append(c switch
            {
                '\\' => '＼',
                '/' => '／',
                ':' => '：',
                '*' => '＊',
                '?' => '？',
                '"' => '＂',
                '<' => '＜',
                '>' => '＞',
                '|' => '｜',
                _ when char.IsControl(c) => '_',
                _ => c
            });
        }

        var result = sanitized.ToString().Trim();
        return result is "" or "." or ".." ? "download" : result;
    }

    private static string SanitizeChineseName(string name)
    {
        var first = name;
        var parenthesis = first.IndexOf(" (", StringComparison.Ordinal);
        if (parenthesis >= 0) first = first[..parenthesis];
        var dash = first.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0) first = first[..dash];
        return first.Replace(@"\", "＼")
            .Replace("/", "／").Replace("|", "｜").Replace(":", "：").Replace("<", "＜").Replace(">", "＞")
            .Replace("*", "＊").Replace("?", "？").Replace("\"", "").Replace("： ", "：");
    }
}
