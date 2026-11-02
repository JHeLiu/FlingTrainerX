using System.Net.Http;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>
/// 从 flingtrainer.com 抓取文章并解析。所有 HTML 都用 AngleSharp 走 DOM，
/// 只有「拼 URL / 拆版本号」这类纯字符串处理，不再用正则去啃页面结构。
/// </summary>
public static class TrainerWeb
{
    public const string Site = "https://flingtrainer.com/";

    /// <summary>官网首页每页 24 条，翻页地址是 /page/2/，API 对应 page=2。</summary>
    public const int PageSize = 24;

    private const string ApiBase = "https://flingtrainer.com/wp-json/wp/v2/posts";

    private static readonly HtmlParser Parser = new();

    private static string EndpointOf(int page) =>
        $"{ApiBase}?per_page={PageSize}&page={page}&orderby=modified&order=desc" +
        "&_fields=id,date,modified,link,title,content";

    private static readonly string[] Platforms =
    {
        "Steam", "Epic Games Store", "GOG", "Xbox/Game Pass", "EA App", "Ubisoft Connect", "Battle.net"
    };

    private static HttpClient? _http;

    private static HttpClient Http => _http ??= CreateClient();

    /// <summary>官网有 UA 校验，这里用本机 Edge 的真实版本拼 UA（见 <see cref="UserAgentProvider"/>）。</summary>
    private static HttpClient CreateClient() => UserAgentProvider.CreateHttpClient(Site);

    private static HttpRequestMessage Request(string url, bool json)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        UserAgentProvider.ApplyHeaders(req, Site);
        if (json)
        {
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        }

