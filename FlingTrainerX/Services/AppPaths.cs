using System.IO;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>
/// 程序目录下的固定子目录。都放在 exe 旁边，方便直接找到文件；
/// 目录不存在时按需创建，创建失败也不抛异常（由调用方决定怎么退化）。
/// </summary>
public static class AppPaths
{
    /// <summary>程序所在目录：调试时是 bin\Debug\net10.0-windows，发布后就是 exe 所在目录。</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>每款游戏自己的文件夹根目录：&lt;程序目录&gt;\f</summary>
    public static string FilesDir => Ensure(Path.Combine(BaseDir, "f"));

    /// <summary>
    /// 某一款游戏的数据目录：&lt;程序目录&gt;\f\&lt;官网文章 ID&gt;\。
    /// 封面、介绍截图、下载的修改器都放在这里，一个游戏一个文件夹。
    /// </summary>
    public static string FilesFor(int trainerId) =>
        Ensure(Path.Combine(FilesDir, trainerId.ToString()));

    /// <summary>SQLite 数据库目录：&lt;程序目录&gt;\dbs</summary>
    public static string DbDir => Ensure(Path.Combine(BaseDir, "dbs"));

    public static string DatabaseFile => Path.Combine(DbDir, "fling.db");

    /// <summary>
    /// 程序目录下 Assets\ 里某个随包发布的静态文件的路径。
    /// <para>
    /// 单文件发布时 <see cref="BaseDir"/>（AppContext.BaseDirectory）就是 exe 所在目录，
    /// 正常情况第一个候选就命中。万一哪天用了会解压到临时目录的发布方式，
    /// 再退到 <see cref="Environment.ProcessPath"/> 所在的目录找一遍；都没有就返回第一个候选，
    /// 由调用方给出「文件缺失」的提示。
    /// </para>
    /// </summary>
    public static string AssetFile(string fileName)
    {
        var first = Path.Combine(BaseDir, "Assets", fileName);

        try
        {
            if (File.Exists(first)) return first;

            var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
            if (!string.IsNullOrEmpty(exeDir))
            {
                var second = Path.Combine(exeDir, "Assets", fileName);
                if (File.Exists(second)) return second;
            }
        }
        catch
        {
            // 拿不到进程路径就走第一个候选
        }

        return first;
    }

    // ==================== 路径存库：一律存相对路径 ====================

    /// <summary>
    /// 绝对路径 → 相对程序目录的路径（例如 <c>f\1142\cover.jpg</c>），用于写进数据库。
    /// 程序换目录、换盘符后这些记录依然有效。不在程序目录下的（用户手工指定的外部文件）
    /// 没法用相对路径表达，原样返回绝对路径。
    /// </summary>
    public static string Relative(string? absolute)
    {
        if (string.IsNullOrWhiteSpace(absolute)) return "";

        try
        {
            if (!Path.IsPathRooted(absolute)) return absolute;   // 本来就是相对路径

            var full = Path.GetFullPath(absolute);
            var rel = Path.GetRelativePath(BaseDir, full);

            // "..\.." 开头说明在程序目录外，只好保留绝对路径
            return rel.StartsWith("..", StringComparison.Ordinal) ? full : rel;
        }
        catch
        {
            return absolute;
        }
    }

    /// <summary>
    /// 数据库里存的相对路径 → 本机绝对路径。<paramref name="stored"/> 已经是绝对路径
    /// （老版本数据或外部文件）时原样返回，保证兼容。
    /// </summary>
    public static string Resolve(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";

        try
        {
            return Path.IsPathRooted(stored)
                ? stored
                : Path.GetFullPath(Path.Combine(BaseDir, stored));
        }
        catch
        {
            return stored;
        }
    }

    /// <summary>
    /// 把一条记录里的本地路径统统改成相对路径（老数据升级用）。
    /// 返回是否真的改动过，没改动就不用回写数据库。
    /// </summary>
    public static bool NormalizeInPlace(TrainerItem item)
    {
        var changed = false;

        if (Path.IsPathRooted(item.CoverPath))
        {
            var rel = Relative(item.CoverPath);
            if (rel != item.CoverPath)
            {
                item.CoverPath = rel;
                changed = true;
            }
        }

        for (var i = 0; i < item.ImagePaths.Count; i++)
        {
            var path = item.ImagePaths[i];
            if (!Path.IsPathRooted(path)) continue;

            var rel = Relative(path);
            if (rel == path) continue;

            item.ImagePaths[i] = rel;
            changed = true;
        }

        foreach (var file in item.Downloads ?? new List<TrainerDownload>())
        {
            if (!Path.IsPathRooted(file.LocalPath)) continue;

            var rel = Relative(file.LocalPath);
            if (rel == file.LocalPath) continue;

            file.LocalPath = rel;
            changed = true;
        }

        return changed;
    }

    private static string Ensure(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // 只读目录（例如装在 Program Files 且没有写权限）下交给调用方报错
        }

        return dir;
    }
}
