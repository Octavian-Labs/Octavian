using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace OctavianRSM.Controls;

/// <summary>
/// Общий виртуализованный просмотр лога: консоль RCON, журнал сервера,
/// вывод SteamCMD, история чата. Держит до MaxLines записей, фильтрует
/// по подстроке и предикату категории, автодоскролл.
/// </summary>
public partial class LogViewer : UserControl
{
    public sealed class LogEntry
    {
        public required string Text { get; init; }
        public string Category { get; init; } = "";
        public Brush Foreground { get; init; } = Brushes.Gray;
    }

    private readonly ObservableCollection<LogEntry> _items = new();
    private readonly ICollectionView _view;
    private string _filterText = "";
    private Func<LogEntry, bool>? _predicate;
    private bool _autoScroll = true;
    // прилипание: прокручиваем вниз только пока пользователь смотрит на конец;
    // вернуть следование — прокрутить до низа вручную
    private bool _stick = true;

    public LogViewer()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => Passes((LogEntry)o);
        List.ItemsSource = _view;
        // ScrollChanged пробрасывается из внутреннего ScrollViewer шаблона ListBox
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    /// <summary>Потолок буфера — 10k+ строк не должны вешать UI ни памятью, ни разметкой.</summary>
    public int MaxLines { get; set; } = 20000;

    /// <summary>Рисовать ли рамку-панель вокруг списка (false — контрол уже внутри OctaPanel).</summary>
    public bool UsePanel
    {
        get => (bool)GetValue(UsePanelProperty);
        set => SetValue(UsePanelProperty, value);
    }

    public static readonly DependencyProperty UsePanelProperty =
        DependencyProperty.Register(nameof(UsePanel), typeof(bool), typeof(LogViewer),
            new FrameworkPropertyMetadata(true, (d, _) =>
            {
                var lv = (LogViewer)d;
                lv.Frame.BorderThickness = new Thickness(0);
                lv.Frame.Background = Brushes.Transparent;
            }));

    /// <summary>Автоскролл разрешён вообще (фактически работает только пока _stick).</summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set => _autoScroll = value;
    }

    /// <summary>Текстовый фильтр (подстрока, без учёта регистра).</summary>
    public string FilterText
    {
        get => _filterText;
        set { _filterText = value ?? ""; _view.Refresh(); _stick = true; ScrollToEnd(); }
    }

    /// <summary>Фильтр по категории: null = показывать всё.</summary>
    public Func<LogEntry, bool>? Predicate
    {
        get => _predicate;
        set { _predicate = value; _view.Refresh(); _stick = true; ScrollToEnd(); }
    }

    public int Count => _items.Count;

    private bool Passes(LogEntry e)
    {
        if (_predicate != null && !_predicate(e)) return false;
        return _filterText.Length == 0
            || e.Text.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    public void AddLine(string text, string category = "", Brush? foreground = null)
    {
        var brush = foreground ?? DefaultBrush(category);
        _items.Add(new LogEntry { Text = text, Category = category, Foreground = brush });
        // чистим пачкой, чтобы не дёргать коллекцию построчно
        if (_items.Count > MaxLines)
        {
            int cut = _items.Count - MaxLines + 200;
            for (int i = 0; i < cut; i++) _items.RemoveAt(0);
        }
        if (_autoScroll && _stick) ScrollToEnd();
    }

    public void AddLines(IEnumerable<string> lines, string category = "", Brush? foreground = null)
    {
        foreach (var l in lines) _items.Add(new LogEntry
            { Text = l, Category = category, Foreground = foreground ?? DefaultBrush(category) });
        if (_items.Count > MaxLines)
        {
            int cut = _items.Count - MaxLines + 200;
            for (int i = 0; i < cut; i++) _items.RemoveAt(0);
        }
        if (_autoScroll && _stick) ScrollToEnd();
    }

    // пользователь скроллил: ушёл вверх — пауза следования, вернулся вниз — снова следуем
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return; // вырос контент, а не жест пользователя
        if (e.OriginalSource is not ScrollViewer sv) return;
        _stick = sv.VerticalOffset >= sv.ScrollableHeight - 4;
    }

    public void ScrollToEnd()
    {
        if (_view.Cast<object>().LastOrDefault() is { } last)
            List.ScrollIntoView(last);
    }

    public void ClearLog() => _items.Clear();

    /// <summary>Весь текущий буфер (для копирования/экспорта).</summary>
    public string AllText() => string.Join("\n", _items.Select(e => e.Text));

    /// <summary>Только видимые после фильтра строки.</summary>
    public string VisibleText() => string.Join("\n", _view.Cast<LogEntry>().Select(e => e.Text));

    private Brush DefaultBrush(string category) => category switch
    {
        "err" or "error" => (Brush)FindResource("B.Danger"),
        "warn" => (Brush)FindResource("B.Warn"),
        "chat" => (Brush)FindResource("B.Cyan"),
        "ok" => (Brush)FindResource("B.Success"),
        _ => (Brush)FindResource("B.TextDim"),
    };

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is LogEntry row)
            Clipboard.SetText(row.Text);
    }

    private void CopyVisible_Click(object sender, RoutedEventArgs e)
        => Clipboard.SetText(string.Join("\n", _view.Cast<LogEntry>().Select(x => x.Text)));

    private void CopyAll_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(AllText());

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearLog();
}
