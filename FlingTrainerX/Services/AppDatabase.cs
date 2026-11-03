using System.IO;
using System.Text.Json;
using DataRow = System.Data.DataRow;
using FlingTrainerX.Models;
using SqlSugar;

namespace FlingTrainerX.Services;

/// <summary>收藏夹表：一条记录就是一篇文章（一款游戏的修改器）。</summary>
[SugarTable("favorite_trainer")]
public sealed class FavoriteRow
{
    /// <summary>
    /// 官网文章 ID。**按字符串存**：官网换 ID 规则（加前缀、变成非法数字）时不用改表结构，
    /// 也能存下非纯数字的键。
    /// </summary>
    [SugarColumn(ColumnName = "trainer_id", Length = 64, IsPrimaryKey = true, IsIdentity = false)]
    public string TrainerId { get; set; } = "";

    [SugarColumn(ColumnName = "title", Length = 256, IsNullable = true)]
    public string Title { get; set; } = "";

    [SugarColumn(ColumnName = "url", Length = 512, IsNullable = true)]
    public string Url { get; set; } = "";

    [SugarColumn(ColumnName = "version", Length = 64, IsNullable = true)]
    public string Version { get; set; } = "";

    [SugarColumn(ColumnName = "option_count")]
    public int OptionCount { get; set; }

    [SugarColumn(ColumnName = "updated", Length = 32, IsNullable = true)]
    public string Updated { get; set; } = "";

    /// <summary>整条数据的 JSON 快照：即使该游戏翻页后不在当前列表里，收藏夹也能正常显示。</summary>
    [SugarColumn(ColumnName = "payload", ColumnDataType = "TEXT", IsNullable = true)]
    public string Payload { get; set; } = "";

    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; }

    private static readonly JsonSerializerOptions SnapshotOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static FavoriteRow From(TrainerItem item) => new()
    {
        TrainerId = item.Id.ToString(),
        Title = item.Title,
        Url = item.Url,
        Version = item.Version,
        OptionCount = item.RealOptionCount,
        Updated = item.UpdatedText,
        Payload = JsonSerializer.Serialize(item),
        CreatedAt = DateTime.Now
    };

    /// <summary>把快照还原成卡片用的对象，快照损坏时返回 null。</summary>
    public TrainerItem? ToItem()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Payload)) return null;
            var item = JsonSerializer.Deserialize<TrainerItem>(Payload, SnapshotOpts);
            if (item is null || item.Id == 0) return null;
            return item;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 首页卡片缓存表：官网一篇文章（一款游戏）一行。
/// <para>
/// 列表数据抓一次就落库，下次启动先读它，界面立刻有内容，再在后台向官网刷新。
/// 完整对象（选项分组 / 截图 / 下载表 / 本地文件路径）都存在 <see cref="Payload"/> 里，
/// 独立出来的列只是方便排查和排序。
/// </para>
/// </summary>
[SugarTable("trainer_cache")]
public sealed class TrainerCacheRow
{
    /// <summary>官网文章 ID，按字符串存（见 <see cref="FavoriteRow.TrainerId"/>）。</summary>
    [SugarColumn(ColumnName = "trainer_id", Length = 64, IsPrimaryKey = true, IsIdentity = false)]
    public string TrainerId { get; set; } = "";

    [SugarColumn(ColumnName = "title", Length = 256, IsNullable = true)]
    public string Title { get; set; } = "";

    [SugarColumn(ColumnName = "url", Length = 512, IsNullable = true)]
    public string Url { get; set; } = "";

    [SugarColumn(ColumnName = "version", Length = 64, IsNullable = true)]
    public string Version { get; set; } = "";

    [SugarColumn(ColumnName = "updated", Length = 32, IsNullable = true)]
    public string Updated { get; set; } = "";

    /// <summary>封面在本机的路径，存相对程序目录的路径（f\&lt;id&gt;\cover.jpg），没落盘就是空。</summary>
    [SugarColumn(ColumnName = "cover_path", Length = 512, IsNullable = true)]
    public string CoverPath { get; set; } = "";

    /// <summary>整条数据的 JSON 快照。</summary>
    [SugarColumn(ColumnName = "payload", ColumnDataType = "TEXT", IsNullable = true)]
    public string Payload { get; set; } = "";

    [SugarColumn(ColumnName = "cached_at")]
    public DateTime CachedAt { get; set; }

    public static TrainerCacheRow From(TrainerItem item) => new()
    {
        TrainerId = item.Id.ToString(),
        Title = item.Title,
        Url = item.Url,
        Version = item.Version,
        Updated = item.UpdatedText,
        CoverPath = item.CoverPath,
        Payload = JsonSerializer.Serialize(item),
        CachedAt = DateTime.Now
    };

