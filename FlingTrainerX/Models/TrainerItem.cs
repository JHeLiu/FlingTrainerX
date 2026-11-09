using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using FlingTrainerX.Services;

namespace FlingTrainerX.Models;

/// <summary>单个修改器选项，例如 "Ctrl+Num 1 – Edit Gold"。</summary>
public sealed class TrainerOption
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

/// <summary>官网详情页 Download 表格里的一行文件。</summary>
public sealed class TrainerDownload
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("size")] public string Size { get; set; } = "";
    [JsonPropertyName("count")] public string Count { get; set; } = "";
    [JsonPropertyName("ext")] public string Ext { get; set; } = "";

    /// <summary>
    /// 下载到本机的文件，存的是**相对程序目录**的路径（<c>f\&lt;id&gt;\文件名</c>），
    /// 换目录、换盘符后依然有效；没下过就是空。跟着缓存表一起存。
    /// </summary>
    [JsonPropertyName("localPath")] public string LocalPath { get; set; } = "";

    /// <summary>
    /// 上一次真正下下来时的字节数。官网的响应是 chunked、不给 Content-Length，
    /// 有了这个数下次下载就能直接显示精确进度（表格里的「大小」是压缩包的大小，会偏小）。
    /// </summary>
    [JsonPropertyName("actualBytes")] public long ActualBytes { get; set; }

    /// <summary>拼上当前程序目录后的绝对路径，运行 / 定位文件时用。</summary>
    [JsonIgnore] public string FullPath => AppPaths.Resolve(LocalPath);

    /// <summary>本地文件还在才认，避免用户手工删掉后界面上还写着「已下载」。</summary>
    [JsonIgnore]
    public bool HasLocalFile
    {
        get
        {
            try
            {
                // 注意：类里有 File 属性，这里必须写全名，否则会解析到属性上
                return !string.IsNullOrWhiteSpace(LocalPath) && System.IO.File.Exists(FullPath);
            }
            catch
            {
                return false;
            }
        }
    }

    [JsonIgnore] public string LocalFileName => HasLocalFile ? Path.GetFileName(FullPath) : "";

    /// <summary>下载次数，按千分位显示；解析失败时原样返回。</summary>
    [JsonIgnore]
    public string CountText =>
        long.TryParse(Count, out var n) ? n.ToString("N0") : (string.IsNullOrWhiteSpace(Count) ? "—" : Count);

    /// <summary>去掉站点域名后的下载地址，界面里作为副标题显示。</summary>
    [JsonIgnore]
    public string ShortUrl =>
        Url.Replace("https://flingtrainer.com/", "").Replace("http://flingtrainer.com/", "");

    [JsonIgnore] public string ExtText => string.IsNullOrWhiteSpace(Ext) ? "file" : Ext.ToUpperInvariant();
}

/// <summary>官网详细页里的一个选项分组。</summary>
public sealed class TrainerGroup
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("items")] public List<TrainerOption> Items { get; set; } = new();

    [JsonIgnore] public int Count => Items.Count;
}

