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
/// 游戏名库表：官网「All Trainers (A-Z)」里每款游戏一行。
/// <para>
/// <see cref="NameEn"/> 是官网原始标题（默认只存它）；<see cref="NameCn"/> 是从 Steam 商店换来的
/// 简体中文名，一开始为空，点「同步」时才批量补。搜索框的智能提示走这张表（本地 SQLite），
/// 真正的搜索仍然发给官网接口。
/// </para>
/// </summary>
[SugarTable("game_names")]
public sealed class GameNameRow
{
    /// <summary>
    /// 官网唯一键。新数据是链接里的 slug（<c>ace-combat-8-wings-of-theve-trainer</c>），
    /// 老数据（以及接口兜底抓来的）是数字文章 ID。
    /// <para>
    /// 只当<b>不透明主键</b>用：查提示、补中文名都不需要它是数字，别拿它做数值比较；
    /// 缓存表 / 收藏表里的 ID 是另一套，靠标题或 slug 对，不要直接拿这列去 join。
    /// </para>
    /// </summary>
    [SugarColumn(ColumnName = "trainer_id", Length = 64, IsPrimaryKey = true, IsIdentity = false)]
    public string TrainerId { get; set; } = "";

    /// <summary>官网原始标题，例如 "Assassin's Creed Mirage Trainer"。</summary>
    [SugarColumn(ColumnName = "name_en", Length = 256, IsNullable = true)]
    public string NameEn { get; set; } = "";

    /// <summary>Steam 商店上的简体中文名；没有（Steam 搜不到 / 没出中文）就是空。</summary>
    [SugarColumn(ColumnName = "name_cn", Length = 256, IsNullable = true)]
    public string NameCn { get; set; } = "";

    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>去掉 " Trainer" / " Enhanced" 后的游戏名，提示列表展示和回填搜索框都用它。</summary>
    [SugarColumn(IsIgnore = true)]
    public string CleanEn => NameEn.Replace(" Trainer", "").Replace(" Enhanced", "").Trim();

