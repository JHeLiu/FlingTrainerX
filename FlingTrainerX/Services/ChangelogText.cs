using System.IO;
using System.Text;

namespace FlingTrainerX.Services;

/// <summary>
/// 「更新内容」正文的读取器：内容整个放在 &lt;程序目录&gt;\Assets\changelog.txt。
/// <para>
/// 和 <see cref="DisclaimerText"/> 一样用纯文本（UTF-8 .txt）：正文只需要段落和换行，
/// 文本里怎么写屏幕上就怎么显示，发版前用记事本改一行就能换文案，不用重新编译。
/// </para>
/// <para>
/// 设置页「关于 · 更新内容」那一行只放首段，点「查看更新」看全文。
/// </para>
/// </summary>
public static class ChangelogText
{
    /// <summary>正文文件名。</summary>
    public const string FileName = "changelog.txt";

    /// <summary>正文的完整路径：&lt;程序目录&gt;\Assets\changelog.txt。</summary>
    public static string FilePath => AppPaths.AssetFile(FileName);

    /// <summary>
    /// 读整篇正文。换行统一成 \n（WPF 的文本控件两种都认，统一一下省得行高算错），
    /// 文件不存在或读不了时返回一句提示，弹窗不会空着。
    /// </summary>
    public static string Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return $"没有找到 Assets\\{FileName}，请检查程序目录是否完整。";

            var text = File.ReadAllText(FilePath, Encoding.UTF8)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim('\n');

            return text.Length > 0 ? text : $"Assets\\{FileName} 里还没有内容。";
        }
        catch (Exception ex)
        {
            return $"更新内容读取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 设置页那一行说明用的一句摘要：优先取第一条「- 」开头的条目，
    /// 没有条目就取第一段不是「v1.2.3」这种纯版本号的行。
    /// 太长会截断，别把右边的按钮挤下去。
    /// </summary>
    public static string LoadSummary()
    {
        var text = Load();

        var lines = text.Split('\n');
        var picked = FirstBullet(lines) ?? FirstNonVersionParagraph(lines) ?? "";

        picked = picked.Trim().TrimStart('-', '·', '*').Trim();
        if (picked.Length == 0) return "";

        const int max = 64;
        return picked.Length > max ? picked[..max].TrimEnd() + "…" : picked;
    }

    /// <summary>第一条以 - / · / * 开头的条目（去掉标记由调用方做）。</summary>
    private static string? FirstBullet(string[] lines)
    {
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length >= 2 && (t[0] is '-' or '·' or '*') && t[1] == ' ') return t;
        }

        return null;
    }

    /// <summary>第一段内容，跳过空行和「v1.2.3」这种纯版本号标题。</summary>
    private static string? FirstNonVersionParagraph(string[] lines)
    {
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;

            var plain = t.TrimStart('v', 'V');
            if (plain.Length > 0 && plain.All(c => char.IsDigit(c) || c == '.')) continue;

            return t;
        }

        return null;
    }
}
