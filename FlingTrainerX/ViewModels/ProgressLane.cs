namespace FlingTrainerX.ViewModels;

/// <summary>
/// 更新弹框里的一条进度线：百分比、进度条、状态行、明细行、计数行。
/// <para>
/// 这里只说「现在该显示什么」，一个控件都不认识 —— 界面单向绑上去，
/// 谁要动进度就改这一份状态，不用管弹框当时是哪种形态。
/// </para>
/// <para>
/// 明细行有两种长相：普通一句说明（<see cref="Detail"/>），
/// 或者「英文名  >>  中文名」这种三色对照（<see cref="ShowPair"/> 为 true 时改用名字那一组属性）。
/// 两者互斥，同一个位置只显示一个。
/// </para>
/// </summary>
public sealed partial class ProgressLane : ObservableObject
{
    /// <summary>整条进度线是否显示（比如只更新游戏名时，「游戏数据」那条整段收起）。</summary>
    [ObservableProperty]
    private bool _visible = true;

    /// <summary>进度条走不确定态（还在数总数的时候）。</summary>
    [ObservableProperty]
    private bool _indeterminate;

    /// <summary>进度值，0~1：XAML 里那条 ProgressBar 的 Maximum 固定成 1。</summary>
    [ObservableProperty]
    private double _value;

    /// <summary>整段的透明度：「本次跳过」那一条灰显但不出框，弹框高度才不会跳。</summary>
    [ObservableProperty]
    private double _opacity = 1;

    [ObservableProperty]
    private string _percent = "";

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private string _count = "";

    /// <summary>明细行改用名字对照（英文名 + 中文名），而不是普通说明。</summary>
    [ObservableProperty]
    private bool _showPair;

    [ObservableProperty]
    private string _pairEnglish = "";

    [ObservableProperty]
    private string _pairChinese = "";

    /// <summary>这一款换到中文名了没有：没有就把破折号顶上，颜色也跟着暗下来。</summary>
    [ObservableProperty]
    private bool _hasChinese;

    /// <summary>英文名和中文名之间那个箭头，两边留空，不随内容变。</summary>
    public string PairArrow => "  >>  ";

    /// <summary>
    /// 中文名那一截：这一款没换到中文名就直接写「无中文名」，
    /// 比一个破折号更直白 —— 那是 Steam 上真没有，不是还没查。
    /// </summary>
    public string ChineseText => HasChinese ? PairChinese : "无中文名";

    /// <summary>明细行显示不显示：空串要收起来，否则进度块下面会多出一截空白。</summary>
    public bool DetailVisible => !ShowPair && !string.IsNullOrWhiteSpace(Detail);

    public string PairTooltip => HasChinese
        ? $"{PairEnglish}  >>  {PairChinese}"
        : $"{PairEnglish}  >>  没有中文名";

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(DetailVisible));

    partial void OnShowPairChanged(bool value) => OnPropertyChanged(nameof(DetailVisible));

    partial void OnHasChineseChanged(bool value)
    {
        OnPropertyChanged(nameof(ChineseText));
        OnPropertyChanged(nameof(PairTooltip));
    }

    partial void OnPairEnglishChanged(string value) => OnPropertyChanged(nameof(PairTooltip));

    partial void OnPairChineseChanged(string value) => OnPropertyChanged(nameof(PairTooltip));

    // ==================== 几种固定形态 ====================

    /// <summary>归零：进度条回到起点，几行字全清掉。</summary>
    public void Reset()
    {
        Value = 0;
        Indeterminate = false;
        Percent = "";
        Detail = "";
        Count = "";
        ShowPair = false;
    }

    /// <summary>等着开工：进度条不动，只写一行说明。</summary>
    public void Waiting(string state, string detail = "")
    {
        Reset();
        State = state;
        Detail = detail;
    }

    /// <summary>总数还不知道（正在读名录 / 正在连官网）：进度条走不确定态。</summary>
    public void Rolling(string state, string detail = "")
    {
        Reset();
        Indeterminate = true;
        State = state;
        Detail = detail;
    }

    /// <summary>总数拿到了，从 0 开始按条推。</summary>
    public void Start(int total, string state)
    {
        var max = Math.Max(1, total);
        Reset();
        State = state;
        Value = 0;
        Percent = "0%";
        Count = $"0 / {max}";
    }

    /// <summary>推一条：百分比、进度条、计数一起走。</summary>
    public void Report(int done, int total, string state)
    {
        var max = Math.Max(1, total);
        var value = Math.Clamp(done, 0, max);

        Indeterminate = false;
        Value = (double)value / max;
        Percent = $"{(int)Math.Round(value * 100.0 / max)}%";
        State = state;
        Count = $"{value} / {max}";
    }

    /// <summary>跑到头：进度条打满，写明结果。</summary>
    public void Finish(string state, string detail = "")
    {
        Indeterminate = false;
        Value = 1;
        Percent = "100%";
        State = state;
        Detail = detail;
        Count = "";
        ShowPair = false;
    }

    /// <summary>
    /// 半路停下（取消 / 连不上）：进度条留在原地，只换说明。
    /// </summary>
    /// <param name="clearPercent">true = 百分比一起清掉（结果已经没意义时）。</param>
    public void Stop(string state, string detail = "", bool clearPercent = true)
    {
        Indeterminate = false;
        if (clearPercent) Percent = "";
        State = state;
        Detail = detail;
        Count = "";
        ShowPair = false;
    }

    /// <summary>明细行换成一句普通说明（名字对照那套同时收起来）。</summary>
    public void SetDetail(string text)
    {
        ShowPair = false;
        Detail = text;
    }

    /// <summary>明细行换成「英文名  >>  中文名」：换一条写一条。</summary>
    public void ShowNamePair(string english, string chinese)
    {
        Detail = "";
        PairEnglish = english;
        PairChinese = chinese;
        HasChinese = !string.IsNullOrWhiteSpace(chinese);
        ShowPair = true;
    }
}
