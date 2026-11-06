using System.Net.Http;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>
/// 「抓全量游戏名」过程中的一条进度快照，给更新弹框第二段用。
/// 名录是一个请求一次拿全的，中途没有条数可算，所以只报「读到多少款、当前是哪款」。
/// </summary>
/// <param name="Count">目前已经解析出的条数。</param>
/// <param name="Current">刚读到的游戏名，给面板滚动展示用。</param>
/// <param name="Detail">阶段说明，例如「第 3 / 8 页」。</param>
public sealed record NameFetchProgress(int Count, string Current, string Detail);

/// <summary>
/// 从 flingtrainer.com 抓取文章并解析。所有 HTML 都用 AngleSharp 走 DOM，
/// 只有「拼 URL / 拆版本号」这类纯字符串处理，不再用正则去啃页面结构。
/// </summary>
public static class TrainerWeb
{
    public const string Site = "https://flingtrainer.com/";

    /// <summary>官网每页 15 条（首页一行 5 张 × 3 行），翻页地址是 /page/2/，API 对应 page=2。</summary>
    public const int PageSize = 15;

    private const string ApiBase = "https://flingtrainer.com/wp-json/wp/v2/posts";

    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// 列表接口字段。_embed 把特色图（官网头图 header.jpg）一起带回来，
    /// 这样卡片封面不用再等正文首图，也不用去猜图片地址。
    /// </summary>
    private static string EndpointOf(int page) =>
        $"{ApiBase}?per_page={PageSize}&page={page}&orderby=modified&order=desc" +
        "&_embed=wp:featuredmedia" +
        "&_fields=id,date,modified,link,title,content,_links,_embedded";

