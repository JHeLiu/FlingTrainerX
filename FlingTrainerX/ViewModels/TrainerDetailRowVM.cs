using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 详情页下载表格的一行。原表格数据 + 对应下载任务的实时进度合成一个可绑定对象，
/// 任务状态变了就整体通知一次，界面不用写转换器。
/// </summary>
public sealed class TrainerDetailRowVM : ObservableObject
{
    private readonly TrainerDownload _file;
    private DownloadTask? _task;

    public TrainerDetailRowVM(TrainerDownload file)
    {
        _file = file;
        Attach(DownloadManager.Instance.Find(file.Url));
    }

    public string FileName => _file.File;
    public string Url => _file.Url;
    public string ShortUrl => _file.ShortUrl;
    public string Date => _file.Date;
    public string Size => _file.Size;
    public string CountText => _file.CountText;

    public DownloadTask? Task => _task;
    public bool HasTask => _task is not null;

    private bool _selected;

    /// <summary>表格里被选中的那一行：右侧「启动程序」启动的就是它。</summary>
    public bool IsSelected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>状态列文案：已下载 / 未下载 / 正在下载。</summary>
    public string StateText
    {
        get
        {
            if (_task is { IsActive: true }) return "正在下载";
            if (_task is { State: DownloadState.Completed }) return "已下载";
            if (_task is { State: DownloadState.Failed }) return "下载失败";
            return HasLocalFile ? "已下载" : "未下载";
        }
    }

    /// <summary>状态列配色用（Idle / Active / Done / Failed），交给 XAML 里的 DataTrigger。</summary>
    public string StateKind
    {
        get
        {
            if (_task is { IsActive: true }) return "Active";
            if (_task is { State: DownloadState.Completed }) return "Done";
            if (_task is { State: DownloadState.Failed }) return "Failed";
            return HasLocalFile ? "Done" : "Idle";
        }
    }

    /// <summary>只有正在下载时才显示进度条，下完即消失。</summary>
    public bool ShowProgress => _task is { IsActive: true };

    /// <summary>这一行对应的文件是否已经在本机（重启后从缓存表恢复，不用重新下）。</summary>
    public bool HasLocalFile => _file.HasLocalFile;

    /// <summary>拼好程序目录的绝对路径（库里存的是相对路径）。</summary>
    public string LocalPath => _file.FullPath;

    public double Progress => _task?.Progress ?? 0;
    public bool Indeterminate => _task?.Indeterminate ?? false;
    public string PercentText => _task?.ProgressText ?? "";
    public string ProgressLine => _task?.DetailText ?? "";

    /// <summary>状态列里那行真实大小：「512 KB / ≈931 KB」。</summary>
    public string SizePairText => _task?.SizePairText ?? "";

    /// <summary>右侧按钮文案随状态变：下载 → 取消 → 重试 / 启动。</summary>
    public string ActionText => _task?.State switch
    {
        DownloadState.Queued => "取消",
        DownloadState.Running => "取消",
        DownloadState.Completed => "启动",
        DownloadState.Failed or DownloadState.Canceled => "重试",
        _ => HasLocalFile ? "启动" : "下载"
    };

    /// <summary>按钮前的图标（Segoe MDL2）：下载 / 取消 / 重试 / 播放。</summary>
    public string ActionGlyph => _task?.State switch
    {
        DownloadState.Queued or DownloadState.Running => "\uE711",
        DownloadState.Completed => "\uE768",
        DownloadState.Failed or DownloadState.Canceled => "\uE72C",
        _ => HasLocalFile ? "\uE768" : "\uE896"
    };

    /// <summary>行尾「定位」按钮只在真的有本地文件时出现。</summary>
    public bool CanOpenFolder => _task?.State == DownloadState.Completed || (_task is null && HasLocalFile);

    public void Attach(DownloadTask? task)
    {
        if (ReferenceEquals(_task, task)) return;

        if (_task is not null) _task.PropertyChanged -= OnTaskChanged;
        _task = task;
        if (_task is not null) _task.PropertyChanged += OnTaskChanged;

        RaiseAll();
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e) => RaiseAll();

    private void RaiseAll()
    {
        // 下载在后台线程推进，通知要回到界面线程，否则绑定不刷新
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(RaiseAllCore));
            return;
        }

        RaiseAllCore();
    }

    private void RaiseAllCore()
    {
        foreach (var name in new[]
                 {
                     nameof(Task), nameof(HasTask), nameof(Progress), nameof(Indeterminate),
                     nameof(PercentText), nameof(ProgressLine), nameof(SizePairText), nameof(ActionText),
                     nameof(ActionGlyph), nameof(CanOpenFolder), nameof(HasLocalFile), nameof(LocalPath),
                     nameof(StateText), nameof(StateKind), nameof(ShowProgress), nameof(IsSelected)
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
