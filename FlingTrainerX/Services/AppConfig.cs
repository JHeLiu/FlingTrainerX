using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlingTrainerX.Services;

/// <summary>
/// 程序配置：exe 旁边的 <c>config.json</c>（编译时从项目目录复制，随安装包一起发）。
/// 文件里目前两件事合在一起：
/// <list type="bullet">
///   <item><c>curVersion</c> —— 当前版本号。发版脚本写，设置页显示、在线更新比新旧都取它。</item>
///   <item><c>autoUpdateCheck</c> —— 启动时要不要自动检查更新。用户在更新弹窗点过「取消更新」就写 false。</item>
/// </list>
/// <para>
/// <b>启动时读一次</b>存进静态字段，之后全进程用内存值；只有「取消更新」那一刻会回写文件
/// （写的时候用 <see cref="JsonObject"/> 改一个键，不动文件里的其它内容）。
/// 文件缺失 / 写坏 / 版本号不合法 → 退回程序集版本、自动检查按「开」处理，绝不让配置把程序带崩。
/// </para>
/// </summary>
public static class AppConfig
{
    /// <summary>配置文件名，跟着 exe 走。</summary>
    public const string FileName = "config.json";

    /// <summary>版本号字段名。</summary>
    public const string VersionKey = "curVersion";

    /// <summary>「启动自动检查更新」字段名。</summary>
    public const string AutoUpdateKey = "autoUpdateCheck";

    /// <summary>&lt;程序目录&gt;\config.json。</summary>
    public static string FilePath => Path.Combine(AppPaths.BaseDir, FileName);

    /// <summary>回写配置时的格式：缩进、中文不转义成 \uXXXX，看得懂也改得动。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly object Gate = new();

    /// <summary>是否已经读过文件（读失败也算读过，不会反复重试）。</summary>
    private static volatile bool _loaded;

    private static Version _version = new(0, 0, 0);

    /// <summary>版本号是从 config.json 读到的（false = 用了程序集版本兜底）。</summary>
    private static bool _fromFile;

    /// <summary>启动时是否自动检查更新（默认开；用户点过「取消更新」为关）。</summary>
    private static bool _autoUpdateCheck = true;

    /// <summary>当前版本（读不到配置时退回程序集版本）。</summary>
    public static Version Version
    {
        get
        {
            EnsureLoaded();
            return _version;
        }
    }

    /// <summary>当前版本的显示文本，例如 <c>1.0.2</c>。</summary>
    public static string VersionText => Version.ToString();

    /// <summary>这次版本号是不是从 config.json 读到的（false = 程序集兜底）。</summary>
    public static bool VersionFromFile
    {
        get
        {
            EnsureLoaded();
            return _fromFile;
        }
    }

    /// <summary>
    /// 启动时要不要自动检查更新。用户点过「取消更新」就是 false，
    /// 之后每次启动都不再自己查（设置页手动点「检查更新」不受影响）。
    /// </summary>
    public static bool AutoUpdateCheck
    {
        get
        {
            EnsureLoaded();
            return _autoUpdateCheck;
        }
    }

    /// <summary>启动时调用一次，把配置读进静态字段（重复调用无副作用）。</summary>
    public static void Initialize() => EnsureLoaded();

    /// <summary>
    /// 改「启动自动检查更新」并写回 config.json；内存值一定跟着变（写失败只影响下次启动）。
    /// </summary>
    /// <param name="value">true = 启动时自动查；false = 不再自动查。</param>
    /// <returns>true = 已经写进文件；false = 写失败（只读目录、磁盘满等）。</returns>
    public static bool SetAutoUpdateCheck(bool value)
    {
        EnsureLoaded();
        _autoUpdateCheck = value;
        return SaveAutoUpdateCheck(value);
    }

    /// <summary>一行说明，调试 / 自检日志用，例如 <c>1.0.2（config.json），自动检查更新 开</c>。</summary>
    public static string Describe() =>
        $"{VersionText}（{(VersionFromFile ? FileName : "程序集兜底")}），自动检查更新 {(_autoUpdateCheck ? "开" : "关")}";

    private static void EnsureLoaded()
    {
        if (_loaded) return;

        lock (Gate)
        {
            if (_loaded) return;

            var (version, fromFile, autoCheck) = Read();

            // 先写好值再把标记立起来，别的线程看到 _loaded 时数据一定已经就位
            _version = version;
            _fromFile = fromFile;
            _autoUpdateCheck = autoCheck;
            _loaded = true;
        }
    }

    /// <summary>读整份配置：版本号 + 自动检查开关。</summary>
    private static (Version Version, bool FromFile, bool AutoUpdateCheck) Read()
    {
        try
        {
            if (ParseRoot() is not { } root) return (AssemblyVersion(), false, true);

            var text = root.TryGetPropertyValue(VersionKey, out var v) && v is JsonValue value &&
                       value.TryGetValue<string>(out var raw)
                ? (raw ?? "").Trim()
                : "";

            // 没写这个字段 = 老配置文件 / 全新安装，按「开」处理
            var autoCheck = true;
            if (root.TryGetPropertyValue(AutoUpdateKey, out var a) && a is JsonValue flagValue &&
                flagValue.TryGetValue<bool>(out var flag))
            {
                autoCheck = flag;
            }

            return Version.TryParse(text, out var parsed) && parsed is not null
                ? (parsed, true, autoCheck)
                : (AssemblyVersion(), false, autoCheck);
        }
        catch
        {
            // 文件写坏了不影响启动：退回程序集版本，自动检查照常开
            return (AssemblyVersion(), false, true);
        }
    }

    /// <summary>把 <see cref="AutoUpdateKey"/> 写进文件，保留文件里其它键（比如 <c>curVersion</c>）。</summary>
    /// <param name="value">要写的值。</param>
    /// <returns>写成功返回 true。</returns>
    private static bool SaveAutoUpdateCheck(bool value)
    {
        try
        {
            var root = ParseRoot() ?? new JsonObject();

            // 文件里没有版本号就补一份当前版本，别写出一个只有开关的残缺配置
            if (!root.ContainsKey(VersionKey)) root[VersionKey] = VersionText;

            root[AutoUpdateKey] = value;

            File.WriteAllText(FilePath, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            // 装到只读目录时写不进去：本次运行照样按新值走，只是重启后会再提示一次
            Debug.WriteLine($"[config] 写入 {FilePath} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>把文件解析成 <see cref="JsonObject"/>；文件不存在 / 读不了 / 不是对象都返回 null。</summary>
    private static JsonObject? ParseRoot()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>程序集里带的版本（打包脚本用 -p:Version 写进去的），配置文件读不出来时兜底。</summary>
    private static Version AssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        // 优先取 InformationalVersion（能带 -beta 这种后缀），没有再用 AssemblyVersion
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var cut = info.IndexOfAny(new[] { '+', '-' });
        if (cut > 0) info = info[..cut];

        return Version.TryParse(info.Trim(), out var parsed) && parsed is not null
            ? parsed
            : assembly.GetName().Version ?? new Version(0, 0, 0);
    }
}
