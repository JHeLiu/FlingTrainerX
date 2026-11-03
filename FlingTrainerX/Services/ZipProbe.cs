namespace FlingTrainerX.Services;

/// <summary>
/// 从 zip 的中央目录里读「解压后大小」。
/// 官网的下载直链最终指向 zip 里的那个 exe，而响应是 chunked、没有 Content-Length，
/// 只有 zip 目录里记录的这个数字，才是最终落盘文件的真实字节数 —— 进度条拿它当分母。
/// </summary>
internal static class ZipProbe
{
    private const uint EocdSignature = 0x06054b50;
    private const uint CentralSignature = 0x02014b50;

    /// <summary>
    /// 从文件尾部的一段字节里解析出最大那个条目的解压后大小（官网的包里只有一个 exe）。
    /// </summary>
    /// <param name="tail">文件末尾的一段原始字节（至少包含 EOCD 与中央目录）。</param>
    /// <param name="bodyStartOffset">这段字节在文件里的起始偏移（0 表示就是从文件头开始）。</param>
    public static long UncompressedSize(byte[] tail, long bodyStartOffset)
    {
        if (tail.Length < 22) return 0;

        // EOCD 从后往前找，签名后面才是中央目录的位置
        var eocd = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(tail, i) != EocdSignature) continue;
            eocd = i;
            break;
        }

        if (eocd < 0) return 0;

        var entries = BitConverter.ToUInt16(tail, eocd + 10);
        var cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
        if (entries == 0) return 0;

        var index = (long)cdOffset - bodyStartOffset;
        if (index < 0 || index + 46 > tail.Length) return 0;

        long largest = 0;
        var cursor = (int)index;

        for (var n = 0; n < entries; n++)
        {
            if (cursor + 46 > tail.Length) break;
            if (BitConverter.ToUInt32(tail, cursor) != CentralSignature) break;

            var uncompressed = BitConverter.ToUInt32(tail, cursor + 24);
            if (uncompressed > largest) largest = uncompressed;

            var nameLength = BitConverter.ToUInt16(tail, cursor + 28);
            var extraLength = BitConverter.ToUInt16(tail, cursor + 30);
            var commentLength = BitConverter.ToUInt16(tail, cursor + 32);
            cursor += 46 + nameLength + extraLength + commentLength;
        }

        return largest;
    }
}
