using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OctavianRSM.Pages;

public partial class ConsolePage : Page
{
    private readonly List<string> _history = new();
    private int _histIdx = -1;

    // полный журнал для фильтрации (как All/Errors/Warnings в RSM Web)
    private readonly List<(string type, string tag, string text, Brush brush)> _entries = new();
    private string _typeFilter = "all";

    public ConsolePage()
    {
        InitializeComponent();
        TxtSub.Text = $"ws://{App.Settings.RconHost}:{App.Settings.RconPort} • ↑/↓ — история команд";
        App.Rcon.OnMessage += Rcon_OnMessage;
        App.Rcon.OnDisconnected += Rcon_OnDisconnected;
        // страницы пересоздаются при навигации — отписываемся, чтобы старые экземпляры не висели в событиях Rcon
        Unloaded += (_, _) =>
        {
            App.Rcon.OnMessage -= Rcon_OnMessage;
            App.Rcon.OnDisconnected -= Rcon_OnDisconnected;
        };
        if (!App.Rcon.IsConnected) _ = Connect();
        // история консоли из SQLite — не обнуляется при переходах между страницами
        foreach (var (ts, type, msg) in App.Db.RecentLogs(400))
        {
            var t = type.StartsWith("rcon:") ? type[5..] : type;
            Append(t, msg, ts.Length >= 19 ? ts[11..] : ts);
        }
        Append("sys", "— история загружена, консоль готова —");
    }

    private void Rcon_OnMessage(string type, string msg) => Dispatcher.Invoke(() => Append(type, msg));
    private void Rcon_OnDisconnected() => Dispatcher.Invoke(() => Append("sys", "— RCON отключён —"));

    private void Append(string type, string msg, string? stamp = null)
    {
        var (tag, brush) = type switch
        {
            "Chat" => ("CHAT", (Brush)FindResource("B.Success")),
            "Generic" => ("OUT", (Brush)FindResource("B.Text")),
            "sys" => ("SYS", (Brush)FindResource("B.Cyan")),
            "cmd" => (">", (Brush)FindResource("B.Accent")),
            "err" => ("ERR", (Brush)FindResource("B.Danger")),
            _ => (type.ToUpperInvariant(), (Brush)FindResource("B.TextDim"))
        };
        var text = $"[{(stamp ?? DateTime.Now.ToString("HH:mm:ss"))}] [{tag}] {msg}";
        _entries.Add((type, tag, text, brush));
        while (_entries.Count > 1500) _entries.RemoveAt(0);
        if (Pass(type, tag, text)) AddLine(text, brush);
    }

    private void AddLine(string text, Brush brush)
    {
        // автоскролл только если пользователь уже смотрит на конец — не выдёргиваем скролл из-под него
        var atBottom = Scroll.VerticalOffset >= Scroll.ScrollableHeight - 2;
        Items.Items.Add(new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        });
        while (Items.Items.Count > 800) Items.Items.RemoveAt(0);
        if (atBottom) Scroll.ScrollToEnd();
    }

    // фильтр по типу + по свободному тексту
    private bool Pass(string type, string tag, string text)
    {
        var ok = _typeFilter switch
        {
            "err" => tag == "ERR" || text.Contains("error", StringComparison.OrdinalIgnoreCase),
            "chat" => type.Equals("Chat", StringComparison.OrdinalIgnoreCase),
            "sys" => type is "sys" or "cmd",
            _ => true
        };
        if (!ok) return false;
        var f = TxtFilter?.Text?.Trim() ?? "";
        return f.Length == 0 || text.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void Refilter()
    {
        Items.Items.Clear();
        foreach (var e in _entries.Where(e => Pass(e.type, e.tag, e.text)).TakeLast(800))
            AddLine(e.text, e.brush);
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _typeFilter = sender == FErr ? "err" : sender == FChat ? "chat" : sender == FSys ? "sys" : "all";
        // подсветка активного фильтра
        FAll.Appearance = _typeFilter == "all" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        FErr.Appearance = _typeFilter == "err" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        FChat.Appearance = _typeFilter == "chat" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        FSys.Appearance = _typeFilter == "sys" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        Refilter();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => Refilter();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, Items.Items.Cast<TextBlock>().Select(t => t.Text));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private async Task Connect()
    {
        Append("sys", "Подключение…");
        var ok = await App.Rcon.ConnectAsync(App.Settings.RconHost, App.Settings.RconPort, App.Settings.RconPassword);
        Append(ok ? "sys" : "err", ok ? "RCON подключён" : "Ошибка подключения — сервер запущен?");
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var cmd = TxtCmd.Text.Trim();
        if (cmd.Length == 0) return;
        TxtCmd.Text = "";
        _history.Add(cmd); _histIdx = _history.Count;
        Append("cmd", cmd);
        var resp = await App.Rcon.SendAsync(cmd);
        if (!string.IsNullOrEmpty(resp)) Append("Generic", resp);
    }

    private void TxtCmd_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Send_Click(sender, e);
        else if (e.Key == Key.Up && _history.Count > 0)
        {
            _histIdx = Math.Max(0, _histIdx - 1);
            TxtCmd.Text = _history[_histIdx];
            TxtCmd.CaretIndex = TxtCmd.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _history.Count > 0)
        {
            _histIdx = Math.Min(_history.Count, _histIdx + 1);
            TxtCmd.Text = _histIdx < _history.Count ? _history[_histIdx] : "";
            TxtCmd.CaretIndex = TxtCmd.Text.Length;
            e.Handled = true;
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await Connect();
    private void Clear_Click(object sender, RoutedEventArgs e) { Items.Items.Clear(); _entries.Clear(); }
}
