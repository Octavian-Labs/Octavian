using System;
using System.Windows;
using System.Windows.Controls;

namespace OctavianRSM.Controls;

/// <summary>
/// Адаптивная сетка карточек: количество колонок вычисляется из доступной
/// ширины (без Viewbox и без сжатия шрифтов). Карточки в ряду — равной ширины.
/// </summary>
public class AdaptiveCardPanel : Panel
{
    public static readonly DependencyProperty MinCardWidthProperty =
        DependencyProperty.Register(nameof(MinCardWidth), typeof(double), typeof(AdaptiveCardPanel),
            new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(AdaptiveCardPanel),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Минимальная ширина карточки — меньше неё колонка не сжимается.</summary>
    public double MinCardWidth
    {
        get => (double)GetValue(MinCardWidthProperty);
        set => SetValue(MinCardWidthProperty, value);
    }

    /// <summary>Горизонтальный и вертикальный зазор между карточками.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int ColumnsFor(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return 1;
        return Math.Max(1, (int)((width + Spacing) / (MinCardWidth + Spacing)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int cols = ColumnsFor(availableSize.Width);
        double cardW = double.IsInfinity(availableSize.Width)
            ? MinCardWidth
            : Math.Max(0, (availableSize.Width - Spacing * (cols - 1)) / cols);
        double y = 0, rowH = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cardW, double.PositiveInfinity));
            rowH = Math.Max(rowH, child.DesiredSize.Height);
            if (++i % cols == 0) { y += rowH + Spacing; rowH = 0; }
        }
        if (i % cols != 0) y += rowH;
        else if (y > 0) y -= Spacing;
        return new Size(availableSize.Width, y);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = ColumnsFor(finalSize.Width);
        double cardW = Math.Max(0, (finalSize.Width - Spacing * (cols - 1)) / cols);
        double x = 0, y = 0, rowH = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            double h = child.DesiredSize.Height;
            rowH = Math.Max(rowH, h);
            child.Arrange(new Rect(x, y, cardW, h));
            if (++i % cols == 0) { y += rowH + Spacing; x = 0; rowH = 0; }
            else x += cardW + Spacing;
        }
        return finalSize;
    }
}
