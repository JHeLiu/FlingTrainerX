using System;

namespace FlingTrainerX.Models;

/// <summary>
/// 一个可用的新版本：数据全部来自 Gitee「发行版（releases）」接口，
/// 界面只读这里，不再自己去拼 URL。
/// </summary>
public sealed class UpdateRelease
{
    /// <summary>纯数字版本号，例如 <c>1.0.3</c>（已经从 tag 里去掉 v 前缀并校验过）。</summary>
    public string Version { get; init; } = "";

    /// <summary>Gitee 上原始的 tag，例如 <c>v1.0.3</c>，写日志 / 拼页面链接用。</summary>
    public string Tag { get; init; } = "";

    /// <summary>发行版标题（发版时填的「发行版名称」，可能为空）。</summary>
    public string Title { get; init; } = "";

    /// <summary>发行说明：Gitee 上的 Markdown 正文，已经洗成纯文本，直接铺进弹窗。</summary>
    public string Notes { get; init; } = "";

    /// <summary>发行版页面地址，用它「在浏览器中打开」。</summary>
    public string PageUrl { get; init; } = "";

    /// <summary>更新包的直链（Gitee 附件下载地址）。</summary>
    public string DownloadUrl { get; init; } = "";

    /// <summary>更新包文件名，例如 <c>风灵月影宗藏宝阁-更新包-1.0.3.zip</c>。</summary>
    public string AssetName { get; init; } = "";

    /// <summary>更新包大小（字节），0 表示接口没给。</summary>
    public long Size { get; init; }

    /// <summary>发布时间。</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>更新包是不是 zip（zip 走「解压覆盖」，exe 走「静默安装」，两条路不一样）。</summary>
    public bool IsZip => AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>发布时间显示文本，没给时间就返回空串。</summary>
    public string PublishedText =>
        PublishedAt is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd") : "";

    /// <summary>更新包大小显示文本，接口没给就返回空串。</summary>
    public string SizeText => Size <= 0 ? "" : DownloadTask.Human(Size);
}
