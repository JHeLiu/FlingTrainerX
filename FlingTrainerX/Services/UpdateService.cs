using System;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>一次更新检查的三种结局。</summary>
public enum UpdateCheckState
{
    /// <summary>Gitee 上有比本机更新的版本。</summary>
    UpdateAvailable,

    /// <summary>已经是最新版本。</summary>
    UpToDate,

    /// <summary>没查成（断网 / 仓库不可见 / 接口变了），<see cref="UpdateCheckResult.Message"/> 里是原因。</summary>
    Failed
}

/// <summary>检查更新的结果：状态 + 新版本信息 + 一句给人看的说明。</summary>
public sealed class UpdateCheckResult
{
    /// <summary>结局。</summary>
    public UpdateCheckState State { get; private init; }

    /// <summary>本次查到的发行版（<see cref="UpdateCheckState.UpToDate"/> 时也有，用来展示「更新内容」）。</summary>
    public UpdateRelease? Release { get; private init; }

    /// <summary>给人看的说明，失败时是失败原因。</summary>
    public string Message { get; private init; } = "";

    /// <summary>构造「发现新版本」结果。</summary>
    public static UpdateCheckResult Available(UpdateRelease release) => new()
    {
        State = UpdateCheckState.UpdateAvailable,
        Release = release,
        Message = $"发现新版本 v{release.Version}"
    };

    /// <summary>构造「已是最新」结果。</summary>
    public static UpdateCheckResult Latest(UpdateRelease? release, string? message = null) => new()
    {
        State = UpdateCheckState.UpToDate,
        Release = release,
        Message = message ?? $"已经是最新版本 v{UpdateService.CurrentVersionText}"
    };

    /// <summary>构造「检查失败」结果。</summary>
    public static UpdateCheckResult Fail(string message) => new()
    {
        State = UpdateCheckState.Failed,
        Message = message
    };
}

/// <summary>
/// 在线更新：从 Gitee 的「发行版（releases）」里查最新版本，下载更新包，再交给安装程序。
///
/// 两步走：
/// 1. 检查走 Gitee 的 JSON 接口，能拿到版本号、发行说明、附件直链，
///    界面才好把「更新了什么」一起显示出来（Gitee 不提供 appcast XML）；
/// 2. 下载自己实现（<see cref="DownloadAsync"/>）：要的是程序内那条真实进度条，
///    而不是第三方库的 WinForms 下载窗。下完由界面拉起安装程序，安装界面接管后续。
/// </summary>
public static class UpdateService
{
    /// <summary>更新弹窗和下载窗口上显示的软件名。</summary>
    public const string AppTitle = "风灵月影宗藏宝阁";

    /// <summary>默认仓库（<c>Assets\update.json</c> 不存在时用它）。</summary>
    private const string DefaultOwner = "misslius";
    private const string DefaultRepo = "FlingTrainerX";

    /// <summary>更新包临时目录：下载的 zip / 安装包都落在这里。</summary>
    public static string DownloadDir =>
        Path.Combine(Path.GetTempPath(), "FlingTrainerX", "update");

