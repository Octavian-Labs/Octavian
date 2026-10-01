using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OctavianRSM;

/// <summary>
/// Кастомная шапка окна в стиле главного: drag-зона, свои кнопки
/// свернуть/развернуть/закрыть. Используется в окнах с WindowStyle="None".
/// </summary>
public partial class OctaTitleBar : UserControl
{
    public static readonly DependencyProperty TitleTextProperty =
        DependencyProperty.Register(nameof(TitleText), typeof(string), typeof(OctaTitleBar),
            new PropertyMetadata("OCTAVIAN RSM"));

    public static readonly DependencyProperty TitleSuffixProperty =
        DependencyProperty.Register(nameof(TitleSuffix), typeof(string), typeof(OctaTitleBar),
            new PropertyMetadata(""));

    /// <summary>Показывать кнопку «свернуть».</summary>
    public static readonly DependencyProperty AllowMinProperty =
        DependencyProperty.Register(nameof(AllowMin), typeof(bool), typeof(OctaTitleBar),
            new PropertyMetadata(false, OnButtonsChanged));

    /// <summary>Показывать кнопку «развернуть» и разрешить двойной клик по шапке.</summary>
    public static readonly DependencyProperty AllowMaxProperty =
        DependencyProperty.Register(nameof(AllowMax), typeof(bool), typeof(OctaTitleBar),
            new PropertyMetadata(false, OnButtonsChanged));

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public string TitleSuffix
    {
        get => (string)GetValue(TitleSuffixProperty);
        set => SetValue(TitleSuffixProperty, value);
    }

    public bool AllowMin
    {
        get => (bool)GetValue(AllowMinProperty);
        set => SetValue(AllowMinProperty, value);
    }

    public bool AllowMax
    {
        get => (bool)GetValue(AllowMaxProperty);
        set => SetValue(AllowMaxProperty, value);
    }

    private Window? _win;
    private bool _pseudoMax;
    private Rect _restoredBounds;

    public OctaTitleBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _win = Window.GetWindow(this);
            if (_win != null)
            {
                _win.StateChanged += OnWindowStateChanged;
                ApplyButtonVisibility();
            }
        };
    }

    private static void OnButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((OctaTitleBar)d).ApplyButtonVisibility();

    private void ApplyButtonVisibility()
    {
        if (BtnMin == null || BtnMax == null) return;
        BtnMin.Visibility = AllowMin ? Visibility.Visible : Visibility.Collapsed;
        BtnMax.Visibility = AllowMax ? Visibility.Visible : Visibility.Collapsed;
    }

    // Системный maximize у окна без хрома залезает на панель задач —
    // перехватываем (Win+Up, Aero Snap) и разворачиваем вручную по рабочей области
    private void OnWindowStateChanged(object? sender, System.EventArgs e)
    {
        if (_win == null || _win.WindowState != WindowState.Maximized) return;
        var rb = _win.RestoreBounds;
        _restoredBounds = rb.IsEmpty ? _restoredBounds : rb;
        _win.WindowState = WindowState.Normal;
        ApplyMaxBounds();
    }

    private void ApplyMaxBounds()
    {
        var wa = SystemParameters.WorkArea;
        _win!.Left = wa.Left;
        _win.Top = wa.Top;
        _win.Width = wa.Width;
        _win.Height = wa.Height;
        _pseudoMax = true;
        BtnMax.Content = "❐";
    }

    private void ToggleMax()
    {
        if (_win == null || !AllowMax) return;
        if (_pseudoMax)
        {
            _pseudoMax = false;
            if (!_restoredBounds.IsEmpty && _restoredBounds.Width > 0)
            {
                _win.Left = _restoredBounds.Left;
                _win.Top = _restoredBounds.Top;
                _win.Width = _restoredBounds.Width;
                _win.Height = _restoredBounds.Height;
            }
            BtnMax.Content = "▢";
            return;
        }
        var rb = _win.RestoreBounds;
        _restoredBounds = (_win.WindowState == WindowState.Normal || rb.IsEmpty)
            ? new Rect(_win.Left, _win.Top, _win.Width, _win.Height)
            : rb;
        if (_win.WindowState != WindowState.Normal)
            _win.WindowState = WindowState.Normal;
        ApplyMaxBounds();
    }

    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_win == null || e.ButtonState != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMax();
            return;
        }
        // Перетаскивание из развёрнутого состояния: сначала возвращаем нормальный
        // размер так, чтобы курсор остался на той же пропорции шапки
        if (_pseudoMax)
        {
            var rel = e.GetPosition(this);
            double ratioX = ActualWidth > 0 ? rel.X / ActualWidth : 0.5;
            var screen = PointToScreen(rel);
            ToggleMax();
            _win.Left = screen.X - _win.Width * ratioX;
            _win.Top = screen.Y - rel.Y;
        }
        try { _win.DragMove(); } catch { }
    }

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        if (_win != null) _win.WindowState = WindowState.Minimized;
    }

    private void Max_Click(object sender, RoutedEventArgs e) => ToggleMax();

    private void Close_Click(object sender, RoutedEventArgs e) => _win?.Close();
}
