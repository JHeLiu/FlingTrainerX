using System.Net;
using System.Net.Http;

namespace FlingTrainerX.Services;

/// <summary>
/// 拼装浏览器 User-Agent 与配套请求头。
/// flingtrainer.com 会拦截非浏览器 UA，这里伪装成 Edge。
///
/// 版本号是**写死**的常量：不读注册表、不看 msedge.exe，
/// 所以换到没装 Edge 的机器（LTSC / Server / 精简系统）上也照样是同一份合法 UA，
/// 不会因为读不到版本而退化成奇怪的字符串。只有系统版本 / 位数取自当前机器。
/// 升级版本改 <see cref="EdgeVersion"/> 一行即可。
/// </summary>
public static class UserAgentProvider
{
    /// <summary>伪装用的 Edge 版本（= 对应的 Chromium 主版本），要改就改这里。</summary>
    private const string EdgeVersion = "141.0.0.0";

    private static readonly Lazy<BrowserInfo> Info = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string UserAgent => Info.Value.UserAgent;
    public static string FullVersion => EdgeVersion;
    public static string MajorVersion => Info.Value.MajorVersion;
    public static string Platform => Info.Value.Platform;
    public static string LanguageHeader => Info.Value.LanguageHeader;

    /// <summary>设置页里展示用的说明。</summary>
    public static string Describe() =>
        $"Edge {EdgeVersion}（内置版本） · {Platform} · 系统 {Environment.OSVersion.VersionString}";

    private sealed record BrowserInfo(
        string UserAgent,
        string MajorVersion,
        string Platform,
        string LanguageHeader);

    private static BrowserInfo Build()
    {
        var major = EdgeVersion.Split('.')[0];

        // 只有这两样取自本机：位数决定 Win64; x64 / WOW64，NT 版本取自当前系统
        var arch = Environment.Is64BitOperatingSystem
            ? (Environment.Is64BitProcess ? "Win64; x64" : "WOW64")
            : string.Empty;

        var nt = Environment.OSVersion.Version;
        var ntVersion = nt.Major >= 10
            ? "Windows NT 10.0"
            : nt.Major == 6 ? $"Windows NT 6.{Math.Max(1, nt.Minor)}" : $"Windows NT {nt.Major}.{nt.Minor}";

        var platform = arch.Length > 0 ? $"{ntVersion}; {arch}" : ntVersion;

        // UA 里 Chrome/ 那段在 Edge 上就是 major.0.0.0，跟 Edg/ 的完整版本号是两个东西，别写成一样
        var ua = $"Mozilla/5.0 ({platform}) AppleWebKit/537.36 (KHTML, like Gecko) " +
                 $"Chrome/{major}.0.0.0 Safari/537.36 Edg/{EdgeVersion}";

        return new BrowserInfo(ua, major, platform, BuildLanguage());
    }

    /// <summary>Accept-Language 按当前系统语言生成，中文机器就是 zh-CN,zh;q=0.9,en;q=0.8。</summary>
    private static string BuildLanguage()
    {
        var name = System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrWhiteSpace(name)) name = "zh-CN";

        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return $"{name},zh;q=0.9,en;q=0.8";
        if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return $"{name},en;q=0.9";

        var lang = name.Split('-')[0];
        return $"{name},{lang};q=0.9,en;q=0.8";
    }

    /// <summary>附上 Edge 会带上的那套请求头，尽量和真实浏览器一致。</summary>
    public static void ApplyHeaders(HttpRequestMessage request, string? referer = null)
    {
        var info = Info.Value;
        var headers = request.Headers;

        headers.TryAddWithoutValidation("User-Agent", info.UserAgent);
        headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp," +
            "image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        headers.TryAddWithoutValidation("Accept-Language", info.LanguageHeader);
        headers.TryAddWithoutValidation("sec-ch-ua",
            $"\"Microsoft Edge\";v=\"{info.MajorVersion}\", \"Chromium\";v=\"{info.MajorVersion}\", " +
            "\"Not_A Brand\";v=\"24\"");
        headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        headers.TryAddWithoutValidation("Cache-Control", "max-age=0");

        if (!string.IsNullOrWhiteSpace(referer))
            headers.TryAddWithoutValidation("Referer", referer);
    }

    /// <summary>统一的 HttpClient：自动解压（br/gzip/deflate），带浏览器 UA 与语言。</summary>
    public static HttpClient CreateHttpClient(string? referer = null, TimeSpan? timeout = null)
    {
        var info = Info.Value;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(25)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", info.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", info.LanguageHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua",
            $"\"Microsoft Edge\";v=\"{info.MajorVersion}\", \"Chromium\";v=\"{info.MajorVersion}\", " +
            "\"Not_A Brand\";v=\"24\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp," +
            "image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");

        if (!string.IsNullOrWhiteSpace(referer))
            client.DefaultRequestHeaders.Referrer = new Uri(referer);

        return client;
    }
}