    private static readonly Lazy<RepoConfig> Config = new(LoadConfig, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Version> InstalledVersion = new(ReadInstalledVersion, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>本机版本（取自程序集版本，打包脚本用 -p:Version 写进去的）。</summary>
    public static Version CurrentVersion => InstalledVersion.Value;

    /// <summary>本机版本的显示文本，例如 <c>1.0.2</c>。</summary>
    public static string CurrentVersionText => CurrentVersion.ToString();

    /// <summary>发行版列表页地址（查不到更新时给用户一个「自己去看看」的入口）。</summary>
    public static string ReleasesPageUrl =>
        $"https://gitee.com/{Config.Value.Owner}/{Config.Value.Repo}/releases";

    /// <summary>Gitee 最新发行版接口地址（带 token 时自动拼上）。</summary>
    public static string ApiUrl
    {
        get
        {
            var cfg = Config.Value;
            var url = $"https://gitee.com/api/v5/repos/{cfg.Owner}/{cfg.Repo}/releases/latest";
            return string.IsNullOrWhiteSpace(cfg.Token) ? url : $"{url}?access_token={Uri.EscapeDataString(cfg.Token)}";
        }
    }

    /// <summary>
    /// 查一次最新版本。任何异常都收敛成 <see cref="UpdateCheckState.Failed"/>，
    /// 调用方不用 try/catch，界面也不会因为断网报错。
    /// </summary>
    /// <param name="ct">取消标记（窗口关掉时用它掐断请求）。</param>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = UserAgentProvider.CreateHttpClient(timeout: TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Fail(DescribeHttpFailure(response.StatusCode));

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = ParseRelease(json);
            if (release is null) return UpdateCheckResult.Fail("发行版信息解析失败，可能是 Gitee 接口改了");

            return IsNewer(release.Version, CurrentVersion)
                ? UpdateCheckResult.Available(release)
                : UpdateCheckResult.Latest(release);
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Fail("检查更新超时，请稍后再试");
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail($"检查更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把更新包下载到临时目录，边下边上报进度。
    /// <para>
    /// 不用 AutoUpdater.NET 的原因：它的下载窗口是 WinForms 的，跟这套深色界面不搭，
    /// 而且下载完会自己去覆盖安装；这里要的是「程序内进度条 + 下完交给安装程序」。
    /// </para>
    /// </summary>
    /// <param name="release">要下的发行版。</param>
    /// <param name="progress">进度回调（由界面用 <c>Progress&lt;T&gt;</c> 包一层，自动回到 UI 线程）。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>下载好的文件完整路径。</returns>
    public static async Task<string> DownloadAsync(
        UpdateRelease release,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            throw new InvalidOperationException("这个版本没有可下载的更新包");

        Directory.CreateDirectory(DownloadDir);

        var fileName = string.IsNullOrWhiteSpace(release.AssetName)
            ? $"setup-{release.Version}.exe"
            : release.AssetName;

        // 文件名里可能有路径分隔符，只保留最后一段，别让它跑出临时目录
        fileName = Path.GetFileName(fileName);
        var target = Path.Combine(DownloadDir, fileName);

        // 上次没下完的半截文件会让安装包损坏，直接覆盖
        if (File.Exists(target)) File.Delete(target);

        using var client = UserAgentProvider.CreateHttpClient(timeout: TimeSpan.FromMinutes(30));
        using var response = await client
            .GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载失败：HTTP {(int)response.StatusCode}");

        // 服务器不给 Content-Length 时退回发行版里写的大小，再没有就是 0（界面走不确定态）
        var total = response.Content.Headers.ContentLength ?? release.Size;

        using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var destination = File.Create(target);

        var buffer = new byte[81920];
        long received = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long nextReportAt = 0;

        progress.Report(new DownloadProgress(0, total, 0));

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;

            // 每 100ms 报一次就够顺滑了，报太密反而把 UI 线程刷爆
            if (stopwatch.ElapsedMilliseconds >= nextReportAt)
            {
                nextReportAt = stopwatch.ElapsedMilliseconds + 100;
                var speed = stopwatch.Elapsed.TotalSeconds > 0 ? received / stopwatch.Elapsed.TotalSeconds : 0;
                progress.Report(new DownloadProgress(received, total, speed));
            }
        }

        progress.Report(new DownloadProgress(received, total, 0));

        if (total > 0 && received < total)
            throw new IOException($"下载不完整（{received} / {total} 字节），请重试");

        return target;
    }

    // ==================== 内部实现 ====================

    /// <summary>仓库配置：优先读 exe 旁边 Assets\update.json，读不到就用内置的默认仓库。</summary>
    private sealed class RepoConfig
    {
        public string Owner { get; init; } = DefaultOwner;
        public string Repo { get; init; } = DefaultRepo;
        public string Token { get; init; } = "";
    }

    private static RepoConfig LoadConfig()
    {
        var path = Path.Combine(AppPaths.BaseDir, "Assets", "update.json");

        try
        {
            if (!File.Exists(path)) return new RepoConfig();

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var owner = ReadString(root, "owner");
            var repo = ReadString(root, "repo");
            var token = ReadString(root, "token");

            return new RepoConfig
            {
                Owner = owner.Length > 0 ? owner : DefaultOwner,
                Repo = repo.Length > 0 ? repo : DefaultRepo,
                Token = token
            };
        }
        catch
        {
            // 配置写错了不能让更新功能把程序带崩，退回默认仓库
            return new RepoConfig();
        }
    }

    private static Version ReadInstalledVersion()
    {
        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null
                ? new Version(0, 0, 0)
                : new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
        }
        catch
        {
            return new Version(0, 0, 0);
        }
    }

    /// <summary>把 Gitee 返回的那段 JSON 解析成 <see cref="UpdateRelease"/>，解析不了返回 null。</summary>
    private static UpdateRelease? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tag = ReadString(root, "tag_name");
            var version = NormalizeVersion(tag);
            if (version is null) return null;

            var (assetName, assetUrl, assetSize) = PickAsset(root);

            var cfg = Config.Value;
            var pageUrl = tag.Length > 0
                ? $"https://gitee.com/{cfg.Owner}/{cfg.Repo}/releases/tag/{Uri.EscapeDataString(tag)}"
                : ReleasesPageUrl;

            return new UpdateRelease
            {
                Version = version.ToString(),
                Tag = tag,
                Title = ReadString(root, "name"),
                Notes = PlainNotes(ReadString(root, "body")),
                PageUrl = pageUrl,
                DownloadUrl = assetUrl,
                AssetName = assetName,
                Size = assetSize,
                PublishedAt = ReadTime(root, "created_at")
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从附件里挑一个能装的：优先安装包 exe（下完直接运行，安装界面接管），
    /// 其次 zip（早期版本传过打包好的目录），都没有就退回第一个附件。
    /// </summary>
    private static (string name, string url, long size) PickAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return ("", "", 0);

        (string name, string url, long size) first = ("", "", 0);
        (string name, string url, long size)? zip = null;
        (string name, string url, long size)? exe = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            var url = ReadString(asset, "browser_download_url");
            if (url.Length == 0) continue;

            var item = (name, url, size: ReadLong(asset, "size"));
            if (first.url.Length == 0) first = item;

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exe is null) exe = item;
            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && zip is null) zip = item;
        }

        return exe ?? zip ?? first;
    }

    /// <summary>把 tag 洗成纯版本号（去掉 v 前缀和 -beta 这类后缀），洗不出来返回 null。</summary>
    private static Version? NormalizeVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V', ' ');
        if (text.Length == 0) return null;

        text = text.Split('-', '+')[0];
        if (!Version.TryParse(text, out var parsed)) return null;

        return new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
    }

    /// <summary>远端版本是不是比本机新（缺一段按 0 算）。</summary>
    private static bool IsNewer(string remote, Version local) =>
        Version.TryParse(remote, out var parsed) && parsed > local;

    /// <summary>HTTP 状态码翻成人话。</summary>
    private static string DescribeHttpFailure(HttpStatusCode code) => code switch
    {
        // 私有仓库对匿名请求也回 404（不是 403），所以这里要把两种情况都说清楚
        HttpStatusCode.NotFound =>
            "没找到 Gitee 仓库或发行版（404）：确认地址正确、已发布版本；仓库若是私有的，匿名读不到，需在 update.json 填 token",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"Gitee 拒绝访问（{(int)code}），仓库可能是私有的，可在 Assets\\update.json 里填 access_token",
        _ => $"Gitee 返回 {(int)code}，暂时查不到更新"
    };

    /// <summary>
    /// 发行说明是 Markdown，弹窗里按纯文本铺（只读 TextBox 能选中复制），
    /// 这里把标题井号、列表符号、星号、链接、HTML 这些标记洗掉，保留分段和换行。
    /// </summary>
    public static string PlainNotes(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</(p|div|li|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "$1");
        text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s{0,3}>\s?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "· ", RegexOptions.Multiline);
        text = Regex.Replace(text, "```[a-zA-Z]*", "");
        text = Regex.Replace(text, @"[*_~`]", "");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static DateTimeOffset? ReadTime(JsonElement root, string name)
    {
        var text = ReadString(root, name);
        if (text.Length == 0) return null;

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var time)
            ? time
            : null;
    }
}
