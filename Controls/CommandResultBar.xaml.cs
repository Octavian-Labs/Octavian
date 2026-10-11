using System.Windows;
using System.Windows.Controls;

namespace OctavianRSM.Controls;

/// <summary>
/// Единая полоса результата команды: сжатый ответ + разворачиваемый полный,
/// копирование/очистка. Подписывается на ServerContext.CommandResultPosted —
/// одинаково показывает результат любой RCON-операции на любой странице.
/// </summary>
public partial class CommandResultBar : UserControl
{
    private string _fullResult = "";

    public CommandResultBar()
    {
        InitializeComponent();
        Loaded += (_, _) => App.Ctx.CommandResultPosted += OnResult;
        Unloaded += (_, _) => App.Ctx.CommandResultPosted -= OnResult;
    }

    /// <summary>Текст в левой части (состояние страницы) — страницы пишут свои статусы сюда.</summary>
    public string StateText
    {
        get => TxtState.Text;
        set => TxtState.Text = value;
    }

    private void OnResult(string command, string result)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => OnResult(command, result)); return; }
        _fullResult = $"❯ {command}\n\n{result}";
        var short_ = result.Length > 200 ? result[..200] + "…" : result;
        TxtResult.Text = short_.Replace("\r", " ").Replace("\n", " ⏎ ");
        TxtResult.ToolTip = result;
        TxtState.Text = result.StartsWith("[") || result.Contains("не отправлена")
            ? "ошибка" : "готово";
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        bool show = PnlFull.Visibility != Visibility.Visible;
        PnlFull.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnExpand.Content = show ? "▼" : "▲";
        if (show) TxtFull.Text = _fullResult;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_fullResult)) Clipboard.SetText(_fullResult);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _fullResult = "";
        TxtResult.Text = "";
        TxtFull.Text = "";
        TxtState.Text = "готов";
        PnlFull.Visibility = Visibility.Collapsed;
        BtnExpand.Content = "▲";
    }
}
