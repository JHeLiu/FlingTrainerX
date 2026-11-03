namespace FlingTrainerX.ViewModels;

/// <summary>
/// 「软件截图」选项卡里的一张图。<see cref="Source"/> 优先是 f\&lt;id&gt;\ 下的本地文件，
/// 还没落盘时才用官网地址；<see cref="Url"/> 始终保留原始链接，点开时兜底用。
/// </summary>
public sealed class ShotVm
{
    public ShotVm(int index, string url, string source)
    {
        Index = index;
        Url = url;
        Source = source;
    }

    public int Index { get; }
    public string Url { get; }
    public string Source { get; }

    public string Caption => $"截图 {Index + 1:00}";
}
