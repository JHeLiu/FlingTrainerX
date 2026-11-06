using System;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoUpdaterDotNET;
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
/// 在线更新：从 Gitee 的「发行版（releases）」里查最新版本，确认更新后交给
/// AutoUpdater.NET（<c>Autoupdater.NET.Official</c>）下载并覆盖安装。
///
/// 分两步走是有原因的：
/// 1. 检查这一步自己走 Gitee 的 JSON 接口，能拿到版本号、发行说明、附件直链，
///    界面才好把「更新了什么」一起显示出来（库自带的 appcast XML 是另一套格式，Gitee 不提供）；
/// 2. 下载 + 解压 + 覆盖 + 重启这段折腾事交给 AutoUpdater.NET：它内置 ZipExtractor，
///    会等本进程退出后再替换文件，替换完自动把新版本拉起来，比自己写批处理可靠得多。
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
    /// 下载并安装。zip 走「等程序退出 → 解压覆盖 → 自动重启」，exe 安装包走「静默安装」。
    /// 会弹 AutoUpdater.NET 的下载窗口（中文界面），本方法返回时文件已经落地。
    /// </summary>
    /// <param name="release">要装的版本。</param>
    /// <param name="owner">下载窗口的父窗口，让它居中并锁住主界面。</param>
    /// <returns>true 表示已经启动安装流程，调用方应立刻退出程序让出文件。</returns>
    public static bool Apply(UpdateRelease release, System.Windows.Window owner)
    {
        if (string.IsNullOrWhiteSpace(release.DownloadUrl)) return false;

        AutoUpdater.AppTitle = AppTitle;
        AutoUpdater.HttpUserAgent = UserAgentProvider.UserAgent;
        AutoUpdater.TopMost = false;

        // 库内部拼相对地址时会用到 BaseUri（= 由 AppCastURL 构造），
        // 我们不走 appcast，但必须让它不为空，否则读 DownloadURL 时会抛异常
        AutoUpdater.AppCastURL = ApiUrl;
        AutoUpdater.InstalledVersion = CurrentVersion;

        // 只覆盖程序文件，绝不删目录：dbs\ 和 f\ 是用户的数据，清掉等于把人家的库端了
        AutoUpdater.ClearAppDirectory = false;
        AutoUpdater.RunUpdateAsAdmin = false;
        AutoUpdater.DownloadPath = DownloadDir;

        try
        {
            AutoUpdater.SetOwner(owner);
        }
        catch
        {
            // 宿主窗口还没建出来时 SetOwner 会失败，无伤大雅，下载窗口只是不跟着居中
        }

        var args = new UpdateInfoEventArgs
        {
            CurrentVersion = release.Version,
            InstalledVersion = CurrentVersion,
            DownloadURL = release.DownloadUrl,
            ChangelogURL = release.PageUrl,

            // zip 不需要参数；安装包则静默运行，安装器自己会关掉占着文件的程序
            InstallerArgs = release.IsZip ? null : "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
        };

        return AutoUpdater.DownloadUpdate(args);
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
    /// 从附件里挑一个能装上的：优先「更新包 zip」（解压覆盖，最稳），
    /// 其次安装包 exe，都没有就返回第一个附件。
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

            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && zip is null) zip = item;
            else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exe is null) exe = item;
        }

        return zip ?? exe ?? first;
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
        HttpStatusCode.NotFound => "没找到 Gitee 仓库或发行版（404），确认仓库地址和是否已发布版本",
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