    /// <summary>
    /// 搜索关键词归一化。官网标题里是排版用的弯引号（Assassin’s 的 U+2019），
    /// 拿它原样去搜是搜不到的（WP 把它当成词的一部分），换成直引号反而能命中；
    /// 破折号同理。别的字符保持原样。
    /// </summary>
    public static string NormalizeKeyword(string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return "";

        var sb = new StringBuilder(keyword.Length);
        foreach (var c in keyword)
        {
            sb.Append(c switch
            {
                '\u2018' or '\u2019' or '\u02BC' => '\'',   // ‘ ’ ʼ
                '\u201C' or '\u201D' => '"',                // “ ”
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' => '-',  // 各种连字符/破折号
                '\u00A0' => ' ',                            // 不换行空格
                _ => c
            });
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// 官网自带搜索的接口形式。官网搜索页（<c>/?s=关键词</c>）用的就是 WP 的 <c>search</c> 参数，
    /// 结果顺序也和搜索页一致（按最后修改时间倒序），所以这里跟列表走同一套排序。
    /// 关键词先做归一化再 URL 编码：中文、空格、冒号、撇号都能直接用。
    /// </summary>
    private static string SearchEndpointOf(string keyword, int page) =>
        $"{ApiBase}?per_page={PageSize}&page={page}&orderby=modified&order=desc" +
        $"&search={Uri.EscapeDataString(NormalizeKeyword(keyword))}" +
        "&_embed=wp:featuredmedia" +
        "&_fields=id,date,modified,link,title,content,_links,_embedded";

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
        var (items, _, _) = await FetchPageAsync(1, ct).ConfigureAwait(false);
        return items;
    }

    /// <summary>
    /// 按文章 ID 单独取头图。收藏夹里的老快照没存 Cover，
    /// 又不在当前页时用它补一次，保证卡片封面和列表一致。失败返回空串。
    /// </summary>
    public static async Task<string> FetchCoverAsync(int id, CancellationToken ct = default)
    {
        try
        {
            using var request = Request(
                $"{ApiBase}/{id}?_embed=wp:featuredmedia&_fields=id,_links,_embedded", true);
            using var res = await Http.SendAsync(request, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return FeaturedCover(doc.RootElement);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 抓取官网第 <paramref name="page"/> 页（1 起），返回文章、总页数与总条数
    /// （分页栏按官网真实总数显示，不再依赖本地加载了多少）。
    /// </summary>
    public static Task<(List<TrainerItem> Items, int TotalPages, int TotalCount)> FetchPageAsync(
        int page, CancellationToken ct = default) => FetchListAsync(EndpointOf(page), ct);

    /// <summary>
    /// 官网搜索：契约同 <see cref="FetchPageAsync"/>，只是多了 <c>search</c> 条件。
    /// 关键词按 URL 编码拼进查询串，中文/空格/符号都能直接用。
    /// </summary>
    public static Task<(List<TrainerItem> Items, int TotalPages, int TotalCount)> SearchAsync(
        string keyword, int page, CancellationToken ct = default)
        => FetchListAsync(SearchEndpointOf(keyword, page), ct);

    /// <summary>官网「全部游戏名」页：官网把它叫 All Trainers (A-Z)，一页列完 700 多款。</summary>
    public const string AllTrainersUrl = "https://flingtrainer.com/all-trainers/";

    /// <summary>
    /// 抓「全部游戏名」——直接解析官网 <c>All Trainers (A-Z)</c> 页面（<see cref="AllTrainersUrl"/>）。
    /// <para>
    /// 这一页把 700 多款游戏按字母排在同一个页面里，<b>一个请求</b>就够；按 WP 接口翻页要 8 次（每页 100 条），
    /// 慢一个量级。每条的键取链接里的 slug（官网改标题也不会变），只用于本地提示，不需要文章 ID。
    /// </para>
    /// <para>
    /// 页面的 <c>#</c> 分组是空的，而且它是按某次快照生成的 —— 数字开头的游戏、以及页面生成之后
    /// 才上架的游戏都不会出现在里面（实测「007 First Light」「Disney Dreamlight Valley」都缺）。
    /// 所以再补一个很小的接口请求，把最近发布的 100 篇按标题合进来，缺的几款就齐了。
    /// </para>
    /// <para>
    /// 页面结构解析不出东西时（改版 / 被拦），退回 WP 接口那套分页抓法，保证「更新游戏名」不会因为
    /// 页面改版直接失效。
    /// </para>
    /// </summary>
    public static async Task<List<GameNameRow>> FetchAllNamesAsync(
        CancellationToken ct = default, IProgress<NameFetchProgress>? progress = null)
    {
        List<GameNameRow> rows;
        try
        {
            rows = await FetchAllNamesFromPageAsync(ct, progress).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 页面这条路走不通就换接口，不把异常抛给调用方
            rows = new List<GameNameRow>();
        }

        if (rows.Count == 0) return await FetchAllNamesFromApiAsync(ct, progress).ConfigureAwait(false);

        progress?.Report(new NameFetchProgress(rows.Count, "", "官网名录读取完成，正在补最新上架的游戏"));

        // 补上 A-Z 页面生成之后新上架的游戏（页面是快照，不会自己长出新条目）
        try
        {
            var seenNames = rows.Select(r => r.NameEn.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in await FetchRecentNamesAsync(ct).ConfigureAwait(false))
            {
                if (seenNames.Add(row.NameEn.Trim())) rows.Add(row);
            }

            progress?.Report(new NameFetchProgress(rows.Count, "", "名录读取完成"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 补漏失败不影响主名单
        }

        return rows;
    }

    /// <summary>
    /// 解析 A-Z 页面：<c>ul.az-columns &gt; li &gt; a</c> 一条一款游戏，链接文字就是官网标题
    /// （带 " Trainer" 后缀，和列表接口给的标题一致）。
    /// </summary>
    private static async Task<List<GameNameRow>> FetchAllNamesFromPageAsync(
        CancellationToken ct, IProgress<NameFetchProgress>? progress)
    {
        using var request = Request(AllTrainersUrl, false);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");
        using var res = await Http.SendAsync(request, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        var html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = Parser.ParseDocument(html);

        var all = new List<GameNameRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTime.Now;

        progress?.Report(new NameFetchProgress(0, "", "正在解析官网名录"));

        foreach (var link in doc.QuerySelectorAll("ul.az-columns a[href]"))
        {
            var key = GameNameRow.KeyOf(link.GetAttribute("href") ?? "");
            if (key.Length == 0 || !seen.Add(key)) continue;

            var title = Collapse(link.TextContent);
            if (title.Length == 0) continue;

            all.Add(new GameNameRow { TrainerId = key, NameEn = title, UpdatedAt = now });

            // 每 30 条报一次：面板上游戏名一路滚过去，看得出在动。
            // 解析 700 多条只要几十毫秒，不在这里稍微让一下，界面只会看到最后一帧、名字全被跳过，
            // 所以每条进度后面留一点点时间给界面把这一帧画出来（只在有人监听时才让，约拖慢 0.4 秒）。
            if (all.Count % 30 == 0)
            {
                progress?.Report(new NameFetchProgress(all.Count, title, "正在解析官网名录"));
                if (progress is not null) await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }

        progress?.Report(new NameFetchProgress(all.Count, all[^1].NameEn, "官网名录解析完成"));

        return all;
    }

    /// <summary>
    /// 补 A-Z 页面漏掉的条目。官网这一页是按某次快照生成的，<b>之后新上架的游戏不会自动出现</b>
    /// （实测「Disney Dreamlight Valley」「007 First Light」都不在页面上），而新游戏恰恰是用户最想搜到的。
    /// <para>
    /// 这里按发布时间倒序取最近 100 篇，和页面名单按标题合并；两边都有的以页面（slug）为准，
    /// 只补页面没有的那几条，所以正常情况下只会多出几款。
    /// </para>
    /// </summary>
    private static async Task<List<GameNameRow>> FetchRecentNamesAsync(CancellationToken ct)
    {
        using var request = Request(
            $"{ApiBase}?per_page=100&page=1&orderby=date&order=desc&_fields=id,title", true);
        using var res = await Http.SendAsync(request, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<GameNameRow>();

        var rows = new List<GameNameRow>();
        var now = DateTime.Now;

        foreach (var post in doc.RootElement.EnumerateArray())
        {
            if (!post.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id) || id <= 0)
                continue;

            var title = HtmlText(post.GetProperty("title").GetProperty("rendered").GetString() ?? "");
            if (title.Length == 0) continue;

            rows.Add(new GameNameRow { TrainerId = id.ToString(), NameEn = title, UpdatedAt = now });
        }

        return rows;
    }

    /// <summary>
    /// 退路：WP 接口按标题 A-Z 分页翻完（767 款 ≈ 8 个请求，每页只取 id 和标题）。
    /// 能拿到真正的文章 ID，主键就用 ID。
    /// </summary>
    private static async Task<List<GameNameRow>> FetchAllNamesFromApiAsync(
        CancellationToken ct, IProgress<NameFetchProgress>? progress)
    {
        var all = new List<GameNameRow>();
        var page = 1;
        int totalPages;

        do
        {
            using var request = Request(
                $"{ApiBase}?per_page=100&page={page}&orderby=title&order=asc&_fields=id,title", true);
            using var res = await Http.SendAsync(request, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            totalPages = 1;
            if (res.Headers.TryGetValues("X-WP-TotalPages", out var values) &&
                int.TryParse(values.FirstOrDefault(), out var t))
            {
                totalPages = t;
            }

            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) break;

            var now = DateTime.Now;
            foreach (var post in doc.RootElement.EnumerateArray())
            {
                if (!post.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id) || id <= 0)
                    continue;

                var title = HtmlText(post.GetProperty("title").GetProperty("rendered").GetString() ?? "");
                if (title.Length == 0) continue;

                all.Add(new GameNameRow { TrainerId = id.ToString(), NameEn = title, UpdatedAt = now });
            }

            progress?.Report(new NameFetchProgress(
                all.Count,
                all.Count > 0 ? all[^1].NameEn : "",
                $"第 {page} / {totalPages} 页"));
        } while (page++ < totalPages);

        return all;
    }

    private static async Task<(List<TrainerItem> Items, int TotalPages, int TotalCount)> FetchListAsync(
        string url, CancellationToken ct)
    {
        using var request = Request(url, true);
        using var res = await Http.SendAsync(request, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        var total = 1;
        if (res.Headers.TryGetValues("X-WP-TotalPages", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var t))
        {
            total = t;
        }

        // X-WP-Total：官网一共有多少篇文章（多少款游戏），副标题与分页栏都用它
        var count = 0;
        if (res.Headers.TryGetValues("X-WP-Total", out var countValues) &&
            int.TryParse(countValues.FirstOrDefault(), out var c))
        {
            count = c;
        }

        var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (Parse(json), total, count);
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

            item.ModifiedIso = post.GetProperty("modified").GetString() ?? "";

            var meta = ParseMeta(content);
            item.OptionCount = meta.Count;
            item.Version = meta.Version;
            item.Platforms = meta.Platforms;
            item.Updated = meta.Updated.Length > 0 ? meta.Updated : item.Modified;

            item.Images = ParseImages(content);
            item.ImageUrl = item.Images.FirstOrDefault() ?? "";

            // 卡片封面用官网头图（header.jpg，460×215），正文首图是修改器界面截图，
            // 只适合详情页的介绍图，所以两者分开存。
            item.Cover = FeaturedCover(post);

            item.Groups = ParseGroups(content);
            list.Add(item);
        }

        return list;
    }

    /// <summary>卡片封面优先取的尺寸档（依次降级，都没有才用原图）。</summary>
    private static readonly string[] CoverSizes = { "stylizer-medium", "alx-medium", "stylizer-large" };

    /// <summary>
    /// 取接口 _embedded.wp:featuredmedia 里的官网头图。官网自己引用的是 200×200 方形裁切，
    /// 但头图本身是宽幅艺术图，这里按卡片需要的尺寸挑一档（520×292，约卡片宽的 2.7 倍），
    /// 挑不到再退回原图 source_url。
    /// </summary>
    private static string FeaturedCover(JsonElement post)
    {
        if (!post.TryGetProperty("_embedded", out var embedded) ||
            !embedded.TryGetProperty("wp:featuredmedia", out var media) ||
            media.ValueKind != JsonValueKind.Array ||
            media.GetArrayLength() == 0)
        {
            return "";
        }

        var first = media[0];

        if (first.TryGetProperty("media_details", out var details) &&
            details.TryGetProperty("sizes", out var sizes) &&
            sizes.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in CoverSizes)
            {
                if (sizes.TryGetProperty(key, out var size) &&
                    size.TryGetProperty("source_url", out var sizedUrl) &&
                    sizedUrl.GetString() is { Length: > 0 } url)
                {
                    return url;
                }
            }
        }

        return first.TryGetProperty("source_url", out var src) ? src.GetString() ?? "" : "";
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
        var describing = false;

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
                    // 说明段落里被 en dash 误切开的那半句，不是真选项
                    if (describing) continue;

                    var key = text[..cut].Trim();
                    var name = text[(cut + 2)..].Trim();

                    // 抓到的空条目（只有空白或标点的占位行）不入库也不显示
                    if (IsEmptyOption(key, name)) continue;

                    if (current is null)
                    {
                        current = new TrainerGroup { Title = "常规" };
                        groups.Add(current);
                    }

                    current.Items.Add(new TrainerOption { Key = key, Name = name });
                }
                else
                {
                    // 整句的说明（例如「本作使用 EasyAntiCheat，请这样启动…」）不是分组标题，
                    // 直接丢掉，跟着它的那半句也不当选项
                    if (IsDescriptionLine(text))
                    {
                        current = null;
                        describing = true;
                        continue;
                    }

                    describing = false;
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

    /// <summary>官网正文里的说明段落：一个长句子，而不是「Edit Player Stats」这种短分组名。</summary>
    private static bool IsDescriptionLine(string text) => text.Length > 60 && text.Contains(' ');

    /// <summary>
    /// 空条目判定：快捷键或名字是空的，或者整条只有标点/空白（官网偶尔留这种占位行），
    /// 这种数据抓下来也没意义，直接丢掉，不进缓存也不进界面。
    /// </summary>
    private static bool IsEmptyOption(string key, string name)
    {
        if (key.Length == 0 || name.Length == 0) return true;

        return !key.Any(char.IsLetterOrDigit) || !name.Any(char.IsLetterOrDigit);
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