        return req;
    }

    public static async Task<List<TrainerItem>> FetchAsync(CancellationToken ct = default)
    {
        var (items, _) = await FetchPageAsync(1, ct).ConfigureAwait(false);
        return items;
    }

    /// <summary>抓取官网第 <paramref name="page"/> 页（1 起），返回文章与总页数。</summary>
    public static async Task<(List<TrainerItem> Items, int TotalPages)> FetchPageAsync(
        int page, CancellationToken ct = default)
    {
        using var request = Request(EndpointOf(page), true);
        using var res = await Http.SendAsync(request, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        var total = 1;
        if (res.Headers.TryGetValues("X-WP-TotalPages", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var t))
        {
            total = t;
        }

        var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (Parse(json), total);
    }

    /// <summary>
    /// 抓取官网详情页，解析 Download 表格与正文介绍图。
    /// （WordPress 正文接口不含下载区，下载表格由插件在页面里渲染。）
    /// </summary>
    public static async Task<(List<TrainerDownload> Downloads, List<string> Images)> FetchDetailAsync(
        string url, CancellationToken ct = default)
    {
        using var request = Request(url, false);
        using var res = await Http.SendAsync(request, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        var html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (ParseDownloads(html), ParseImages(html));
    }

    // ==================== 下载表格 ====================

    /// <summary>解析详情页里的 .download-attachments 表格。</summary>
    public static List<TrainerDownload> ParseDownloads(string html)
    {
        var list = new List<TrainerDownload>();

        var doc = Parser.ParseDocument(html);
        var table = doc.QuerySelector("table.download-attachments") ?? doc.QuerySelector(".download-attachments");
        if (table is null) return list;

        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var link = row.QuerySelector("a[href]");
            if (link is null) continue;

            var name = Collapse(link.TextContent);
            var title = Collapse(link.GetAttribute("title"));
            if (name.Length == 0) name = title;
            if (name.Length == 0) continue;

            // 单元格顺序：文件名 / 添加日期 / 大小 / 下载量，空单元格直接丢掉
            var cells = row.QuerySelectorAll("td")
                .Select(td => Collapse(td.TextContent))
                .Where(t => t.Length > 0)
                .ToList();

            list.Add(new TrainerDownload
            {
                File = name,
                Url = Abs(link.GetAttribute("href") ?? ""),
                Date = cells.Count > 1 ? cells[1] : "",
                Size = cells.Count > 2 ? cells[2] : "",
                Count = cells.Count > 3 ? cells[3] : "",
                Ext = ExtOf(row),
            });
        }

        return list;
    }

    /// <summary>行内图标是 /images/ext/zip.png 这种，取扩展名用。</summary>
    private static string ExtOf(IElement row)
    {
        var src = row.QuerySelector("img[src*='/ext/']")?.GetAttribute("src");
        if (string.IsNullOrEmpty(src)) return "zip";

        var cut = src.LastIndexOf('/');
        var file = cut >= 0 ? src[(cut + 1)..] : src;
        var dot = file.IndexOf('.');
        return dot > 0 ? file[..dot].ToLowerInvariant() : "zip";
    }

    // ==================== 图片 ====================

    /// <summary>正文里的介绍截图（排除 12px 的工具提示小图标），优先用 768 宽的缩略版本。</summary>
    public static List<string> ParseImages(string html)
    {
        var list = new List<string>();

        foreach (var img in Parser.ParseDocument(html).QuerySelectorAll("img"))
        {
            var src = img.GetAttribute("src") ?? img.GetAttribute("data-src");
            if (string.IsNullOrWhiteSpace(src)) continue;

            if (src.Contains("tooltip", StringComparison.OrdinalIgnoreCase)) continue;

            if (int.TryParse(img.GetAttribute("width"), out var px) && px < 200) continue;

            var url = Abs(MidSizeVariant(img.GetAttribute("srcset")) ?? src);
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!list.Contains(url)) list.Add(url);
        }

        return list;
    }

    /// <summary>从 srcset 里挑 -768x 那一档，详情页和卡片都不需要原图。</summary>
    private static string? MidSizeVariant(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset)) return null;

        foreach (var candidate in srcset.Split(','))
        {
            var url = candidate.Trim().Split(' ')[0];
            if (url.Contains("-768x", StringComparison.OrdinalIgnoreCase)) return url;
        }

        return null;
    }

    private static string Abs(string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
        if (url.StartsWith('/')) return Site.TrimEnd('/') + url;
        return url;
    }

    // ==================== 列表 ====================

    public static List<TrainerItem> Parse(string json)
    {
        var list = new List<TrainerItem>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var post in doc.RootElement.EnumerateArray())
        {
            var content = post.GetProperty("content").GetProperty("rendered").GetString() ?? "";
            var title = HtmlText(post.GetProperty("title").GetProperty("rendered").GetString() ?? "");

            var item = new TrainerItem
            {
                Id = post.GetProperty("id").GetInt32(),
                Slug = SlugOf(post.GetProperty("link").GetString() ?? ""),
                Title = title,
                Url = post.GetProperty("link").GetString() ?? "",
                Published = Short(post.GetProperty("date").GetString()),
                Modified = Short(post.GetProperty("modified").GetString()),
            };

            var meta = ParseMeta(content);
            item.OptionCount = meta.Count;
            item.Version = meta.Version;
            item.Platforms = meta.Platforms;
            item.Updated = meta.Updated.Length > 0 ? meta.Updated : item.Modified;

            item.Images = ParseImages(content);
            item.ImageUrl = item.Images.FirstOrDefault() ?? "";

            item.Groups = ParseGroups(content);
            list.Add(item);
        }

        return list;
    }

    /// <summary>
    /// 把正文包进一个容器再解析，并去掉脚本 / 样式 / 内联图标。
    /// 官网在选项后面塞了 jQuery 的 toolTips 脚本，不去掉会被当成选项文本。
    /// </summary>
    private static IElement? RootOf(string html)
    {
        var doc = Parser.ParseDocument($"<div id=\"fling-root\">{html}</div>");
        var root = doc.QuerySelector("#fling-root");
        if (root is null) return null;

        foreach (var node in root.QuerySelectorAll("script,style,noscript,svg").ToArray())
            node.Remove();

        return root;
    }

    /// <summary>把正文解析成 DOM 后取纯文本，用于 "33 Options · Game Version: … · Last Updated: …"。</summary>
    private static (int Count, string Version, List<string> Platforms, string Updated) ParseMeta(string content)
    {
        var (count, version, platforms, updated) = (0, "", new List<string>(), "");

        var root = RootOf(content);
        if (root is null) return (count, version, platforms, updated);

        // 这一行本身就是一个段落，按行找同时含 Options 与 Game Version 的那行，避免和后面的选项混在一起
        foreach (var line in root.TextContent.Split('\n'))
        {
            if (!line.Contains("Options", StringComparison.OrdinalIgnoreCase) ||
                !line.Contains("Game Version:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var segment in line.Split('\u00b7'))
            {
                var text = Collapse(segment);
                if (text.Length == 0) continue;

                var optIndex = text.IndexOf("Options", StringComparison.OrdinalIgnoreCase);
                if (count == 0 && optIndex > 0)
                {
                    var digits = new StringBuilder();
                    foreach (var c in text[..optIndex])
                    {
                        if (char.IsDigit(c)) digits.Append(c);
                    }

                    if (digits.Length > 0) count = int.Parse(digits.ToString());
                }

                if (version.Length == 0 && After(text, "Game Version:") is { } rawVersion)
                {
                    var (v, p) = SplitVersion(rawVersion);
                    version = v;
                    platforms = p;
                }

                if (updated.Length == 0 && After(text, "Last Updated:") is { } rawDate)
                    updated = Short(FirstToken(rawDate));
            }

            if (count > 0) break;
        }

        return (count, version, platforms, updated);
    }

    private static string FirstToken(string text)
    {
        var cut = text.IndexOfAny(new[] { ' ', '\t', '\u00b7' });
        return (cut > 0 ? text[..cut] : text).Trim();
    }

    private static string? After(string text, string marker)
    {
        var i = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        var value = text[(i + marker.Length)..].Trim();
        return value.Length == 0 ? null : value;
    }

    // ==================== 选项分组 ====================

    /// <summary>把正文里的段落 / 标题块解析成分组的选项列表。</summary>
    public static List<TrainerGroup> ParseGroups(string html)
    {
        var groups = new List<TrainerGroup>();
        TrainerGroup? current = null;

        var root = RootOf(html);
        if (root is null) return groups;

        foreach (var block in root.QuerySelectorAll("h1,h2,h3,h4,h5,h6,p"))
        {
            // 下载表格里的文字不是选项
            if (block.Closest("table") is not null) continue;

            foreach (var raw in TextWithBreaks(block).Split('\n'))
            {
                var text = Collapse(raw);
                if (text.Length == 0) continue;

                var lower = text.ToLowerInvariant();
                if (lower is "options" or "download") continue;
                if (IsMetaLine(lower)) continue;

                var cut = text.LastIndexOf('\u2013');
                if (cut > 0 && cut + 2 < text.Length && text[cut + 1] == ' ')
                {
                    var key = text[..cut].Trim();
                    var name = text[(cut + 2)..].Trim();
                    if (key.Length == 0 || name.Length == 0) continue;

                    if (current is null)
                    {
                        current = new TrainerGroup { Title = "常规" };
                        groups.Add(current);
                    }

                    current.Items.Add(new TrainerOption { Key = key, Name = name });
                }
                else
                {
                    current = new TrainerGroup { Title = text };
                    groups.Add(current);
                }
            }
        }

        groups.RemoveAll(g => g.Items.Count == 0);
        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Title)) g.Title = "常规";
        }

        return groups;
    }

    /// <summary>取元素文本，把 &lt;br&gt; 当换行（一个段落里常有多条选项）。</summary>
    private static string TextWithBreaks(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        var document = clone.Owner ?? Parser.ParseDocument("");

        foreach (var br in clone.QuerySelectorAll("br").ToArray())
            br.Parent?.ReplaceChild(document.CreateTextNode("\n"), br);

        return clone.TextContent;
    }

    /// <summary>"33 Options · Game Version: … · Last Updated: …" 这一行不是选项。</summary>
    private static bool IsMetaLine(string lower)
    {
        if (lower.Contains("game version:", StringComparison.Ordinal) ||
            lower.Contains("last updated:", StringComparison.Ordinal))
        {
            return true;
        }

        var digits = 0;
        while (digits < lower.Length && char.IsDigit(lower[digits])) digits++;
        return digits > 0 && lower[digits..].TrimStart().StartsWith("option", StringComparison.Ordinal);
    }

    private static string HtmlText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        return Collapse(Parser.ParseDocument(html).Body?.TextContent ?? "");
    }

    // ==================== 字符串 ====================

    /// <summary>从 "Steam Xbox/Game Pass v1.0-v2.x+" 里拆出平台和版本号。</summary>
    private static (string Version, List<string> Platforms) SplitVersion(string raw)
    {
        var text = Collapse(raw);
        var found = new List<string>();

        var changed = true;
        while (changed && text.Length > 0)
        {
            changed = false;
            foreach (var p in Platforms)
            {
                if (!text.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(p);
                text = text[p.Length..].Trim();
                changed = true;
            }
        }

        return (text, found);
    }

    /// <summary>统一空白（含不换行空格），比正则便宜也更好读。</summary>
    private static string Collapse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length);
        var space = false;

        foreach (var raw in s)
        {
            var c = raw == '\u00a0' ? ' ' : raw;

            if (char.IsWhiteSpace(c))
            {
                if (!space && sb.Length > 0) sb.Append(' ');
                space = true;
                continue;
            }

            sb.Append(c);
            space = false;
        }

        return sb.ToString().TrimEnd();
    }

    private static string Short(string? iso) =>
        DateTime.TryParse(iso, out var d) ? d.ToString("yyyy.MM.dd") : (iso ?? "");

    private static string SlugOf(string url) =>
        url.Trim('/').Split('/').LastOrDefault() ?? "";
}
