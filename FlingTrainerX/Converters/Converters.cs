using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlingTrainerX.Converters;

/// <summary>把本地相对路径 / http 地址转成可复用的 BitmapImage（同一张图只解码一次）。</summary>
public sealed class ImagePathConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly BitmapImage? Placeholder = null;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path)) return Placeholder;

        if (Cache.TryGetValue(path, out var cached)) return cached;

        ImageSource? img = null;
        try
        {
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                img = bmp;
            }
            else
            {
                var full = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(AppContext.BaseDirectory, "Assets", path);
                if (File.Exists(full))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(full, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 700;
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
            }
        }
        catch
        {
            img = null;
        }

        Cache[path] = img;
        return img;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（参数 "invert" 取反）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串为空 → Collapsed。</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = !string.IsNullOrWhiteSpace(value as string);
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>没有竖版海报时给截图加模糊，避免卡片太乱。</summary>
public sealed class PosterBlurConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasPoster = value is bool b && b;
        return hasPoster ? null : new System.Windows.Media.Effects.BlurEffect
        {
            Radius = 26,
            KernelType = System.Windows.Media.Effects.KernelType.Gaussian
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
