using System.Net.Http;
using System.Text.Json;

namespace FlingTrainerX.Services;

/// <summary>
/// 拿官网的英文游戏名去 Steam 商店换简体中文名。
/// <para>
/// 用的是商店搜索接口（<c>api/storesearch</c>）：按关键词搜、指定 <c>l=schinese</c>，
/// 返回的第一个 app 的名字就是国区中文名。不需要 key，也不需要登录。
/// 只有一条规则：返回的名字里得真有汉字，否则说明这款游戏在 Steam 上没出中文，
/// 或者搜到的是别的英文结果——这两种都当「没换到」，留空。
/// </para>
/// </summary>
public static class SteamWeb
{
    private const string StoreHost = "https://store.steampowered.com";
    private const string SearchApi = StoreHost + "/api/storesearch/";

    private static HttpClient? _http;

    private static HttpClient Http => _http ??= CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgentProvider.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.6");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", StoreHost + "/");
        return client;
    }

    /// <summary>
    /// 按英文名换中文名。返回值三种情况：
    /// 有中文名 → 中文名；接口通但没搜到中文 → 空串；
    /// 请求本身失败（网络不通 / Steam 被墙 / 超时）→ <c>null</c>，同步时据此判断「是不是该提前收工」。
    /// </summary>
    public static async Task<string?> FetchChineseNameAsync(string englishName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(englishName)) return "";

        try
        {
            var url = $"{SearchApi}?term={Uri.EscapeDataString(englishName.Trim())}&l=schinese&cc=CN";
            using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                return "";
            }

            var name = items[0].TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            return HasCjk(name) ? name.Trim() : "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>名字里有没有汉字——纯英文结果说明这款游戏没出中文。</summary>
    private static bool HasCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        }

        return false;
    }
}
