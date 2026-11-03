using System.IO;
using System.Windows.Media.Imaging;

namespace FlingTrainerX.Services;

/// <summary>
/// 图片统一入口：f\&lt;id&gt;\ 下的本地文件直接读盘，官网地址交给 WPF 异步下载、按需解码。
/// 不裁剪、不改尺寸。
/// <para>
/// 这里必须是有上限的缓存：解码后的位图非常占内存（一张 520×292 的封面就是 0.6 MB，
/// 整库几百款一路滚下来能吃掉好几百兆），所以只留最近用到的若干张（LRU），
/// 滚过去的图直接从表里丢出去，离开屏幕后由 GC 回收；再滚回来时从本地文件重新解码，
/// 一张十几毫秒，看不出来。
/// </para>
/// </summary>
public static class NetImages
{
    /// <summary>最多缓存多少张（按张数先兜一层）。</summary>
    private const int MaxEntries = 24;

    /// <summary>再按解码后的字节数兜一层，截图这类大图会被提前挤掉。</summary>
    private const long MaxBytes = 16L * 1024 * 1024;

    private sealed class Entry
    {
        public Entry(string key, BitmapImage? bitmap)
        {
            Key = key;
            Bitmap = bitmap;
            Bytes = Estimate(bitmap);
        }

        public string Key { get; }

        public BitmapImage? Bitmap { get; }

        public long Bytes { get; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Index = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Entry> Lru = new();
    private static long _bytes;

    /// <summary>取一张图；网络图还没下完时返回的 BitmapImage 会在后台继续下载，完成后界面自动刷新。</summary>
    public static BitmapImage? Get(string? source)    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        lock (Gate)
        {
            if (Index.TryGetValue(source, out var hit))
            {
                Lru.Remove(hit);
                Lru.AddFirst(hit);
                return hit.Value.Bitmap;
            }
        }

        var bmp = Load(source);

        // 失败的也记下来（null），免得每次绑定都重试一次；本地文件可能是稍后才落盘的，所以本地路径不记失败
        if (bmp is null && IsLocal(source)) return null;

        lock (Gate)
        {
            // 期间别的线程可能刚把同一张图放进来
            if (Index.TryGetValue(source, out var again))
            {
                Lru.Remove(again);
                Lru.AddFirst(again);
                return again.Value.Bitmap;
            }

            var node = Lru.AddFirst(new Entry(source, bmp));
            Index[source] = node;
            _bytes += node.Value.Bytes;

            Trim();
        }

        return bmp;
    }

    /// <summary>把最久没用到的图挤出去，直到张数和字节数都回到上限内。</summary>
    private static void Trim()
    {
        while (Lru.Count > 0 && (Lru.Count > MaxEntries || _bytes > MaxBytes))
        {
            var last = Lru.Last;
            if (last is null) break;

            Lru.RemoveLast();
            Index.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
        }
    }

    /// <summary>估算解码后占多少字节；网络图在下载完成前拿不到像素尺寸，先按 1 MB 记。</summary>
    private static long Estimate(BitmapImage? bmp)
    {
        if (bmp is null) return 0;

        try
        {
            var pixels = (long)bmp.PixelWidth * bmp.PixelHeight;
            if (pixels > 0) return pixels * 4;   // Bgra32，每像素 4 字节
        }
        catch
        {
            // 某些状态下取像素尺寸会抛，走下面的兜底
        }

        return 1024 * 1024;
    }

    private static BitmapImage? Load(string source)
    {
        try
        {
            if (IsLocal(source))
            {
                if (!File.Exists(source)) return null;

                var local = new BitmapImage();
                local.BeginInit();
                local.UriSource = new Uri(source, UriKind.Absolute);
                // 本地文件用 OnLoad：一次读完并释放文件句柄，之后删除/替换都不会被占用
                local.CacheOption = BitmapCacheOption.OnLoad;
                local.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                local.EndInit();
                local.Freeze();
                return local;
            }

            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

            var remote = new BitmapImage();
            remote.BeginInit();
            remote.UriSource = uri;
            // OnDemand：下载与解码都在后台，不阻塞调用线程；OnLoad 会在当前线程等整张图下完
            remote.CacheOption = BitmapCacheOption.OnDemand;
            remote.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            remote.EndInit();

            return remote;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLocal(string source) =>
        source.Length > 2 && source[1] == ':' || source.StartsWith(@"\\", StringComparison.Ordinal);
}
