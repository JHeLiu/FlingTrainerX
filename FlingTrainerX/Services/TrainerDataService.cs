using System.IO;
using System.Text.Json;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>数据同步 / 收藏状态。</summary>
public static class TrainerDataService
{
    private static readonly string DataDir = AppPaths.LegacyDir;
    private static readonly string StateFile = Path.Combine(DataDir, "state.json");

    /// <summary>当前内存中的库，供详情页补全下载数据后使用。</summary>
    public static TrainerLibrary? CurrentLibrary { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

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
                it.Image = prev.Image;
                if (it.Downloads.Count == 0) it.Downloads = prev.Downloads;
                if (it.Images.Count == 0) it.Images = prev.Images;
                MergeLocal(it, prev);
            }

            SaveCache(items);

            return new TrainerLibrary
            {
                Source = TrainerWeb.Site,
                FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Pages = 1,
                Items = items
            };
        }
        catch
        {
            return null;
        }
    }

    // ---------- 首页卡片缓存（SQLite） ----------

    /// <summary>
    /// 启动时先把上次抓到的卡片读出来，界面立刻有内容；随后再向官网刷新。
    /// 没有缓存（第一次运行）返回 null。
    /// </summary>
    public static TrainerLibrary? LoadCachedLibrary()
    {
        var items = AppDatabase.Instance.LoadCache();
        if (items.Count == 0) return null;

        return new TrainerLibrary
        {
            Source = TrainerWeb.Site,
            FetchedAt = AppDatabase.Instance.LatestCacheTime()?.ToString("yyyy-MM-dd HH:mm") ?? "",
            Pages = 1,
            Items = items
        };
    }

    // ---------- 卡片缓存 ----------

    /// <summary>把抓到的卡片写进缓存表（有则更新，无则插入）。</summary>
    public static void SaveCache(IEnumerable<TrainerItem> items) => AppDatabase.Instance.SaveCacheRange(items);

    /// <summary>单条回写（封面落盘、下载完成时用）。</summary>
    public static void SaveCache(TrainerItem item) => AppDatabase.Instance.SaveCache(item);

    /// <summary>
    /// 刷新后新对象里没有本地路径，从旧对象按 ID / URL 搬过来，
    /// 否则已经下好的封面和程序在界面上会「消失」。
    /// </summary>
    public static void MergeLocal(TrainerItem fresh, TrainerItem? prev)
    {
        if (prev is null) return;

        if (string.IsNullOrWhiteSpace(fresh.CoverPath)) fresh.CoverPath = prev.CoverPath;
        if (fresh.ImagePaths.Count == 0 && prev.ImagePaths.Count > 0) fresh.ImagePaths = prev.ImagePaths;
        if (fresh.Images.Count == 0 && prev.Images.Count > 0) fresh.Images = prev.Images;

        // 列表接口不返回下载表，缓存里那份（带本地路径）原样接过来，
        // 否则刷新一次「启动程序」就又变灰了
        if (fresh.Downloads.Count == 0)
        {
            fresh.Downloads = prev.Downloads;
            return;
        }

        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in prev.Downloads)
        {
            if (!string.IsNullOrWhiteSpace(file.LocalPath)) local[file.Url] = file.LocalPath;
            if (file.ActualBytes > 0) bytes[file.Url] = file.ActualBytes;
        }

        foreach (var file in fresh.Downloads)
        {
            if (string.IsNullOrWhiteSpace(file.LocalPath) && local.TryGetValue(file.Url, out var path))
                file.LocalPath = path;
            if (file.ActualBytes <= 0 && bytes.TryGetValue(file.Url, out var size))
                file.ActualBytes = size;
        }
    }

    /// <summary>详情页重新读到下载表后，把本地路径补回去（按 URL 对齐）。</summary>
    public static void MergeLocalDownloads(TrainerItem item, List<TrainerDownload> fresh)
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in item.Downloads)
        {
            if (!string.IsNullOrWhiteSpace(file.LocalPath)) local[file.Url] = file.LocalPath;
            if (file.ActualBytes > 0) bytes[file.Url] = file.ActualBytes;
        }

        foreach (var file in fresh)
        {
            if (string.IsNullOrWhiteSpace(file.LocalPath) && local.TryGetValue(file.Url, out var path))
                file.LocalPath = path;
            if (file.ActualBytes <= 0 && bytes.TryGetValue(file.Url, out var size))
                file.ActualBytes = size;
        }
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
            // 主键是字符串，转回数字 ID 再和内存里的库对齐
            if (!int.TryParse(row.TrainerId, out var id)) continue;
            if (!seen.Add(id)) continue;

            if (byId.TryGetValue(id, out var live))
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
