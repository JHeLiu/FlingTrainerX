using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>
/// 下载队列（单例）。直接连官网下载直链，最多同时下 2 个，其余排队；
/// 每个任务都会回写进度、速度与剩余时间，界面只负责绑。
/// </summary>
public sealed class DownloadManager
{
    public static DownloadManager Instance { get; } = new();

    private const int MaxConcurrent = 2;

    private readonly HttpClient _http;
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private int _active;

    /// <summary>队列里的全部任务（含已完成，便于查看保存位置）。</summary>
    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    /// <summary>文件保存目录。</summary>
    public string DownloadDir { get; }

    /// <summary>队列发生变化（新增 / 完成 / 失败）时触发，主窗口用它刷新角标。</summary>
    public event EventHandler? Changed;

    public int ActiveCount => _active;

    public string SummaryText
    {
        get
        {
            if (Tasks.Count == 0) return "暂无下载任务";

            var running = Tasks.Count(t => t.State == DownloadState.Running);
            var queued = Tasks.Count(t => t.State == DownloadState.Queued);
            var done = Tasks.Count(t => t.IsCompleted);
            var failed = Tasks.Count(t => t.State == DownloadState.Failed);

            var parts = new List<string> { $"共 {Tasks.Count} 个" };
            if (running > 0) parts.Add($"下载中 {running}");
            if (queued > 0) parts.Add($"排队 {queued}");
            if (done > 0) parts.Add($"已完成 {done}");
            if (failed > 0) parts.Add($"失败 {failed}");
            return string.Join(" · ", parts);
        }
    }

    private DownloadManager()
    {
        DownloadDir = ResolveDownloadDir();
        _http = UserAgentProvider.CreateHttpClient(TrainerWeb.Site, Timeout.InfiniteTimeSpan);
    }

