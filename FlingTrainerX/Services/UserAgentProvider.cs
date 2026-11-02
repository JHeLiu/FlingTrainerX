using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FlingTrainerX.Services;

/// <summary>
/// 按本机真实的 Edge 浏览器信息拼装 User-Agent 与配套的浏览器请求头。
/// flingtrainer.com 会拦截非浏览器 UA，这里直接伪装成本地安装的 Edge，
/// 版本号从注册表 / msedge.exe 里现读，系统版本、位数也从当前机器动态获取。
/// </summary>
public static class UserAgentProvider
{
    private static readonly Lazy<BrowserInfo> Info = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string UserAgent => Info.Value.UserAgent;
    public static string FullVersion => Info.Value.FullVersion;
    public static string MajorVersion => Info.Value.MajorVersion;
    public static string Platform => Info.Value.Platform;
    public static string LanguageHeader => Info.Value.LanguageHeader;

    /// <summary>设置页里展示用的说明。</summary>
    public static string Describe() =>
        $"Edge {FullVersion} · {Platform} · 系统 {Environment.OSVersion.VersionString}";

    private sealed record BrowserInfo(
        string UserAgent,
        string FullVersion,
        string MajorVersion,
        string Platform,
        string LanguageHeader);

    private static BrowserInfo Detect()
    {
        var version = FindEdgeVersion();
        var major = version.Split('.')[0];
        var chrome = $"{major}.0.0.0";

        var arch = Environment.Is64BitOperatingSystem
            ? (Environment.Is64BitProcess ? "Win64; x64" : "WOW64")
            : string.Empty;

        var nt = Environment.OSVersion.Version;
        var ntVersion = nt.Major >= 10
            ? "Windows NT 10.0"
            : nt.Major == 6 ? $"Windows NT 6.{Math.Max(1, nt.Minor)}" : $"Windows NT {nt.Major}.{nt.Minor}";

        var platform = arch.Length > 0 ? $"{ntVersion}; {arch}" : ntVersion;

        var ua = $"Mozilla/5.0 ({platform}) AppleWebKit/537.36 (KHTML, like Gecko) " +
                 $"Chrome/{chrome} Safari/537.36 Edg/{version}";

        return new BrowserInfo(ua, version, major, platform, BuildLanguage());
    }

    /// <summary>从注册表读 Edge 版本，读不到就从 msedge.exe 的文件版本取，最后兜底一个较新的版本。</summary>
    private static string FindEdgeVersion()
    {
        const string edgeClient = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var v = ReadVersion(RegistryKey.OpenBaseKey(hive, view), edgeClient);
                if (v is not null) return v;
                var v2 = ReadVersion(RegistryKey.OpenBaseKey(hive, view),
                    @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}");
                if (v2 is not null) return v2;
            }
        }

        foreach (var exe in EdgeExeCandidates())
        {
            try
            {
                if (!File.Exists(exe)) continue;
                var fv = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                if (Normalize(fv) is { } v) return v;
            }
            catch
            {
                // 忽略单个路径读取失败
            }
        }

        return "141.0.0.0";
    }

    private static string? ReadVersion(RegistryKey baseKey, string path)
    {
        try
        {
            using var key = baseKey.OpenSubKey(path);
            return Normalize(key?.GetValue("pv") as string);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>统一成 4 段数字，避免注册表里出现 "141.0.3537" 这种不完整版本。</summary>
    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major) || major < 80) return null;

        var sb = new StringBuilder(major.ToString());
        for (var i = 1; i < 4; i++)
        {
            var piece = i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
            sb.Append('.').Append(piece);
        }

        return sb.ToString();
    }

    private static IEnumerable<string> EdgeExeCandidates()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
        }
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
