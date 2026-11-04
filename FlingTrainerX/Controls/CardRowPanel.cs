using System;
using System.Windows;
using System.Windows.Controls;

namespace FlingTrainerX.Controls;

/// <summary>
/// 一行卡片的排布面板：按「固定列数 + 固定间距」把可用宽度**精确分完**，
/// 第一张的左边缘贴住容器左边、最后一张的右边缘贴住容器右边（扣掉最后一格间距）。
/// <para>
/// 为什么不用 UniformGrid：UniformGrid 是「先把宽度平分成 N 格，再把卡片居中放进去」，
/// 卡片宽度取整之后每格都会剩几个像素，攒起来就是卡片左右各缩进几像素、
/// 和上面的标题 / 顶栏对不齐。这里直接算卡片宽度并线性排布，两边严丝合缝。
/// </para>
/// </summary>
public sealed class CardRowPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CardRowPanel),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(CardRowPanel),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxChildWidthProperty = DependencyProperty.Register(
        nameof(MaxChildWidth), typeof(double), typeof(CardRowPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>一行固定几张（不足一张的末行也按这个宽度排，卡片不会变大）。</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>卡片之间的横向空隙。</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>
    /// 单卡宽度上限（由窗口高度反推出来的 <c>CardW</c>）。
    /// 宽屏上「3 行必须装得下」会把卡片压窄，这时行内按上限排并整行居中，
    /// 不会把卡片拉回满宽导致封面比例走样。
    /// </summary>
    public double MaxChildWidth
    {
        get => (double)GetValue(MaxChildWidthProperty);
        set => SetValue(MaxChildWidthProperty, value);
    }

    /// <summary>
    /// 实际用的单卡宽度：平分宽度，但不超过上限（上限按窗口高度算，保证 3 行不用滚动）。
    /// </summary>
    private double CardWidthOf(double available)
    {
        var columns = Math.Max(1, Columns);
        var width = CardWidthOf(available, columns, Math.Max(0, Gap));

        var max = MaxChildWidth;
        if (!double.IsInfinity(max) && max > 0 && width > max) width = max;

        return width;
    }

    /// <summary>
    /// 实际用的间距：卡片被上限压窄时，把省下来的宽度平均摊到空隙里，
    /// 这样一行仍然从左边缘铺到右边缘（和上面的标题严格对齐），只是卡片间隙略宽一点。
    /// </summary>
    private double GapOf(double available, double cardWidth)
    {
        var columns = Math.Max(1, Columns);
        var gap = Math.Max(0, Gap);

        var used = cardWidth * columns + gap * (columns - 1);
        if (used >= available - 0.5 || columns <= 1) return gap;

        return gap + (available - used) / (columns - 1);
    }

    /// <summary>按列数和间距算出来的单卡宽度，供外部（行高计算）取用。</summary>
    public static double CardWidthOf(double available, int columns, double gap)
    {
        if (columns <= 0) columns = 1;
        return Math.Max(0, (available - gap * (columns - 1)) / columns);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 宽度未知（放在 ScrollViewer 里且水平方向不限）时先按子元素自身量，避免宽度算成 0
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var cardWidth = width > 0 ? CardWidthOf(width) : 0;

        double height = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(cardWidth > 0
                ? new Size(cardWidth, availableSize.Height)
                : new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cardWidth = CardWidthOf(finalSize.Width);
        var gap = GapOf(finalSize.Width, cardWidth);

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            // 卡片被压窄时空隙变大，但首个仍贴左边、末个仍贴右边
            var x = i * (cardWidth + gap);
            // 末行不满 5 张时也用同样的宽度，卡片不会突然变胖
            InternalChildren[i].Arrange(new Rect(x, 0, cardWidth, finalSize.Height));
        }

        return finalSize;
    }
}
