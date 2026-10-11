using System.Windows;
using System.Windows.Controls;

namespace OctavianRSM.Controls;

/// <summary>
/// Унифицированная строка настройки: заголовок + пояснение слева,
/// редактор справа, опциональный значок «заблокировано» (launch-lock,
/// недоступно по capability) — вместо разнородных рядов на страницах.
/// </summary>
public partial class SettingRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow),
            new PropertyMetadata("", OnTextChanged));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SettingRow),
            new PropertyMetadata("", OnTextChanged));

    /// <summary>Причина блокировки; непустое значение гасит редактор и показывает бейдж.</summary>
    public static readonly DependencyProperty LockReasonProperty =
        DependencyProperty.Register(nameof(LockReason), typeof(string), typeof(SettingRow),
            new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty EditorProperty =
        DependencyProperty.Register(nameof(Editor), typeof(object), typeof(SettingRow),
            new PropertyMetadata(null, OnTextChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string? LockReason
    {
        get => (string?)GetValue(LockReasonProperty);
        set => SetValue(LockReasonProperty, value);
    }

    public object? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public SettingRow()
    {
        InitializeComponent();
        Sync();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SettingRow)d).Sync();

    private void Sync()
    {
        if (TxtTitle == null) return;
        TxtTitle.Text = Title;
        TxtHint.Text = Hint;
        TxtHint.Visibility = string.IsNullOrEmpty(Hint) ? Visibility.Collapsed : Visibility.Visible;
        bool locked = !string.IsNullOrEmpty(LockReason);
        LockBadge.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        if (locked) TxtLock.Text = "🔒 " + LockReason;
        EditorHost.Content = Editor;
        if (Editor is UIElement el) el.IsEnabled = !locked;
    }
}
