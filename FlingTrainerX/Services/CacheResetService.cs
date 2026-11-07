using System.IO;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>
/// 「清除本地缓存」这一整套流程：停下载 → 等还在飞的任务收尾 → 清 SQLite 缓存表 →
/// 删 <c>f\</c> 目录 → 再多扫几轮把迟到的落盘文件一起清掉。
/// <para>
/// 纯后台逻辑，没有界面：<see cref="LibraryCatalog"/> 和下载管理器是它的输入，
/// 返回一句给人看的总结，由调用方决定写进状态栏还是弹窗。
/// </para>
/// </summary>
public sealed class CacheResetService
{
    /// <summary>正在清：这期间再点「清除缓存」不会叠着来一遍。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>上一次清除 + 重新加载是否已经走完（自检用它等一个稳定状态）。</summary>
    public bool IsDone { get; private set; } = true;

    /// <summary>
    /// 执行一次清除。
    /// </summary>
    /// <param name="stillLoading">
    /// 首页 / 翻页是否还有请求在跑；返回 true 时先等它收尾，避免「刚删完又被写回去」。
    /// </param>
    /// <param name="ct">取消标记。</param>
    /// <returns>一行总结，例如「已清除缓存 · 320 条缓存记录 · 812 个文件（1.2 GB）」。</returns>
    public async Task<string> ClearAsync(Func<bool> stillLoading, CancellationToken ct = default)
    {
        if (IsRunning) return "";
        IsRunning = true;
        IsDone = false;

        try
        {
            AppDatabase.MuteCache();            // 掐断在途下载，别让缓存写入和落盘又填回来

            // 1) 先停掉正在下的任务，免得一边删一边又写回来
            foreach (var task in DownloadManager.Instance.Tasks.Where(t => t.IsActive).ToList())
                DownloadManager.Instance.Cancel(task);

            // 2) 等首页加载 / 翻页请求 / 封面下载 / 被取消的任务收尾
            for (var i = 0; i < 40 && (stillLoading() || DownloadManager.Instance.ActiveCount > 0); i++)
                await Task.Delay(100, ct);

            // 3) 清空 SQLite 里的卡片缓存表（收藏夹那张表不动）+ 删掉 f 目录下的所有内容
            var rows = AppDatabase.Instance.ClearCache();
            var (files, bytes, failed) = DeleteContents(AppPaths.FilesDir);

            // 4) 已经在传输中的封面可能还会落盘，多扫几轮直到连续一轮没东西可删
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(400, ct);
                rows += AppDatabase.Instance.ClearCache();

                var (extraFiles, extraBytes, extraFailed) = DeleteContents(AppPaths.FilesDir);
                files += extraFiles;
                bytes += extraBytes;
                failed = extraFailed;

                if (extraFiles == 0 && extraFailed == 0) break;   // 这一轮没东西可删 = 真的空了
            }

            return $"已清除缓存 · {rows} 条缓存记录 · {files} 个文件（{DownloadTask.Human(bytes)}）" +
                   (failed > 0 ? $" · {failed} 项被占用，已跳过" : "");
        }
        finally
        {
            // 再静音一小会儿，别让马上又开始的下一次下载把刚清掉的东西立刻写回来
            try { await Task.Delay(800, ct); } catch (OperationCanceledException) { }

            AppDatabase.UnmuteCache();
            IsRunning = false;
        }
    }

    /// <summary>清完缓存 + 重新加载这一整套走完了。</summary>
    public void MarkDone() => IsDone = true;

    /// <summary>
    /// 删掉目录下的全部内容（保留目录本身）。单个文件被占用时跳过、不中断，
    /// 返回删掉的文件数、总字节数和失败项数。
    /// </summary>
    private static (int files, long bytes, int failed) DeleteContents(string root)
    {
        if (!Directory.Exists(root)) return (0, 0, 0);

        var files = 0;
        var bytes = 0L;
        var failed = 0;

        // 子目录（f\<id>\）整棵删掉
        foreach (var dir in Directory.EnumerateDirectories(root).ToList())
        {
            try
            {
                var info = new DirectoryInfo(dir);
                var count = 0;
                var size = 0L;
                foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    count++;
                    size += file.Length;
                }

                info.Delete(recursive: true);
                files += count;
                bytes += size;
            }
            catch
            {
                failed++;
            }
        }

        // 根目录下散着的文件
        foreach (var path in Directory.EnumerateFiles(root).ToList())
        {
            try
            {
                bytes += new FileInfo(path).Length;
                File.Delete(path);
                files++;
            }
            catch
            {
                failed++;
            }
        }

        return (files, bytes, failed);
    }
}
