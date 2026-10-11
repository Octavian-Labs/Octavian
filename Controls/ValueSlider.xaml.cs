using System.Windows;
using System.Windows.Controls;

namespace OctavianRSM.Controls;

/// <summary>
/// Слайдер с точным числовым вводом: значение можно задать и ползунком,
/// и прямым текстом — поле справа. Требование ТЗ: числовые поля не должны
/// ограничиваться только слайдером.
/// </summary>
public partial class ValueSlider : UserControl
{
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(ValueSlider),
            new PropertyMetadata(0.0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(ValueSlider),
            new PropertyMetadata(10.0, OnRangeChanged));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(ValueSlider),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty TickFrequencyProperty =
        DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(ValueSlider),
            new PropertyMetadata(1.0, OnRangeChanged));

    public static readonly DependencyProperty SnapToTickProperty =
        DependencyProperty.Register(nameof(SnapToTick), typeof(bool), typeof(ValueSlider),
            new PropertyMetadata(true, OnRangeChanged));

    private bool _syncing;

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double TickFrequency
    {
        get => (double)GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    public bool SnapToTick
    {
        get => (bool)GetValue(SnapToTickProperty);
        set => SetValue(SnapToTickProperty, value);
    }

    public ValueSlider()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncRange();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ValueSlider)d).SyncRange();

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ValueSlider)d;
        if (c._syncing || c.PartSlider == null || c.PartBox == null) return;
        c._syncing = true;
        c.PartSlider.Value = c.Value;
        c.PartBox.Value = c.Value;
        c._syncing = false;
    }

    private void SyncRange()
    {
        if (PartSlider == null || PartBox == null) return;
        PartSlider.Minimum = Minimum; PartSlider.Maximum = Maximum;
        PartSlider.TickFrequency = TickFrequency;
        PartSlider.IsSnapToTickEnabled = SnapToTick;
        PartBox.Minimum = Minimum; PartBox.Maximum = Maximum;
        _syncing = true;
        PartSlider.Value = Value; PartBox.Value = Value;
        _syncing = false;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Value = PartSlider.Value;
    }

    private void Box_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var v = PartBox.Value;
        if (v.HasValue && !double.IsNaN(v.Value)) Value = v.Value;
    }
}
