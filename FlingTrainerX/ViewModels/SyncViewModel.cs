namespace FlingTrainerX.ViewModels;

/// <summary>
/// 更新进度弹框的视图模型：三条进度线（游戏数据 / 游戏名 / Steam 中文名）
/// 加上标题、说明、可选项和两颗按钮的状态。
/// <para>
/// 窗口那边只负责「跑」：抓数据、推进度、收尾。至于弹框长什么样、哪块藏着、
/// 按钮叫什么，全在这里定；界面单向绑过来，代码里不再出现 <c>xxx.Text = ...</c>。
/// </para>
/// </summary>
public sealed partial class SyncViewModel : ObservableObject
{
    /// <summary>① 游戏数据：官网列表 / 本地详情页。</summary>
    public ProgressLane Data { get; } = new();

    /// <summary>② 官网游戏名录（+ 不分开显示时的 Steam 中文名）。</summary>
    public ProgressLane Names { get; } = new();

    /// <summary>③ Steam 中文名：只有「快速更新游戏名」这一路会单独拆出来。</summary>
    public ProgressLane Chinese { get; } = new() { Visible = false };

    /// <summary>弹框开着没有（遮罩 + 卡片一起显隐）。</summary>
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = "更新全部游戏数据";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _tip = "";

    /// <summary>第二条进度线的标签：「更新游戏名」还是「更新游戏名与中文名」。</summary>
    [ObservableProperty]
    private string _namesLabel = "更新游戏名与中文名";

    /// <summary>
    /// 两条进度分开显示（只更新游戏名时）：名录一条、Steam 中文名一条。
    /// false 时中文名和名录共用一条。
    /// </summary>
    [ObservableProperty]
    private bool _splitLayout;

    // ---- 「同时更新 Steam 中文名」那一块：只更新游戏名时才有 ----

    [ObservableProperty]
    private bool _steamOptionVisible;

    /// <summary>开跑之后还留在原位，只是锁住不让改（框不跳动）。</summary>
    [ObservableProperty]
    private bool _steamOptionEnabled = true;

    [ObservableProperty]
    private bool _steamChecked;

    // ---- 右下角两颗按钮 ----

    [ObservableProperty]
    private bool _startVisible;

    [ObservableProperty]
    private bool _startEnabled = true;

    [ObservableProperty]
    private string _startText = "开始更新";

    [ObservableProperty]
    private bool _cancelEnabled = true;

    [ObservableProperty]
    private string _cancelText = "取消更新";

    /// <summary>点「开始更新」要做的事：由窗口挂进来（VM 不认识窗口，也不认识取消标记）。</summary>
    public Action? StartRequested { get; set; }

    /// <summary>点「取消更新」要做的事。</summary>
    public Action? CancelRequested { get; set; }

    public IRelayCommand StartCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public SyncViewModel()
    {
        StartCommand = new RelayCommand(() => StartRequested?.Invoke(), () => StartEnabled);
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke(), () => CancelEnabled);
    }

    partial void OnStartEnabledChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    partial void OnCancelEnabledChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();

    /// <summary>只更新游戏名：游戏数据那条不跑，第三条单独显示。</summary>
    public void UseSplitLayout()
    {
        SplitLayout = true;
        Data.Visible = false;
        Chinese.Visible = true;
    }

    /// <summary>三条线全部归零，回到「刚开框」的干净状态。</summary>
    public void ResetLanes()
    {
        Data.Reset();
        Names.Reset();
        Chinese.Reset();
    }
}
