using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 「正在下载更新包」浮层（DownloadLayer）的视图模型。
/// <para>
/// 只负责把进度数字变成给人看的字，下载本身在 <see cref="UpdateService.DownloadAsync"/>，
/// 界面动作（开合浮层、拉安装程序）在 <c>Views\AppOverlays</c>。
/// </para>
/// </summary>
public sealed partial class UpdateDownloadVM : ObservableObject
{
    [ObservableProperty] private bool _isOpen;

    /// <summary>0~100，直接绑进度条。</summary>
    [ObservableProperty] private double _percent;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subTitle = "";
    [ObservableProperty] private string _hint = "";

    /// <summary>「32.7 MB / 72.7 MB · 45%」这种。</summary>
    [ObservableProperty] private string _progressText = "";

    /// <summary>「3.2 MB/s · 还剩 00:21」；总大小未知时只显示速度。</summary>
    [ObservableProperty] private string _speedText = "";

    private CancellationTokenSource? _cancellation;

    /// <summary>本次下载的取消令牌（用户点「取消」时触发）。</summary>
    public CancellationToken Token => _cancellation?.Token ?? CancellationToken.None;

    /// <summary>开始一次下载：重置数字、打开浮层。</summary>
    /// <param name="release">要下的发行版。</param>
    public void Begin(UpdateRelease release)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        Percent = 0;
        Title = $"正在下载 v{release.Version}";
        SubTitle = string.IsNullOrWhiteSpace(release.AssetName) ? "更新包" : release.AssetName;
        Hint = "下载完成后会自动打开安装程序，按提示点下一步就行；中途可以取消。";
        ProgressText = release.Size > 0 ? $"0 {DownloadTask.Human(0)} / {DownloadTask.Human(release.Size)}" : "";
        SpeedText = "";
        IsOpen = true;
    }

    /// <summary>收一次进度（调用方保证在 UI 线程上）。</summary>
    /// <param name="progress">进度快照。</param>
    public void Report(DownloadProgress progress)
    {
        var total = progress.Total > 0 ? progress.Total : 0;

        if (total > 0)
        {
            Percent = Math.Clamp(progress.Received * 100.0 / total, 0, 100);
            ProgressText = $"{DownloadTask.Human(progress.Received)} / {DownloadTask.Human(total)}";
        }
        else
        {
            // 服务器没给大小：进度条走不确定态，数字只报已下载量
            Percent = 0;
            ProgressText = DownloadTask.Human(progress.Received);
        }

        var speed = progress.BytesPerSecond;
        if (speed <= 0)
        {
            SpeedText = "";
            return;
        }

        var speedText = $"{DownloadTask.Human((long)speed)}/s";
        if (total > 0 && speed > 0)
        {
            var remain = TimeSpan.FromSeconds(Math.Max(0, (total - progress.Received) / speed));
            speedText += $" · 还剩 {remain:mm\\:ss}";
        }
        SpeedText = speedText;
    }

    /// <summary>下载收场（成功或失败都会调用），关掉浮层。</summary>
    public void Finish()
    {
        IsOpen = false;
        SpeedText = "";
    }

    /// <summary>「取消」：掐断下载，<see cref="UpdateService.DownloadAsync"/> 会抛 OperationCanceledException。</summary>
    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();
}
