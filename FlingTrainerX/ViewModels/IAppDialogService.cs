using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>状态指示灯的三档颜色（界面用 DataTrigger 换成画笔，VM 不碰 Brush）。</summary>
public enum StatusLevel
{
    /// <summary>中性（默认灰蓝）。</summary>
    Idle,

    /// <summary>成功（绿）。</summary>
    Success,

    /// <summary>提示 / 警告（强调色）。</summary>
    Attention
}

/// <summary>
/// 底部状态栏的写入口。各页 ViewModel 只往这里写一句人话，
/// 具体哪个 TextBlock 显示由外壳 VM 转成绑定。
/// </summary>
public interface IStatusSink
{
    /// <summary>换一句状态栏文字。</summary>
    /// <param name="text">要说的话。</param>
    /// <param name="level">指示灯颜色；不传就保持当前档位。</param>
    void Report(string text, StatusLevel? level = null);
}

/// <summary>
/// 需要窗口才能做的事：弹确认框、开「关于 / 打赏 / 更新 / 详情」这些浮层、滚动列表、打开浏览器。
/// <para>
/// ViewModel 只依赖这个接口，不引用 <c>MainWindow</c>；实现由窗口交给
/// <c>Views/AppOverlays</c> 完成（那里面才碰控件、播动画）。
/// </para>
/// </summary>
public interface IAppDialogService
{
    /// <summary>弹一个确认框，等用户点确定 / 取消。</summary>
    /// <param name="title">标题。</param>
    /// <param name="body">正文。</param>
    /// <param name="okText">确定按钮上的字。</param>
    /// <param name="primary">确定按钮用主色（普通操作）；false = 警示色（删除这类不可逆操作）。</param>
    Task<bool> ConfirmAsync(string title, string body, string okText, bool primary = false);

    /// <summary>带一个可选项的确认框（例如「顺便更新 Steam 中文名」）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="body">正文。</param>
    /// <param name="okText">确定按钮上的字。</param>
    /// <param name="optionText">可选项主文案。</param>
    /// <param name="optionHint">可选项下方的小字。</param>
    Task<(bool Ok, bool Checked)> ConfirmWithOptionAsync(
        string title, string body, string okText, string optionText, string optionHint);

    /// <summary>只有一句提示、一个「知道了」按钮的弹窗，等用户看完关掉才返回。</summary>
    /// <param name="title">标题。</param>
    /// <param name="body">正文。</param>
    /// <param name="okText">按钮文案。</param>
    Task NotifyAsync(string title, string body, string okText = "知道了");

    /// <summary>打开「关于 / 免责声明」。</summary>
    void ShowAbout();

    /// <summary>收起「关于」浮层。</summary>
    void CloseAbout();

    /// <summary>打开「灵石结缘」。</summary>
    void ShowSupport();

    /// <summary>收起「灵石结缘」浮层。</summary>
    void CloseSupport();

    /// <summary>打开更新说明 / 发现新版本弹窗。</summary>
    /// <param name="release">发行版。</param>
    /// <param name="prompt">true = 带「立即更新」；false = 只看说明。</param>
    void ShowUpdate(UpdateRelease release, bool prompt);

    /// <summary>打开「更新内容」弹窗，正文由调用方给（本地文本文件）。</summary>
    /// <param name="title">标题，例如「更新内容 · v1.0.2」。</param>
    /// <param name="versionLine">标题下面那行小字，一般写当前版本。</param>
    /// <param name="notes">正文。</param>
    void ShowUpdateNotes(string title, string versionLine, string notes);

    /// <summary>用系统默认浏览器打开链接。</summary>
    /// <param name="url">地址。</param>
    void OpenExternal(string url);

    /// <summary>
    /// 打开下载浮层并下载更新包，进度实时显示；返回落地文件的完整路径。
    /// 用户取消返回 null，失败抛异常由调用方提示。
    /// </summary>
    /// <param name="release">要下载的发行版。</param>
    Task<string?> DownloadUpdateAsync(UpdateRelease release);

    /// <summary>用资源管理器把安装程序拉起来（安装界面由它自己接管）。</summary>
    /// <param name="path">下载好的安装包路径。</param>
    void LaunchInstaller(string path);

    /// <summary>更新前的退出：真退，不走「收进托盘」。</summary>
    void ShutdownForUpdate();

    /// <summary>打开详情弹框。</summary>
    /// <param name="item">那一款游戏。</param>
    /// <param name="favorite">打开时是否已收藏。</param>
    /// <param name="favoriteChanged">用户在里面加 / 取消收藏时回调。</param>
    /// <param name="launched">用户在里面点「启动程序」成功时回调。</param>
    void ShowDetail(TrainerItem item, bool favorite, Action<bool> favoriteChanged, Action launched);

    /// <summary>把卡片列表滚回顶部。</summary>
    void ScrollToTop();
}
