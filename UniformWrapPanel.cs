using System;
using System.Windows;
using System.Windows.Controls;

namespace OctavianRSM;

/// <summary>
/// Адаптивная сетка карточек: число колонок подбирается по доступной ширине
/// (1..MaxColumns), ячейки одинаковой ширины и заполняют ряд целиком,
/// высота строки — по самой высокой карточке. Заменяет WrapPanel там,
/// где карточки должны тянуться при растягивании окна.
/// </summary>
public class UniformWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(UniformWrapPanel),
            new FrameworkPropertyMetadata(300.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(UniformWrapPanel),
            new FrameworkPropertyMetadata(4,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(UniformWrapPanel),
            new FrameworkPropertyMetadata(12.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Минимальная ширина ячейки — от неё считается число колонок.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>Верхний предел колонок — на широком окне сетка не расползается.</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>Отступ между ячейками по горизонтали и вертикали.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int ColumnsFor(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return 1;
        var cols = (int)Math.Floor((width + Spacing) / (MinItemWidth + Spacing));
        return Math.Max(1, Math.Min(MaxColumns, cols));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cols = ColumnsFor(availableSize.Width);
        var cw = Math.Max(0, (availableSize.Width - Spacing * (cols - 1)) / cols);
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(cw, double.PositiveInfinity));

        double total = 0;
        var count = InternalChildren.Count;
        for (var i = 0; i < count; i += cols)
        {
            var rowH = RowHeight(i, cols, count);
            total += rowH;
            if (i + cols < count) total += Spacing;
        }
        return new Size(availableSize.Width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = ColumnsFor(finalSize.Width);
        var cw = Math.Max(0, (finalSize.Width - Spacing * (cols - 1)) / cols);
        var count = InternalChildren.Count;
        double y = 0;
        for (var i = 0; i < count; i += cols)
        {
            var rowH = RowHeight(i, cols, count);
            double x = 0;
            for (var j = i; j < Math.Min(i + cols, count); j++)
            {
                InternalChildren[j].Arrange(new Rect(x, y, cw, rowH));
                x += cw + Spacing;
            }
            y += rowH + Spacing;
        }
        return finalSize;
    }

    private double RowHeight(int start, int cols, int count)
    {
        double h = 0;
        for (var j = start; j < Math.Min(start + cols, count); j++)
            h = Math.Max(h, InternalChildren[j].DesiredSize.Height);
        return h;
    }
}