    public TrainerItem? ToItem()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Payload)) return null;

            var item = JsonSerializer.Deserialize<TrainerItem>(Payload, SnapshotOpts);
            if (item is null || item.Id == 0) return null;

            // 表里的列是权威值，快照缺字段时用它们兜
            if (string.IsNullOrWhiteSpace(item.Title)) item.Title = Title;
            if (string.IsNullOrWhiteSpace(item.Url)) item.Url = Url;
            if (string.IsNullOrWhiteSpace(item.CoverPath)) item.CoverPath = CoverPath;
            return item;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SnapshotOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// 本地 SQLite 库（单例）。整个应用只持有一个 <see cref="SqlSugarScope"/>，
/// SqlSugarScope 本身是线程安全的，可以直接多线程共用。
/// </summary>
public sealed class AppDatabase
{
    private static readonly Lazy<AppDatabase> LazyInstance =
        new(() => new AppDatabase(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static AppDatabase Instance => LazyInstance.Value;

    public static string DatabaseFile => AppPaths.DatabaseFile;

    private readonly SqlSugarScope? _db;
    private readonly object _gate = new();

    /// <summary>建库/建表失败时为 false，收藏功能退化为本次会话内的内存集合。</summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// 「清除缓存」期间短暂静音：还在跑的封面下载 / 缓存写入先别落盘，
    /// 否则刚删完又被写回来。清完由调用方 <see cref="UnmuteCache"/> 恢复。
    /// </summary>
    private static CancellationTokenSource _cacheMuteCts = new();

    public static bool CacheMuted { get; private set; }

    /// <summary>静音的那一刻会取消，用来立刻掐断正在传输的图片/文件下载。</summary>
    public static CancellationToken CacheMuteToken => _cacheMuteCts.Token;

    public static void MuteCache()
    {
        CacheMuted = true;
        _cacheMuteCts.Cancel();
    }

    public static void UnmuteCache()
    {
        _cacheMuteCts.Dispose();
        _cacheMuteCts = new CancellationTokenSource();
        CacheMuted = false;
    }

    public string? LastError { get; private set; }

    private AppDatabase()
    {
        try
        {
            MigrateLegacy();

            var config = new ConnectionConfig
            {
                DbType = DbType.Sqlite,
                ConnectionString = $"DataSource={DatabaseFile}",
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute
            };

            _db = new SqlSugarScope(config);

            // 老库的 trainer_id 是 INTEGER，先改名腾地方，等新表建好再把数据搬过去
            BeginKeyMigration();

            _db.CodeFirst.InitTables<FavoriteRow>();
            _db.CodeFirst.InitTables<TrainerCacheRow>();

            MergeLeftoverTables();
            MigratePathsToRelative();

            IsAvailable = true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsAvailable = false;
        }
    }

    /// <summary>两张表的 <c>trainer_id</c> 都从 INTEGER 换成了 TEXT。</summary>
    private static readonly string[] KeyTables = { "favorite_trainer", "trainer_cache" };

    /// <summary>
    /// 旧版本的主键是整数，先改名挂起；新表由 CodeFirst 按 string 主键建出来，
    /// 再由 <see cref="MergeLeftoverTables"/> 把老数据整表搬进去。
    /// </summary>
    private void BeginKeyMigration()
    {
        if (_db is null) return;

        foreach (var table in KeyTables)
        {
            try
            {
                if (!_db.DbMaintenance.IsAnyTable(table, false)) continue;

                var cols = _db.Ado.GetDataTable($"PRAGMA table_info('{table}')");
                var type = "";
                foreach (DataRow row in cols.Rows)
                {
                    if ((string)row["name"] != "trainer_id") continue;
                    type = row["type"] as string ?? "";
                    break;
                }

                // 已经是 TEXT（或没有这一列）就什么都不用做
                if (!type.Contains("INT", StringComparison.OrdinalIgnoreCase)) continue;

                _db.Ado.ExecuteCommand($"DROP TABLE IF EXISTS \"{table}__old\"");
                _db.Ado.ExecuteCommand($"ALTER TABLE \"{table}\" RENAME TO \"{table}__old\"");
            }
            catch
            {
                // 迁移失败就别动它，旧表原样留着，后面读写出问题也只是这一张表
            }
        }
    }

    /// <summary>
    /// 把 <c>&lt;表&gt;__old</c> 里新表还没有的记录补进去，然后删掉旧表。
    /// 只取两张表都有的列（旧版本还有 title_cn / poster 这类已删掉的字段）；
    /// 用 INSERT OR IGNORE，主键已存在就跳过，重复执行也安全。
    /// </summary>
    private void MergeLeftoverTables()
    {
        if (_db is null) return;

        foreach (var table in KeyTables)
        {
            var old = $"{table}__old";
            try
            {
                if (!_db.DbMaintenance.IsAnyTable(old, false)) continue;

                var oldCols = Columns(old);
                var newCols = Columns(table).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var shared = oldCols.Where(newCols.Contains).ToList();
                if (shared.Count == 0) continue;

                var list = string.Join(", ", shared.Select(c => $"\"{c}\""));
                _db.Ado.ExecuteCommand($"INSERT OR IGNORE INTO \"{table}\" ({list}) SELECT {list} FROM \"{old}\"");
                _db.Ado.ExecuteCommand($"DROP TABLE \"{old}\"");
            }
            catch
            {
                // 搬不过去也别让程序起不来；旧表留着，人工还能查
            }
        }
    }

    private List<string> Columns(string table) =>
        _db is null
            ? new List<string>()
            : _db.Ado.GetDataTable($"PRAGMA table_info('{table}')")
                .Rows.Cast<DataRow>()
                .Select(r => (string)r["name"])
                .ToList();

    /// <summary>
    /// 老版本把 &lt;程序目录&gt; 的**绝对路径**写进了库，程序换目录就全失效。
    /// 这里统一改写成相对路径（f\&lt;id&gt;\cover.jpg），一次性迁移。
    /// </summary>
    private void MigratePathsToRelative()
    {
        if (_db is null) return;

        var root = AppPaths.BaseDir;

        foreach (var row in Run(db => db.Queryable<TrainerCacheRow>().ToList(), new List<TrainerCacheRow>()))
        {
            if (!HasRoot(row.Payload, row.CoverPath, root)) continue;
            if (row.ToItem() is not { } item) continue;
            if (!AppPaths.NormalizeInPlace(item)) continue;

            _db.Updateable<TrainerCacheRow>()
                .SetColumns(r => new TrainerCacheRow
                {
                    Payload = JsonSerializer.Serialize(item),
                    CoverPath = item.CoverPath
                })
                .Where(r => r.TrainerId == row.TrainerId)
                .ExecuteCommand();
        }

        foreach (var row in Run(db => db.Queryable<FavoriteRow>().ToList(), new List<FavoriteRow>()))
        {
            if (!HasRoot(row.Payload, "", root)) continue;
            if (row.ToItem() is not { } item) continue;
            if (!AppPaths.NormalizeInPlace(item)) continue;

            _db.Updateable<FavoriteRow>()
               .SetColumns(r => new FavoriteRow { Payload = JsonSerializer.Serialize(item) })
               .Where(r => r.TrainerId == row.TrainerId)
               .ExecuteCommand();
        }
    }

    /// <summary>粗略判断这条记录里是否还留着「程序目录的绝对路径」，避免每次启动都全表反序列化。</summary>
    private static bool HasRoot(string payload, string coverPath, string root) =>
        (!string.IsNullOrEmpty(payload) && payload.Contains(root, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrEmpty(coverPath) && coverPath.Contains(root, StringComparison.OrdinalIgnoreCase));

    /// <summary>数据库从 %LOCALAPPDATA% 挪到程序目录，老库还在就直接复制过来，别让用户白丢收藏。</summary>
    private static void MigrateLegacy()
    {
        try
        {
            var legacy = Path.Combine(AppPaths.LegacyDir, "fling.db");
            if (File.Exists(DatabaseFile) || !File.Exists(legacy)) return;

            Directory.CreateDirectory(AppPaths.DbDir);
            File.Copy(legacy, DatabaseFile);
        }
        catch
        {
            // 迁移失败就从空库开始，不影响启动
        }
    }

    private T Run<T>(Func<ISqlSugarClient, T> action, T fallback)
    {
        if (_db is null) return fallback;
        try
        {
            lock (_gate) return action(_db);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return fallback;
        }
    }

    /// <summary>按收藏时间倒序取全部收藏。</summary>
    public List<FavoriteRow> GetAll() =>
        Run(db => db.Queryable<FavoriteRow>()
            .OrderBy(r => r.CreatedAt, OrderByType.Desc)
            .ToList(), new List<FavoriteRow>());

    public bool Exists(int trainerId)
    {
        var key = trainerId.ToString();
        return Run(db => db.Queryable<FavoriteRow>().Any(r => r.TrainerId == key), false);
    }

    /// <summary>写入或更新一条收藏（主键冲突时覆盖）。</summary>
    public bool Save(TrainerItem item)
    {
        var row = FavoriteRow.From(item);
        return Run(db =>
        {
            var old = db.Queryable<FavoriteRow>().First(r => r.TrainerId == row.TrainerId);
            if (old is not null)
            {
                row.CreatedAt = old.CreatedAt;
                db.Updateable(row).ExecuteCommand();
            }
            else
            {
                db.Insertable(row).ExecuteCommand();
            }

            return true;
        }, false);
    }

    public bool Remove(int trainerId)
    {
        var key = trainerId.ToString();
        return Run(db => db.Deleteable<FavoriteRow>().Where(r => r.TrainerId == key).ExecuteCommand() > 0, false);
    }

    public int Clear() => Run(db => db.Deleteable<FavoriteRow>().ExecuteCommand(), 0);

    public int Count() => Run(db => db.Queryable<FavoriteRow>().Count(), 0);

    // ==================== 首页卡片缓存 ====================

    /// <summary>按官网修改时间倒序读出缓存的卡片，损坏的快照直接跳过。</summary>
    public List<TrainerItem> LoadCache()
    {
        var rows = Run(db => db.Queryable<TrainerCacheRow>()
            .OrderBy(r => r.CachedAt, OrderByType.Desc)
            .ToList(), new List<TrainerCacheRow>());

        var items = new List<TrainerItem>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ToItem() is { } item) items.Add(item);
        }

        // 缓存表里的先后不一定等于官网顺序，统一按官网修改时间倒序排一遍
        // （ModifiedIso 带时分秒，同一天的条目也能排对；老快照没有它就退回日期）
        return items
            .OrderByDescending(i => ParseDate(
                string.IsNullOrWhiteSpace(i.ModifiedIso) ? i.Modified : i.ModifiedIso))
            .ThenByDescending(i => i.Id)
            .ToList();
    }

    /// <summary>缓存里最新的一次写入时间，用来在界面上标「本地缓存于 …」。</summary>
    public DateTime? LatestCacheTime() =>
        Run(db => db.Queryable<TrainerCacheRow>()
            .OrderBy(r => r.CachedAt, OrderByType.Desc)
            .Select(r => (DateTime?)r.CachedAt)
            .First(), (DateTime?)null);

    public int CacheCount() => Run(db => db.Queryable<TrainerCacheRow>().Count(), 0);

    /// <summary>写入或刷新一条卡片缓存（主键冲突时覆盖）。</summary>
    public bool SaveCache(TrainerItem item)
    {
        if (item.Id <= 0 || CacheMuted) return false;
        var row = TrainerCacheRow.From(item);

        return Run(db =>
        {
            var old = db.Queryable<TrainerCacheRow>()
                .Where(r => r.TrainerId == row.TrainerId)
                .Select(r => r.TrainerId)
                .First();

            if (!string.IsNullOrEmpty(old))
                db.Updateable(row).ExecuteCommand();
            else
                db.Insertable(row).ExecuteCommand();

            return true;
        }, false);
    }

    /// <summary>批量写缓存：一次查已存在的 ID，再分别批量 insert / update。</summary>
    public int SaveCacheRange(IEnumerable<TrainerItem> items)
    {
        if (CacheMuted) return 0;

        var rows = items.Where(i => i.Id > 0).Select(TrainerCacheRow.From).ToList();
        if (rows.Count == 0) return 0;

        return Run(db =>
        {
            var ids = rows.Select(r => r.TrainerId).ToList();
            var exist = db.Queryable<TrainerCacheRow>()
                .Where(r => ids.Contains(r.TrainerId))
                .Select(r => r.TrainerId)
                .ToList()
                .ToHashSet();

            var added = rows.Where(r => !exist.Contains(r.TrainerId)).ToList();
            var updated = rows.Where(r => exist.Contains(r.TrainerId)).ToList();

            if (added.Count > 0) db.Insertable(added).ExecuteCommand();
            if (updated.Count > 0) db.Updateable(updated).ExecuteCommand();

            return rows.Count;
        }, 0);
    }

    public int ClearCache() => Run(db => db.Deleteable<TrainerCacheRow>().ExecuteCommand(), 0);

    private static DateTime ParseDate(string text) =>
        DateTime.TryParse(text?.Replace('.', '-'), out var d) ? d : DateTime.MinValue;

    /// <summary>旧版本把收藏存在 state.json 里，第一次升级时迁进来。</summary>
    public int ImportLegacy(IEnumerable<int> ids, IEnumerable<TrainerItem> library)
    {
        var byId = library.ToDictionary(i => i.Id);
        var added = 0;

        foreach (var id in ids.Distinct())
        {
            if (!byId.TryGetValue(id, out var item)) continue;
            if (Exists(id)) continue;
            if (Save(item)) added++;
        }

        return added;
    }
}
