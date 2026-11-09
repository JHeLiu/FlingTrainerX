using System.IO;
using System.Text;

namespace FlingTrainerX.Services;

/// <summary>
/// 免责声明正文的读取器：内容整个放在 &lt;程序目录&gt;\Assets\免责声明.txt，
/// 打开「关于」时读一遍，原样交给界面显示。
/// <para>
/// 用纯文本（UTF-8 .txt）而不是 Markdown / HTML：正文只需要段落和换行，
/// 文本里怎么写，屏幕上就怎么显示 —— 多空一行就是多一个空行，不需要任何标记，
/// 改文案用记事本就能改，也不会因为写错标记把排版弄乱。
/// </para>
/// </summary>
public static class DisclaimerText
{
    /// <summary>正文文件名。</summary>
    public const string FileName = "免责声明.txt";

    /// <summary>正文的完整路径：&lt;程序目录&gt;\Assets\免责声明.txt。</summary>
    public static string FilePath => AppPaths.AssetFile(FileName);

    /// <summary>
    /// 读整篇正文。换行统一成 \n（WPF 的 TextBlock 两种都认，统一一下省得行高算错），
    /// 文件不存在或读不了时返回一句提示，弹窗不会空着。
    /// </summary>
    public static string Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return "没有找到 Assets\\免责声明.txt，请检查程序目录是否完整。";

            var text = File.ReadAllText(FilePath, Encoding.UTF8)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim('\n');

            return text.Length > 0 ? text : "免责声明内容为空，请检查 Assets\\免责声明.txt。";
        }
        catch (Exception ex)
        {
            return $"免责声明读取失败：{ex.Message}";
        }
    }
}
