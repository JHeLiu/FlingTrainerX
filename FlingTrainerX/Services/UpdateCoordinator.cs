using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>
/// 在线更新的那一套状态机：同一时间只查一次、查到就缓存发行版、装的时候锁住入口。
/// <para>
/// 它不碰界面：查完把 <see cref="UpdateCheckResult"/> 交回去，由设置页的 ViewModel
/// 决定那两行字怎么写、要不要弹窗。真正下载安装交给 <c>IAppDialogService.ApplyUpdate</c>
/// （AutoUpdater 需要一个 Owner 窗口，那是视图的事）。
/// </para>
/// </summary>
public sealed class UpdateCoordinator
{
    /// <summary>最近一次查到的发行版；拿到就缓存，「查看更新」不用再查一遍。</summary>
    public UpdateRelease? Release { get; private set; }

    /// <summary>本次运行已经自动查过一次更新（启动检查只跑一次）。</summary>
    public bool AutoChecked { get; private set; }

    /// <summary>
    /// 本次运行是否已经查过一次（自动或手动都算）。
    /// 启动那次检查等 1 分钟才跑，若这期间用户自己点过「检查更新」，就不再重复查、也不再弹第二遍窗。
    /// </summary>
    public bool HasChecked { get; private set; }

    /// <summary>正在下载 / 安装：这期间不再响应检查按钮，也不让 Esc 关弹窗。</summary>
    public bool IsUpdating { get; private set; }

    /// <summary>正在检查：同一时间只跑一次，避免连点几个请求叠在一起。</summary>
    public bool IsChecking { get; private set; }

    /// <summary>「查看更新」要用的发行版：没有就先查一次。</summary>
    /// <returns>拿到发行版返回它，拿不到返回 null。</returns>
    public async Task<UpdateRelease?> EnsureReleaseAsync()
    {
        if (Release is not null) return Release;

        var result = await CheckAsync();
        return result.Release;
    }

    /// <summary>
    /// 查一次更新。失败不抛异常，原因放在返回值的 Message 里。
    /// </summary>
    /// <param name="ct">取消标记（关窗口 / 超时）。</param>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (IsUpdating) return UpdateCheckResult.Fail("正在更新，等这次装完再检查");
        if (IsChecking) return UpdateCheckResult.Fail("正在检查更新…");

        IsChecking = true;
        try
        {
            HasChecked = true;

            // 20 秒还没结果就当超时，免得按钮一直转
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var result = await UpdateService.CheckAsync(timeout.Token);

            if (result.Release is { } release) Release = release;
            return result;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>标记「本次运行已经自动查过」，避免重复跑启动检查。</summary>
    /// <returns>已经查过返回 true，调用方就不必再查。</returns>
    public bool MarkAutoChecked()
    {
        if (AutoChecked) return true;

        AutoChecked = true;
        return false;
    }

    /// <summary>
    /// 交给安装器下载并安装；期间锁住「检查更新」。
    /// </summary>
    /// <param name="release">要装的发行版。</param>
    /// <param name="installer">真正干活的那一步（由视图提供，需要 Owner 窗口）。</param>
    /// <returns>false = 用户在下载窗口里点了取消。</returns>
    public bool Apply(UpdateRelease release, Func<UpdateRelease, bool> installer)
    {
        IsUpdating = true;
        try
        {
            return installer(release);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    /// <summary>
    /// 异步版：下载期间一直锁着「检查更新」，下载完才解锁。
    /// </summary>
    /// <param name="release">要下载的发行版。</param>
    /// <param name="run">下载动作（返回落地路径，取消返回 null）。</param>
    public async Task<string?> RunAsync(UpdateRelease release, Func<UpdateRelease, Task<string?>> run)
    {
        IsUpdating = true;
        try
        {
            return await run(release).ConfigureAwait(true);
        }
        finally
        {
            IsUpdating = false;
        }
    }
}
