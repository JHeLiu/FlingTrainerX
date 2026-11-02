using System.ComponentModel;
using System.Text.Json.Serialization;
using FlingTrainerX.Services;

namespace FlingTrainerX.Models;

/// <summary>单个修改器选项，例如 "Ctrl+Num 1 – Edit Gold"。</summary>
public sealed class TrainerOption
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>界面显示用的中文名（对照表 → 规则拼接 → 原文兜底）。</summary>
    [JsonIgnore] public string NameCn => TrainerZh.Of(Name);
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

    /// <summary>分组标题的中文（"常规" 这类本来就是中文的直接返回）。</summary>
    [JsonIgnore] public string TitleCn => TrainerZh.Of(Title);
}

/// <summary>一款游戏的修改器（对应 flingtrainer.com 上的一篇文章）。</summary>
public sealed class TrainerItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>后台把封面缓存到本地后调用，卡片会就地刷新图片。</summary>
    public void SetPoster(string path)
    {
        Poster = path;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Poster)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPoster)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverSource)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOfficialPoster)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScreenshotCover)));
    }

    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("titleCn")] public string TitleCn { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("published")] public string Published { get; set; } = "";
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("modified")] public string Modified { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("platforms")] public List<string> Platforms { get; set; } = new();
    [JsonPropertyName("optionCount")] public int OptionCount { get; set; }
    [JsonPropertyName("image")] public string Image { get; set; } = "";
    [JsonPropertyName("poster")] public string Poster { get; set; } = "";
    [JsonPropertyName("imageUrl")] public string ImageUrl { get; set; } = "";
    [JsonPropertyName("images")] public List<string> Images { get; set; } = new();
    [JsonPropertyName("downloads")] public List<TrainerDownload> Downloads { get; set; } = new();
    [JsonPropertyName("groups")] public List<TrainerGroup> Groups { get; set; } = new();

    [JsonIgnore] public bool HasCn => !string.IsNullOrWhiteSpace(TitleCn);
    [JsonIgnore] public bool HasPoster => !string.IsNullOrWhiteSpace(Poster);
    [JsonIgnore] public bool HasDownloads => Downloads.Count > 0;

    /// <summary>封面是不是 Steam 官方竖版海报（Pack 目录里的）。</summary>
    [JsonIgnore]
    public bool IsOfficialPoster =>
        HasPoster && Poster.Replace('\\', '/').StartsWith("Posters/", StringComparison.OrdinalIgnoreCase);

    /// <summary>封面是官网截图兜底时，卡片会额外压暗一层，保证标题可读。</summary>
    [JsonIgnore] public bool IsScreenshotCover => HasPoster && !IsOfficialPoster;

    /// <summary>没有封面时卡片上的水印字母。</summary>
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
        ? string.Join(" · ", Platforms.Select(TrainerZh.Platform))
        : "";

    /// <summary>卡片/标题上显示的主标题：优先中文名。</summary>
    [JsonIgnore] public string DisplayTitle => HasCn ? TitleCn : CleanTitle;

    /// <summary>副标题：有中文名时显示官网原名。</summary>
    [JsonIgnore] public string SubTitle => HasCn ? CleanTitle : "FLiNG 修改器";

    [JsonIgnore] public string CleanTitle =>
        Title.Replace(" Trainer", "").Replace(" Enhanced", "").Trim();

    /// <summary>官网标注的最近更新时间（YYYY.MM.DD）。</summary>
    [JsonIgnore] public string UpdatedText => string.IsNullOrWhiteSpace(Updated) ? Modified : Updated;

    /// <summary>卡片右上角的小角标，例如 "10.31"。</summary>
    [JsonIgnore] public string UpdatedShort
    {
        get
        {
            var t = UpdatedText.Replace('.', '-').Replace('/', '-');
            return DateTime.TryParse(t, out var d) ? d.ToString("MM.dd") : UpdatedText;
        }
    }

    [JsonIgnore] public bool IsRecent =>
        DateTime.TryParse(UpdatedText.Replace('.', '-'), out var d) && (DateTime.Now - d).TotalDays <= 7;

    /// <summary>卡片封面：优先竖版海报，其次本地缓存的官网截图（绝不返回网络地址，避免滚动时卡顿）。</summary>
    [JsonIgnore]
    public string CoverSource =>
        HasPoster ? Poster : (string.IsNullOrEmpty(Image) ? "" : Image);

    [JsonIgnore] public string OptionCountText => $"{OptionCount} 项修改";

    [JsonIgnore] public int RealOptionCount =>
        OptionCount > 0 ? OptionCount : Groups.Sum(g => g.Items.Count);

    /// <summary>详情页顶部的横幅：优先竖版海报，其次本地截图，最后官网图。</summary>
    [JsonIgnore]
    public string BannerSource =>
        HasPoster ? Poster : (!string.IsNullOrWhiteSpace(Image) ? Image : ImageUrl);

    /// <summary>官网正文里的介绍截图：优先本地缓存（内置数据自带），否则用正文里的网络图。</summary>
    [JsonIgnore]
    public string IntroImage =>
        !string.IsNullOrWhiteSpace(Image)
            ? Image
            : (Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) ?? ImageUrl);

    [JsonIgnore] public bool HasIntroImage => !string.IsNullOrWhiteSpace(IntroImage);

    [JsonIgnore]
    public string DownloadSummary => Downloads.Count == 0 ? "暂无下载" : $"{Downloads.Count} 个文件";

    [JsonIgnore]
    public string LatestDownloadDate => Downloads.Count == 0 ? "—" : Downloads
        .Select(d => d.Date)
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .OrderByDescending(d => d, StringComparer.Ordinal)
        .FirstOrDefault() ?? "—";
}

/// <summary>trainers.json 的根对象。</summary>
public sealed class TrainerLibrary
{
    [JsonPropertyName("source")] public string Source { get; set; } = "https://flingtrainer.com/";
    [JsonPropertyName("fetchedAt")] public string FetchedAt { get; set; } = "";
    [JsonPropertyName("pages")] public int Pages { get; set; } = 1;
    [JsonPropertyName("items")] public List<TrainerItem> Items { get; set; } = new();
}
