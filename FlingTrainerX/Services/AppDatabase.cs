using System.IO;
using System.Text.Json;
using FlingTrainerX.Models;
using SqlSugar;

namespace FlingTrainerX.Services;

/// <summary>收藏夹表：一条记录就是一篇文章（一款游戏的修改器）。</summary>
[SugarTable("favorite_trainer")]
public sealed class FavoriteRow
{
    /// <summary>官网文章 ID，直接做主键，避免重复收藏。</summary>
    [SugarColumn(ColumnName = "trainer_id", IsPrimaryKey = true, IsIdentity = false)]
    public int TrainerId { get; set; }

    [SugarColumn(ColumnName = "title", Length = 256, IsNullable = true)]
    public string Title { get; set; } = "";

    [SugarColumn(ColumnName = "title_cn", Length = 128, IsNullable = true)]
    public string TitleCn { get; set; } = "";

    [SugarColumn(ColumnName = "url", Length = 512, IsNullable = true)]
    public string Url { get; set; } = "";

    [SugarColumn(ColumnName = "version", Length = 64, IsNullable = true)]
    public string Version { get; set; } = "";

    [SugarColumn(ColumnName = "option_count")]
    public int OptionCount { get; set; }

    [SugarColumn(ColumnName = "updated", Length = 32, IsNullable = true)]
    public string Updated { get; set; } = "";

    [SugarColumn(ColumnName = "poster", Length = 512, IsNullable = true)]
    public string Poster { get; set; } = "";

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
        TrainerId = item.Id,
        Title = item.Title,
        TitleCn = item.TitleCn,
        Url = item.Url,
        Version = item.Version,
        OptionCount = item.RealOptionCount,
        Updated = item.UpdatedText,
        Poster = item.Poster,
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
/// 本地 SQLite 库（单例）。整个应用只持有一个 <see cref="SqlSugarScope"/>，
/// SqlSugarScope 本身是线程安全的，可以直接多线程共用。
/// </summary>
public sealed class AppDatabase
{
    private static readonly Lazy<AppDatabase> LazyInstance =
        new(() => new AppDatabase(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static AppDatabase Instance => LazyInstance.Value;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlingTrainerX");

    public static string DatabaseFile => Path.Combine(DataDir, "fling.db");

    private readonly SqlSugarScope? _db;
    private readonly object _gate = new();

    /// <summary>建库/建表失败时为 false，收藏功能退化为本次会话内的内存集合。</summary>
    public bool IsAvailable { get; }

    public string? LastError { get; private set; }

    private AppDatabase()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            var config = new ConnectionConfig
            {
                DbType = DbType.Sqlite,
                ConnectionString = $"DataSource={DatabaseFile}",
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute
            };

            _db = new SqlSugarScope(config);
            _db.CodeFirst.InitTables<FavoriteRow>();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsAvailable = false;
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

    public bool Exists(int trainerId) =>
        Run(db => db.Queryable<FavoriteRow>().Any(r => r.TrainerId == trainerId), false);

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

    public bool Remove(int trainerId) =>
        Run(db => db.Deleteable<FavoriteRow>().Where(r => r.TrainerId == trainerId).ExecuteCommand() > 0, false);

    public int Clear() => Run(db => db.Deleteable<FavoriteRow>().ExecuteCommand(), 0);

    public int Count() => Run(db => db.Queryable<FavoriteRow>().Count(), 0);

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
