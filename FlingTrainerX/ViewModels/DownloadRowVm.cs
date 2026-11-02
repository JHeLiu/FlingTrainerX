using System.ComponentModel;
using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 详情页下载表格的一行。原表格数据 + 对应下载任务的实时进度合成一个可绑定对象，
/// 任务状态变了就整体通知一次，界面不用写转换器。
/// </summary>
public sealed class DownloadRowVm : INotifyPropertyChanged
{
    private readonly TrainerDownload _file;
    private DownloadTask? _task;

    public DownloadRowVm(TrainerDownload file)
    {
        _file = file;
        Attach(DownloadManager.Instance.Find(file.Url));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FileName => _file.File;
    public string Url => _file.Url;
    public string ShortUrl => _file.ShortUrl;
    public string Date => _file.Date;
    public string Size => _file.Size;
    public string CountText => _file.CountText;

    public DownloadTask? Task => _task;
    public bool HasTask => _task is not null;

    public double Progress => _task?.Progress ?? 0;
    public bool Indeterminate => _task?.Indeterminate ?? false;
    public string PercentText => _task?.ProgressText ?? "";
    public string ProgressLine => _task?.DetailText ?? "";

    /// <summary>右侧按钮文案随状态变：下载 → 取消 → 重试 / 打开。</summary>
    public string ActionText => _task?.State switch
    {
        DownloadState.Queued => "取消",
        DownloadState.Running => "取消",
        DownloadState.Completed => "打开",
        DownloadState.Failed or DownloadState.Canceled => "重试",
        _ => "下载"
    };

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
        foreach (var name in new[]
                 {
                     nameof(Task), nameof(HasTask), nameof(Progress), nameof(Indeterminate),
                     nameof(PercentText), nameof(ProgressLine), nameof(ActionText)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