    /// <summary>默认存到「下载\FlingTrainerX」，系统没有下载目录就退到本地应用数据目录。</summary>
    private static string ResolveDownloadDir()
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(profile, "Downloads");
            return Directory.Exists(downloads) ? Path.Combine(downloads, "FlingTrainerX") : FallbackDir();
        }
        catch
        {
            return FallbackDir();
        }
    }

    private static string FallbackDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlingTrainerX", "downloads");

    // ==================== 队列操作 ====================

    /// <summary>把官网的一个文件加入队列；已经在队列里（且没失败）就直接返回原任务。</summary>
    public DownloadTask Enqueue(TrainerItem item, TrainerDownload file)
    {
        var key = $"{item.Id}|{file.Url}";
        var exist = Tasks.FirstOrDefault(t => t.Key == key);
        if (exist is not null && !exist.CanRetry) return exist;

        var task = new DownloadTask(item, file);
        Tasks.Add(task);
        Notify();
        Pump();
        return task;
    }

    /// <summary>按地址找当前任务，详情页每一行用它拿到进度。</summary>
    public DownloadTask? Find(string url) =>
        Tasks.FirstOrDefault(t => string.Equals(t.Url, url, StringComparison.OrdinalIgnoreCase));

    public void Retry(DownloadTask task)
    {
        if (!task.CanRetry) return;

        task.Error = "";
        task.Received = 0;
        task.Total = 0;
        task.Speed = 0;
        task.SavePath = "";
        task.State = DownloadState.Queued;
        Notify();
        Pump();
    }

    public void Cancel(DownloadTask task)
    {
        if (!task.IsActive) return;

        if (_running.TryGetValue(task.Key, out var cts))
        {
            cts.Cancel();
            return;
        }

        // 还在排队，直接标记取消
        task.State = DownloadState.Canceled;
        Notify();
    }

    public void Remove(DownloadTask task)
    {
        Cancel(task);
        Tasks.Remove(task);
        Notify();
    }

    /// <summary>清掉已完成 / 已失败 / 已取消的任务，正在下的不动。</summary>
    public int ClearFinished()
    {
        var removed = Tasks.Where(t => t.IsFinished).ToList();
        foreach (var task in removed) Tasks.Remove(task);
        if (removed.Count > 0) Notify();
        return removed.Count;
    }

    public void OpenFolder(DownloadTask? task)
    {
        try
        {
            if (task is not null && File.Exists(task.SavePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{task.SavePath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            Directory.CreateDirectory(DownloadDir);
            Process.Start(new ProcessStartInfo(DownloadDir) { UseShellExecute = true });
        }
        catch
        {
            // 打开资源管理器失败不致命
        }
    }

    // ==================== 下载实现 ====================

    private void Pump()
    {
        while (_active < MaxConcurrent)
        {
            var next = Tasks.FirstOrDefault(t => t.State == DownloadState.Queued);
            if (next is null) break;

            next.State = DownloadState.Running;
            _active++;
            _ = DownloadAsync(next);
        }

        Notify();
    }

    private async Task DownloadAsync(DownloadTask task)
    {
        var cts = new CancellationTokenSource();
        _running[task.Key] = cts;

        var path = "";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
            UserAgentProvider.ApplyHeaders(request,
                string.IsNullOrWhiteSpace(task.ArticleUrl) ? TrainerWeb.Site : task.ArticleUrl);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            task.Total = response.Content.Headers.ContentLength ?? 0;

            Directory.CreateDirectory(DownloadDir);
            path = UniquePath(Path.Combine(DownloadDir, ResolveFileName(response, task)));
            task.SavePath = path;

            await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous);

            var buffer = new byte[81920];
            long received = 0;
            long lastBytes = 0;
            var clock = Stopwatch.StartNew();
            var lastMs = 0L;

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cts.Token);
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                received += read;

                // 120ms 更新一次界面就够，太频繁反而拖慢下载
                var ms = clock.ElapsedMilliseconds;
                if (ms - lastMs < 120) continue;

                var seconds = (ms - lastMs) / 1000d;
                task.Speed = seconds > 0 ? (received - lastBytes) / seconds : 0;
                task.Received = received;
                lastMs = ms;
                lastBytes = received;
            }

            task.Received = received;
            task.Speed = 0;
            task.State = DownloadState.Completed;
        }
        catch (OperationCanceledException)
        {
            task.Speed = 0;
            task.State = DownloadState.Canceled;
            TryDelete(path);
        }
        catch (Exception ex)
        {
            task.Speed = 0;
            task.Error = Describe(ex);
            task.State = DownloadState.Failed;
            TryDelete(path);
        }
        finally
        {
            _running.Remove(task.Key);
            _active--;
            cts.Dispose();
            Notify();
            Pump();
        }
    }

    /// <summary>服务器跳转后的真实文件名最准；拿不到就用表格里的名字 + 扩展名。</summary>
    private static string ResolveFileName(HttpResponseMessage response, DownloadTask task)
    {
        var uri = response.RequestMessage?.RequestUri;

        if (uri is not null)
        {
            var last = uri.AbsolutePath.Split('/').LastOrDefault() ?? "";
            var name = Clean(Uri.UnescapeDataString(last));
            if (name.Length > 0 && Path.HasExtension(name)) return name;
        }

        return $"{Clean(task.FileName)}.{task.Ext}";
    }

    /// <summary>去掉文件名里 Windows 不允许的字符，过长时保留扩展名截断。</summary>
    private static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);

        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        var text = sb.ToString().Trim().TrimEnd('.');
        if (text.Length == 0) return "";

        if (text.Length <= 120) return text;

        var ext = Path.GetExtension(text);
        var stem = text[..(text.Length - ext.Length)];
        return stem[..Math.Min(stem.Length, 110)] + ext;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (path.Length > 0 && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就留着
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http => $"网络错误：{http.Message}",
        IOException io => $"写入失败：{io.Message}",
        _ => ex.Message
    };

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