    /// <summary>
    /// 从官网链接里取唯一键：<c>https://flingtrainer.com/trainer/elden-ring-trainer/</c> → <c>elden-ring-trainer</c>。
    /// 取不到（空链接 / 没有路径）返回空串，调用方自己决定要不要落库。
    /// </summary>
    public static string KeyOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        var text = url.Trim();
        var cut = text.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) text = text[..cut];
        text = text.TrimEnd('/');

        var slash = text.LastIndexOf('/');
        return slash >= 0 && slash + 1 < text.Length ? text[(slash + 1)..] : "";
    }
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
            _db.CodeFirst.InitTables<GameNameRow>();

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

    // ==================== 游戏名库（官网 A-Z 全量 + Steam 中文名） ====================

    /// <summary>游戏名库里有多少条；启动时用它判断「要不要重新抓一次全量」。</summary>
    public int GameNameCount() => Run(db => db.Queryable<GameNameRow>().Count(), 0);

    /// <summary>已经有中文名的条数（同步进度 / 自检用）。</summary>
    public int GameNameCnCount() =>
        Run(db => db.Queryable<GameNameRow>().Where(r => r.NameCn != "" && r.NameCn != null).Count(), 0);

    /// <summary>清空游戏名库（只在「已经不再需要旧数据」时用，正常重建走 <see cref="RebuildGameNames"/>）。</summary>
    public int ClearGameNames() => Run(db => db.Deleteable<GameNameRow>().ExecuteCommand(), 0);

    /// <summary>
    /// 用官网新名单重建游戏名表，<b>但把已经换到的中文名原样带过去</b>。
    /// 之前「重新抓取完整名单」是清表重写，396 个中文名跟着一起没了、又要从头问一遍 Steam；
    /// 现在只换名单本身：新条目中文名为空，老条目继续用原来的中文名，官网已经下架的自然消失。
    /// <para>
    /// 中文名先按主键对；主键对不上（老数据是数字 ID、新名单是 slug，或者两种来源混着）时
    /// 再按英文标题对一次 —— 同一条游戏换了个键不该把问好的中文名丢掉。
    /// </para>
    /// 返回保留下来的中文名条数。
    /// </summary>
    public int RebuildGameNames(List<GameNameRow> fresh)
    {
        var list = fresh.Where(r => !string.IsNullOrEmpty(r.TrainerId)).ToList();
        if (list.Count == 0) return 0;

        return Run(db =>
        {
            var withCn = db.Queryable<GameNameRow>()
                .Where(r => r.NameCn != "" && r.NameCn != null)
                .ToList();

            var cnById = withCn
                .GroupBy(r => r.TrainerId)
                .ToDictionary(g => g.Key, g => g.First().NameCn);

            var cnByName = withCn
                .GroupBy(r => r.NameEn.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().NameCn, StringComparer.OrdinalIgnoreCase);

            var kept = 0;
            foreach (var row in list)
            {
                var cn = "";
                if (!cnById.TryGetValue(row.TrainerId, out cn) || string.IsNullOrEmpty(cn))
                    cnByName.TryGetValue(row.NameEn.Trim(), out cn);

                if (string.IsNullOrEmpty(cn)) continue;

                row.NameCn = cn;
                kept++;
            }

            db.Deleteable<GameNameRow>().ExecuteCommand();

            for (var i = 0; i < list.Count; i += 200)
                db.Insertable(list.Skip(i).Take(200).ToList()).ExecuteCommand();

            return kept;
        }, 0);
    }

    /// <summary>还没有中文名的那些行：后台「慢慢补齐」时只拿它们去问 Steam，
    /// 已经有中文名的就不重复请求了。
    /// </summary>
    public List<GameNameRow> LoadGameNamesMissingCn() =>
        Run(db => db.Queryable<GameNameRow>()
            .Where(r => r.NameCn == "" || r.NameCn == null)
            .OrderBy(r => r.NameEn)
            .ToList(), new List<GameNameRow>());

    /// <summary>
    /// 按官网链接批量取中文名（卡片标题切中文时用）。
    /// 一次 IN 查询取回当前这一页的十几条，键是 <see cref="GameNameRow.KeyOf"/> 算出来的 slug；
    /// 查不到的那些不会出现在结果里，调用方把标题退回游戏数据里的英文名即可。
    /// </summary>
    public Dictionary<string, string> LoadChineseNames(IEnumerable<string> urls)
    {
        var keys = urls
            .Select(GameNameRow.KeyOf)
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();

        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) return empty;

        return Run(db =>
        {
            var rows = db.Queryable<GameNameRow>()
                .Where(r => keys.Contains(r.TrainerId))
                .Where(r => r.NameCn != "" && r.NameCn != null)
                .Select(r => new { r.TrainerId, r.NameCn })
                .ToList();

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var cn = row.NameCn?.Trim() ?? "";
                if (cn.Length > 0) map[row.TrainerId] = cn;
            }

            return map;
        }, empty);
    }

    /// <summary>批量写入全量游戏名（调用前一般刚清空过表，直接 insert）。</summary>
    public int SaveGameNames(IEnumerable<GameNameRow> rows)
    {
        var list = rows.Where(r => !string.IsNullOrEmpty(r.TrainerId)).ToList();
        if (list.Count == 0) return 0;

        return Run(db =>
        {
            // 一批最多 200 条：SQLite 单条语句的参数有上限，别一口气塞 700 多个
            for (var i = 0; i < list.Count; i += 200)
                db.Insertable(list.Skip(i).Take(200).ToList()).ExecuteCommand();
            return list.Count;
        }, 0);
    }

    /// <summary>
    /// 把官网搜索 / 同步抓到的游戏补进游戏名库：已存在的行一律不动（中文名不会丢），
    /// 只补没有的。这样下次再搜同一个游戏就能直接在本地命中，不用再联网。
    /// </summary>
    /// <param name="items">刚抓到的条目。</param>
    public int AddGameNamesIfMissing(IEnumerable<TrainerItem> items)
    {
        var rows = items
            .Where(i => i.Id > 0 && !string.IsNullOrWhiteSpace(i.Title))
            .Select(i =>
            {
                // 和 A-Z 名单用同一套键：链接里的 slug，取不到才退回文章 ID
                var key = GameNameRow.KeyOf(i.Url);
                return new GameNameRow
                {
                    TrainerId = key.Length > 0 ? key : i.Id.ToString(),
                    NameEn = i.Title,
                    NameCn = "",
                    UpdatedAt = DateTime.Now
                };
            })
            .GroupBy(r => r.TrainerId)
            .Select(g => g.First())
            .ToList();

        if (rows.Count == 0) return 0;

        return Run(db =>
        {
            var keys = rows.Select(r => r.TrainerId).ToList();
            var names = rows.Select(r => r.NameEn.Trim()).ToList();

            var existKeys = db.Queryable<GameNameRow>()
                .Where(r => keys.Contains(r.TrainerId))
                .Select(r => r.TrainerId)
                .ToList()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 键不同但标题一样的也算「已经有了」：老库是数字 ID、新名单是 slug，
            // 不按标题再挡一次，同一款游戏会在提示里出现两遍
            var existNames = db.Queryable<GameNameRow>()
                .Where(r => names.Contains(r.NameEn))
                .Select(r => r.NameEn)
                .ToList()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = rows
                .Where(r => !existKeys.Contains(r.TrainerId) && !existNames.Contains(r.NameEn.Trim()))
                .ToList();

            if (added.Count == 0) return 0;

            db.Insertable(added).ExecuteCommand();
            return added.Count;
        }, 0);
    }

    /// <summary>
    /// 本地搜索：官网标题（英文名）或已换到的中文名里包含关键词的缓存条目。
    /// 搜索框提交时先用它，命中就直接给本地结果、不联网；最多返回 <paramref name="limit"/> 条。
    /// </summary>
    /// <param name="keyword">关键词，中文 / 英文都行。</param>
    /// <param name="limit">最多返回多少条。</param>
    public List<TrainerItem> SearchCache(string keyword, int limit)
    {
        var kw = keyword.Trim();
        if (kw.Length == 0 || limit <= 0) return new List<TrainerItem>();

        // ① 官网标题直接命中
        var rows = Run(db => db.Queryable<TrainerCacheRow>()
            .Where(r => r.Title.Contains(kw))
            .OrderBy(r => r.Title)
            .Take(limit)
            .ToList(), new List<TrainerCacheRow>());

        // ② 中文名 / 英文名命中：先从游戏名库拿到英文标题，再按标题从缓存表取整条。
        //    游戏名库的键是官网 slug（老数据是数字 ID），和缓存表的主键不是一套，所以这里按标题对，
        //    比按主键 join 更经得住「名单换键」这种事。
        var hitTitles = Run(db => db.Queryable<GameNameRow>()
            .Where(r => r.NameEn.Contains(kw) || r.NameCn.Contains(kw))
            .OrderBy(r => r.NameEn)
            .Take(limit)
            .Select(r => r.NameEn)
            .ToList(), new List<string>());

        var have = rows.Select(r => r.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = hitTitles
            .Where(t => !string.IsNullOrWhiteSpace(t) && !have.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count > 0)
        {
            rows.AddRange(Run(db => db.Queryable<TrainerCacheRow>()
                .Where(r => missing.Contains(r.Title))
                .ToList(), new List<TrainerCacheRow>()));
        }

        var items = new List<TrainerItem>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ToItem() is { } item) items.Add(item);
        }

        return items
            .OrderBy(i => i.CleanTitle, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>给某一款游戏补上中文名（同步过程中来一个写一个，取消时已写的不丢）。</summary>
    public bool UpdateGameNameCn(string trainerId, string nameCn) => Run(db =>
        db.Updateable<GameNameRow>()
            .SetColumns(r => new GameNameRow { NameCn = nameCn, UpdatedAt = DateTime.Now })
            .Where(r => r.TrainerId == trainerId)
            .ExecuteCommand() > 0, false);

    /// <summary>
    /// 中文名反查英文名：官网是英文站，中文关键词直接发过去必然搜不到，
    /// 所以搜索前先用中文名在本地库里换成英文名，再用英文名去官网搜。
    /// 先认完全相同的，再退化到「包含」（输入「赛博朋克」也能命中「赛博朋克 2077」）；
    /// 多个候选时取中文名最短、英文名最短的那条 —— 「艾尔登法环」应该落在
    /// Elden Ring 本体上，而不是中文名同样叫这个的资料片（Nightreign）。
    /// 返回的是去掉 " Trainer" 的干净名字，搜出来更准。
    /// </summary>
    public string? FindEnglishByChinese(string chineseName)
    {
        var key = chineseName.Trim();
        if (key.Length == 0) return null;

        return Run(db =>
        {
            var rows = db.Queryable<GameNameRow>()
                .Where(r => r.NameCn != "" && r.NameCn != null && r.NameCn.Contains(key))
                .Take(60)
                .ToList();

            if (rows.Count == 0) return null;

            var exact = rows
                .Where(r => string.Equals(r.NameCn.Trim(), key, StringComparison.Ordinal))
                .OrderBy(r => r.NameEn.Length)
                .ThenBy(r => r.NameEn, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (exact is not null) return exact.CleanEn;

            return rows.OrderBy(r => r.NameCn.Length)
                .ThenBy(r => r.NameEn.Length)
                .ThenBy(r => r.NameEn, StringComparer.OrdinalIgnoreCase)
                .First().CleanEn;
        }, null);
    }

    /// <summary>
    /// 智能提示用：英文名 / 中文名里包含关键词的行。英文、中文都认；
    /// 前缀命中的排前面，再按字母序，最多 <paramref name="limit"/> 条。
    /// </summary>
    public List<GameNameRow> SearchGameNames(string keyword, int limit)
    {
        var kw = keyword.Trim();
        if (kw.Length == 0 || limit <= 0) return new List<GameNameRow>();

        var rows = Run(db => db.Queryable<GameNameRow>()
            .Where(r => r.NameEn.Contains(kw) || r.NameCn.Contains(kw))
            .OrderBy(r => r.NameEn)
            .Take(60)
            .ToList(), new List<GameNameRow>());

        return rows
            .OrderByDescending(r => StartsWith(r.CleanEn, kw))
            .ThenByDescending(r => StartsWith(r.NameCn, kw))
            .ThenBy(r => r.CleanEn, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        static bool StartsWith(string text, string prefix) => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ParseDate(string text) =>
        DateTime.TryParse(text?.Replace('.', '-'), out var d) ? d : DateTime.MinValue;
}