/// <summary>一款游戏的修改器（对应 flingtrainer.com 上的一篇文章）。</summary>
public sealed class TrainerItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("published")] public string Published { get; set; } = "";
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("modified")] public string Modified { get; set; } = "";

    /// <summary>官网原始的修改时间（带时分秒），缓存排序用；界面上显示的是 <see cref="Modified"/>。</summary>
    [JsonPropertyName("modifiedIso")] public string ModifiedIso { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("platforms")] public List<string> Platforms { get; set; } = new();
    [JsonPropertyName("optionCount")] public int OptionCount { get; set; }
    [JsonPropertyName("image")] public string Image { get; set; } = "";

    /// <summary>官网头图（接口特色图，520×292 宽幅艺术图），卡片的正式封面。</summary>
    [JsonPropertyName("cover")] public string Cover { get; set; } = "";
    [JsonPropertyName("imageUrl")] public string ImageUrl { get; set; } = "";
    [JsonPropertyName("images")] public List<string> Images { get; set; } = new();

    private string _coverPath = "";

    /// <summary>
    /// 封面在本机的路径，存相对程序目录的路径（<c>f\&lt;id&gt;\cover.jpg</c>）。有了它就不再走网络。
    /// </summary>
    [JsonPropertyName("coverPath")]
    public string CoverPath
    {
        get => _coverPath;
        set
        {
            if (_coverPath == value) return;
            _coverPath = value ?? "";
            Raise(nameof(CoverPath));
            Raise(nameof(CoverSource));
            Raise(nameof(HasCover));
            Raise(nameof(BannerSource));
        }
    }

    /// <summary>封面在本机的绝对路径（拼上当前程序目录）。</summary>
    [JsonIgnore] public string CoverFullPath => AppPaths.Resolve(_coverPath);

    private List<string> _imagePaths = new();

    /// <summary>
    /// 介绍截图在本机的相对路径，和 <see cref="Images"/> 一一对应（下载失败的位置留空）。
    /// </summary>
    [JsonPropertyName("imagePaths")]
    public List<string> ImagePaths
    {
        get => _imagePaths;
        set
        {
            _imagePaths = value ?? new();
            Raise(nameof(ImagePaths));
        }
    }
    [JsonPropertyName("downloads")] public List<TrainerDownload> Downloads { get; set; } = new();
    [JsonPropertyName("groups")] public List<TrainerGroup> Groups { get; set; } = new();

    [JsonIgnore] public bool HasDownloads => Downloads.Count > 0;

    /// <summary>没有封面地址时卡片上的水印字母。</summary>
    [JsonIgnore]
    public string Initials
    {
        get
        {
            var words = CleanTitle.Split(new[] { ' ', ':', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            if (words.Length == 1) return words[0][..1].ToUpperInvariant();
            return (words[0][..1] + words[1][..1]).ToUpperInvariant();
        }
    }

    [JsonIgnore]
    public string PlatformText => Platforms is { Count: > 0 }
        ? string.Join(" · ", Platforms)
        : "";

    /// <summary>
    /// 卡片标题用中文还是英文（全局开关，设置栏那个「当前游戏名」下拉控制，默认英文）。
    /// 切语言时各卡片重新取一次 <see cref="DisplayTitle"/> 即可。
    /// </summary>
    public static bool UseChineseTitle { get; set; }

    private string _nameCn = "";

    /// <summary>
    /// 从 game_names 表左连过来的简体中文名；查不到就是空串，标题自动退回官网英文名。
    /// </summary>
    [JsonIgnore]
    public string NameCn
    {
        get => _nameCn;
        set
        {
            var text = value ?? "";
            if (_nameCn == text) return;
            _nameCn = text;
            Raise(nameof(NameCn));
            Raise(nameof(DisplayTitle));
        }
    }

    /// <summary>
    /// 卡片/标题上显示的主标题：跟着全局语言开关走，只显示一种语言。
    /// 选了中文但这款没有中文名时，退回官网英文标题（以游戏数据表为准）。
    /// </summary>
    [JsonIgnore]
    public string DisplayTitle =>
        UseChineseTitle && _nameCn.Length > 0 ? _nameCn : CleanTitle;

    [JsonIgnore] public string CleanTitle =>
        Title.Replace(" Trainer", "").Replace(" Enhanced", "").Trim();

    /// <summary>官网标注的最近更新时间（YYYY.MM.DD）。</summary>
    [JsonIgnore] public string UpdatedText => string.IsNullOrWhiteSpace(Updated) ? Modified : Updated;

    /// <summary>卡片标题下方的更新时间标签，例如 "2026.10.31"（完整日期，不省年份）。</summary>
    [JsonIgnore]
    public string UpdatedShort
    {
        get
        {
            var t = UpdatedText.Replace('.', '-').Replace('/', '-');
            return DateTime.TryParse(t, out var d) ? d.ToString("yyyy.MM.dd") : UpdatedText;
        }
    }

    [JsonIgnore] public bool IsRecent =>
        DateTime.TryParse(UpdatedText.Replace('.', '-'), out var d) && (DateTime.Now - d).TotalDays <= 7;

    /// <summary>
    /// 卡片封面：优先用 f\&lt;id&gt;\ 里已经存好的本地图（存的是相对路径，这里拼成绝对路径），
    /// 没有才退回官网地址。两种地址都交给同一个图片加载器，界面侧不用区分。
    /// </summary>
    [JsonIgnore]
    public string CoverSource
    {
        get
        {
            if (LocalExists(CoverPath)) return CoverFullPath;
            if (!string.IsNullOrWhiteSpace(Cover)) return Cover;
            if (!string.IsNullOrWhiteSpace(ImageUrl)) return ImageUrl;
            return Image;
        }
    }

    [JsonIgnore] public bool HasCover => !string.IsNullOrWhiteSpace(CoverSource);

    /// <summary>封面是否已经落到本地（界面上用来显示「已缓存」角标）。</summary>
    [JsonIgnore] public bool HasLocalCover => LocalExists(CoverPath);

    private static bool LocalExists(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(AppPaths.Resolve(path));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>给 <see cref="CoverPath"/> / <see cref="ImagePaths"/> 这类运行时才变的字段发通知。</summary>
    public void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [JsonIgnore] public string OptionCountText => $"{OptionCount} 项修改";

    /// <summary>卡片标题区那个小标签的文字，例如 "30项"；没有数据时为空串（标签自动隐藏）。</summary>
    [JsonIgnore] public string OptionCountTag => RealOptionCount > 0 ? $"{RealOptionCount}项" : "";

    [JsonIgnore] public int RealOptionCount =>
        OptionCount > 0 ? OptionCount : Groups.Sum(g => g.Items.Count);

    /// <summary>详情页顶部的横幅：官网头图，没有才退回正文截图。</summary>
    [JsonIgnore]
    public string BannerSource => CoverSource;

    [JsonIgnore]
    public string DownloadSummary => Downloads.Count == 0 ? "暂无下载" : $"{Downloads.Count} 个文件";

    [JsonIgnore]
    public string LatestDownloadDate => Downloads.Count == 0 ? "—" : Downloads
        .Select(d => d.Date)
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .OrderByDescending(d => d, StringComparer.Ordinal)
        .FirstOrDefault() ?? "—";
}

/// <summary>内存中的游戏库（全部来自官网实时抓取，不落盘）。</summary>
public sealed class TrainerLibrary
{
    [JsonPropertyName("source")] public string Source { get; set; } = "https://flingtrainer.com/";
    [JsonPropertyName("fetchedAt")] public string FetchedAt { get; set; } = "";
    [JsonPropertyName("pages")] public int Pages { get; set; } = 1;
    [JsonPropertyName("items")] public List<TrainerItem> Items { get; set; } = new();
}
