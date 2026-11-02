using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>数据加载 / 本地缓存 / 收藏状态。</summary>
public static class TrainerDataService
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlingTrainerX");

    private static readonly string CacheFile = Path.Combine(DataDir, "library.json");
    private static readonly string StateFile = Path.Combine(DataDir, "state.json");
    private static readonly string ImageDir = Path.Combine(DataDir, "images");

    // 图片同样要走浏览器 UA，否则 CDN 会按 UA 拒绝
    private static readonly HttpClient ImgHttp =
        UserAgentProvider.CreateHttpClient(TrainerWeb.Site, TimeSpan.FromSeconds(30));

    /// <summary>当前内存中的库，供详情页补全下载/图片后回写缓存。</summary>
    public static TrainerLibrary? CurrentLibrary { get; set; }

    public static string BundledFile =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "trainers.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static TrainerLibrary LoadLocal()
    {
        foreach (var path in new[] { CacheFile, BundledFile })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var lib = JsonSerializer.Deserialize<TrainerLibrary>(File.ReadAllText(path), JsonOpts);
                if (lib?.Items.Count > 0) return lib;
            }
            catch
            {
                // 缓存损坏时回退到内置数据
            }
        }

        return new TrainerLibrary();
    }

    /// <summary>把当前内存里的库（含翻页累加的数据）写回缓存。</summary>
    public static void SaveCache(TrainerLibrary lib)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(lib, JsonOpts));
        }
        catch
        {
            // 缓存写入失败不影响本次使用
        }
    }

    /// <summary>从官网拉取最新列表，并复用本地的封面 / 中文名 / 下载表。返回 null 表示失败。</summary>
    public static async Task<TrainerLibrary?> RefreshAsync(TrainerLibrary current, CancellationToken ct = default)
    {
        try
        {
            var items = await TrainerWeb.FetchAsync(ct);
            if (items.Count == 0) return null;

            var old = current.Items.ToDictionary(i => i.Id);
            foreach (var it in items)
            {
                if (!old.TryGetValue(it.Id, out var prev)) continue;
                it.TitleCn = prev.TitleCn;
                it.Poster = prev.Poster;
                it.Image = prev.Image;
                if (it.Downloads.Count == 0) it.Downloads = prev.Downloads;
                if (it.Images.Count == 0) it.Images = prev.Images;
            }

            var lib = new TrainerLibrary
            {
                Source = TrainerWeb.Site,
                FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Pages = 1,
                Items = items
            };

            SaveCache(lib);
            return lib;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 图片（本地直接读，网络图后台下载并缓存） ----------

    /// <summary>同步读取本地封面；文件不存在返回 null。</summary>
    public static ImageSource? LoadLocalImage(string? path, int decodeWidth = 0)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return null;

            var full = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, "Assets", path);
            return File.Exists(full) ? Decode(full, decodeWidth) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>异步加载介绍图：先查磁盘缓存，再后台下载解码（不阻塞 UI 线程）。</summary>
    public static async Task<ImageSource?> LoadImageAsync(string? pathOrUrl, int decodeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return null;

        if (!pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return await Task.Run(() => LoadLocalImage(pathOrUrl, decodeWidth)).ConfigureAwait(false);

        var cached = CachePathOf(pathOrUrl);
        try
        {
            if (File.Exists(cached)) return await Task.Run(() => Decode(cached, decodeWidth));

            Directory.CreateDirectory(ImageDir);
            var bytes = await ImgHttp.GetByteArrayAsync(pathOrUrl).ConfigureAwait(false);
            await File.WriteAllBytesAsync(cached, bytes).ConfigureAwait(false);
            return await Task.Run(() => Decode(cached, decodeWidth));
        }
        catch
        {
            return null;
        }
    }

    private static string CachePathOf(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        var ext = Path.GetExtension(url.Split('?')[0]);
        if (ext.Length is 0 or > 5) ext = ".img";
        return Path.Combine(ImageDir, hash + ext);
    }

    private static readonly SemaphoreSlim CoverGate = new(4);

    /// <summary>把官网截图缓存到本地并设为卡片封面（后台执行，不阻塞滚动）。</summary>
    public static async Task EnsureCoverAsync(TrainerItem item)
    {
        if (item.HasPoster || string.IsNullOrWhiteSpace(item.ImageUrl)) return;

        await CoverGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var local = CachePathOf(item.ImageUrl);
            if (!File.Exists(local))
            {
                Directory.CreateDirectory(ImageDir);
                var bytes = await ImgHttp.GetByteArrayAsync(item.ImageUrl).ConfigureAwait(false);
                await File.WriteAllBytesAsync(local, bytes).ConfigureAwait(false);
            }

            item.SetPoster(local);
        }
        catch
        {
            // 忽略单张封面失败
        }
        finally
        {
            CoverGate.Release();
        }
    }

    private static ImageSource? Decode(string file, int decodeWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(file, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ---------- 收藏夹（SQLite / SqlSugar 单例） ----------

    private sealed class LegacyState
    {
        public List<int> Favorites { get; set; } = new();
    }

    private static bool _legacyChecked;

    /// <summary>
    /// 读取收藏列表：先按 ID 关联当前库（拿到最新版本/下载数据），
    /// 库里没有的（翻页没加载到）就用数据库里的 JSON 快照还原。
    /// </summary>
    public static List<TrainerItem> LoadFavorites(TrainerLibrary library)
    {
        ImportLegacyOnce(library);

        var byId = library.Items.ToDictionary(i => i.Id);
        var result = new List<TrainerItem>();
        var seen = new HashSet<int>();

        foreach (var row in AppDatabase.Instance.GetAll())
        {
            if (!seen.Add(row.TrainerId)) continue;

            if (byId.TryGetValue(row.TrainerId, out var live))
            {
                result.Add(live);
            }
            else if (row.ToItem() is { } snapshot)
            {
                result.Add(snapshot);
            }
        }

        return result;
    }

    /// <summary>添加 / 移除收藏，落库后立即生效。</summary>
    public static bool SetFavorite(TrainerItem item, bool favorite)
    {
        var db = AppDatabase.Instance;
        if (!db.IsAvailable) return false;

        return favorite ? db.Save(item) : db.Remove(item.Id);
    }

    public static bool ClearFavorites() => AppDatabase.Instance.Clear() >= 0;

    public static int FavoriteCount() => AppDatabase.Instance.Count();

    /// <summary>老版本把收藏写在 state.json，检测到就一次性迁进 SQLite。</summary>
    private static void ImportLegacyOnce(TrainerLibrary library)
    {
        if (_legacyChecked) return;
        _legacyChecked = true;

        try
        {
            if (!File.Exists(StateFile)) return;
            if (AppDatabase.Instance.Count() > 0) return;

            var st = JsonSerializer.Deserialize<LegacyState>(File.ReadAllText(StateFile), JsonOpts);
            if (st?.Favorites is { Count: > 0 })
                AppDatabase.Instance.ImportLegacy(st.Favorites, library.Items);

            File.Delete(StateFile);
        }
        catch
        {
            // 迁移失败不影响新库
        }
    }
}
