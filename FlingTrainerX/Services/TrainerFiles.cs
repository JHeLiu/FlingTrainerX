using System.Diagnostics;
using System.IO;
using System.Net.Http;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>
/// 每款游戏的本地文件（封面 / 介绍截图 / 下载好的修改器）统一放在
/// <c>&lt;程序目录&gt;\f\&lt;官网文章 ID&gt;\</c> 下，路径写回 SQLite 缓存表，
/// 下次启动直接读本地文件，不再重复下载。
/// </summary>
public static class TrainerFiles
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };

    private static readonly string[] ArchiveExts = { ".zip", ".rar", ".7z", ".tar", ".gz", ".001" };

    private static HttpClient? _http;

    private static HttpClient Http => _http ??= UserAgentProvider.CreateHttpClient(TrainerWeb.Site);

    public static string FolderFor(int trainerId) => AppPaths.FilesFor(trainerId);

    // ==================== 图片 ====================

    /// <summary>
    /// 把一个图片地址存成 &lt;id&gt;/&lt;name&gt;.ext，返回**相对程序目录**的路径（写库用）。
    /// 已经存在就直接返回，失败返回空串（界面上会自动退回在线地址）。
    /// </summary>
    public static async Task<string> SaveImageAsync(
        int trainerId, string url, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        // 已经是本地文件就直接用
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) is false)
        {
            var existing = AppPaths.Resolve(url);
            if (File.Exists(existing)) return AppPaths.Relative(existing);
        }

        try
        {
            var dir = FolderFor(trainerId);
            var path = Path.Combine(dir, SafeName(name) + ExtOf(url));

            if (File.Exists(path) && new FileInfo(path).Length > 0) return AppPaths.Relative(path);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            UserAgentProvider.ApplyHeaders(request, TrainerWeb.Site);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // 先写 .part 再改名：中途失败不会留下半张图被当成「已缓存」
            var temp = path + ".part";
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            {
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            return AppPaths.Relative(path);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>把封面存到本地并写回对象；已经存过就返回 false。</summary>
    public static async Task<bool> CacheCoverAsync(TrainerItem item, CancellationToken ct = default)
    {
        if (item.Id <= 0) return false;
        if (item.HasLocalCover) return false;
        if (string.IsNullOrWhiteSpace(item.Cover)) return false;

        var path = await SaveImageAsync(item.Id, item.Cover, "cover", ct).ConfigureAwait(false);
        if (path.Length == 0) return false;

        item.CoverPath = path;
        return true;
    }

    /// <summary>把正文里的介绍截图逐张存到本地，返回新落盘的张数。</summary>
    public static async Task<int> CacheScreenshotsAsync(TrainerItem item, CancellationToken ct = default)
    {
        if (item.Images.Count == 0) return 0;

        var paths = new List<string>(item.ImagePaths);
        while (paths.Count < item.Images.Count) paths.Add("");

        var added = 0;
        for (var i = 0; i < item.Images.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            if (!string.IsNullOrWhiteSpace(paths[i]) && File.Exists(AppPaths.Resolve(paths[i]))) continue;

            var path = await SaveImageAsync(item.Id, item.Images[i], $"shot{i + 1:00}", ct).ConfigureAwait(false);
            if (path.Length == 0) continue;

            paths[i] = path;
            added++;
        }

        if (added > 0) item.ImagePaths = paths;
        return added;
    }

    /// <summary>某张截图应该显示哪个地址：本地有就用本地（拼成绝对路径），否则用官网链接。</summary>
    public static string ShotSource(TrainerItem item, int index)
    {
        if (index >= 0 && index < item.ImagePaths.Count)
        {
            var local = AppPaths.Resolve(item.ImagePaths[index]);
            if (!string.IsNullOrWhiteSpace(local) && File.Exists(local)) return local;
        }

        return index >= 0 && index < item.Images.Count ? item.Images[index] : "";
    }

    // ==================== 下载好的程序 ====================

    /// <summary>这款游戏本地已经下好的文件，取官网日期最新的那个。</summary>
    public static TrainerDownload? LocalFile(TrainerItem item) =>
        item.Downloads
            .Where(d => d.HasLocalFile)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>用系统默认方式打开/运行一个本地文件。</summary>
    public static bool Launch(string path)
    {
        try
        {
            path = AppPaths.Resolve(path);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>压缩包不能直接运行，返回 true 让界面提示先解压。</summary>
    public static bool IsArchive(string path) =>
        ArchiveExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>在资源管理器里定位到某个文件。</summary>
    public static void ShowInFolder(string path)
    {
        try
        {
            path = AppPaths.Resolve(path);

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = AppPaths.FilesDir;
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了
        }
    }

    // ==================== 工具 ====================

    /// <summary>从 URL 里猜图片扩展名，猜不到按 jpg 存。</summary>
    private static string ExtOf(string url)
    {
        var path = url;
        var cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) path = path[..cut];

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExts.Contains(ext) ? ext : ".jpg";
    }

    /// <summary>去掉文件名里的非法字符。</summary>
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);

        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);

        var text = sb.ToString().Trim();
        return text.Length == 0 ? "file" : text;
    }
}
