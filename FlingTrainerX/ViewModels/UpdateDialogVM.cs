using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 「发现新版本 / 更新说明」弹窗（UpdateLayer）的视图模型。
/// <para>
/// 两种形态：<see cref="Prompt"/> = 发现新版本（带「取消更新」和更新包大小），
/// false = 只是查看更新内容（只有「知道了」）。文字全部由这里给，XAML 单向绑定。
/// </para>
/// </summary>
public sealed partial class UpdateDialogVM : ObservableObject
{
    [ObservableProperty] private bool _isOpen;

    /// <summary>是不是「发现新版本」（换一种按钮组合）。</summary>
    [ObservableProperty] private bool _prompt;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _versionLine = "";
    [ObservableProperty] private string _notes = "";

    /// <summary>主按钮上的字：发现新版本是「立即更新」，只看说明是「知道了」。</summary>
    public string NowText => Prompt ? "立即更新" : "知道了";

    /// <summary>当前要装 / 要看的发行版。</summary>
    public UpdateRelease? Release { get; private set; }

    partial void OnPromptChanged(bool value) => OnPropertyChanged(nameof(NowText));

    /// <summary>
    /// 用户按了按钮：<c>true</c> = 「立即更新」，<c>false</c> = 「取消更新 / 知道了」。
    /// <para>收场动画和下载安装都需要窗口，所以这里只发信号，由 <c>Views/AppOverlays</c> 接。</para>
    /// </summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>按一次检查结果摆好内容。</summary>
    /// <param name="release">发行版。</param>
    /// <param name="prompt">true = 发现新版本（能直接更新）。</param>
    public void Show(UpdateRelease release, bool prompt)
    {
        Release = release;
        Prompt = prompt;

        Title = prompt ? $"发现新版本 v{release.Version}" : $"更新内容 · v{release.Version}";

        var parts = new List<string> { $"当前版本 v{UpdateService.CurrentVersionText}" };
        if (prompt) parts.Add($"最新版本 v{release.Version}");
        if (release.PublishedText.Length > 0) parts.Add(release.PublishedText);
        if (prompt && release.SizeText.Length > 0) parts.Add($"更新包 {release.SizeText}");
        VersionLine = string.Join(" · ", parts);

        Notes = release.Notes.Length > 0 ? release.Notes : "这个版本没有填写更新说明。";
    }

    /// <summary>
    /// 只看「更新内容」：正文由调用方给（本地 Assets\changelog.txt），不依赖网络，
    /// 所以这种形态永远只有「知道了」。
    /// </summary>
    public void ShowNotes(string title, string versionLine, string notes)
    {
        Release = null;
        Prompt = false;

        Title = title;
        VersionLine = versionLine;
        Notes = notes;
    }

    /// <summary>收框。</summary>
    public void Close()
    {
        IsOpen = false;
    }

    /// <summary>
    /// 「取消更新」/「知道了」：先关掉。
    /// <para>
    /// 发现新版本这条路上，取消意味着「以后别再拿更新烦我」——顺手把
    /// <c>config.json</c> 里的 <c>autoUpdateCheck</c> 写成 false，下次启动就不再自动检查。
    /// 只是查看更新内容（<see cref="Prompt"/> = false）时不动这个开关。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void Decline()
    {
        if (Prompt) AppConfig.SetAutoUpdateCheck(false);

        CloseRequested?.Invoke(this, false);
    }

    /// <summary>「立即更新」：关掉并交给更新器。</summary>
    [RelayCommand]
    private void Accept() => CloseRequested?.Invoke(this, true);
}
