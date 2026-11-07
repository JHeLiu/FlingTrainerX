namespace FlingTrainerX.Models;

/// <summary>
/// 下载进度的一次快照：已经收了多少、总共多少、当前速度。
/// 由 <c>UpdateService.DownloadAsync</c> 每 100ms 上报一次，界面只读不写。
/// </summary>
/// <param name="Received">已接收字节数。</param>
/// <param name="Total">总字节数；服务器没给 Content-Length 且发行版也没写大小时为 0。</param>
/// <param name="BytesPerSecond">当前速度（字节/秒），按整体平均值算，比瞬时值稳。</param>
public readonly record struct DownloadProgress(long Received, long Total, double BytesPerSecond);
