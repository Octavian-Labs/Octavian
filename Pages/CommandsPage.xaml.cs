using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OctavianRSM.Models;
using WpfNumberBox = Wpf.Ui.Controls.NumberBox;
using WpfTextBox = Wpf.Ui.Controls.TextBox;

namespace OctavianRSM.Pages;

public partial class CommandsPage : Page
{
    private readonly CancellationTokenSource _pageCts = new();
    private static readonly string[] LoadConvars =
    {
        "spawn.min_rate", "spawn.max_rate", "spawn.min_density", "spawn.max_density",
        "ai.npc_enable", "ai.think", "ai.move"
    };
    private static readonly string[] WorldConvars = { "server.pve", "decay.scale", "decay.upkeep" };

    public CommandsPage()
    {
        InitializeComponent();
        UpdateRconState();
        App.Server.OnStateChanged += Server_OnStateChanged;
        // отписка при уходе со страницы — экземпляр после навигации не нужен в подписчиках
        Unloaded += (_, _) =>
        {
            App.Server.OnStateChanged -= Server_OnStateChanged;
            _pageCts.Cancel();
        };
        App.Settings.AnnouncementTemplates ??= new List<string>();
        App.Settings.LoadModeSnapshot ??= new Dictionary<string, string>();
        App.Settings.WorldModeSnapshot ??= new Dictionary<string, string>();
        LoadAnnouncements();
        RefreshChat();
        _ = RefreshPlayers();
        _ = RefreshModes();
    }

    private void Server_OnStateChanged(bool running) => Dispatcher.Invoke(UpdateRconState);

    private void UpdateRconState()
        => TxtRcon.Text = App.Rcon.IsConnected
            ? $"RCON подключён • ws://{App.Settings.RconHost}:{App.Settings.RconPort}"
            : "RCON не подключён — команды не выполнятся";

    private PlayerInfo? SelectedTarget => CbTarget.SelectedItem as PlayerInfo;

    private async System.Threading.Tasks.Task RefreshPlayers()
    {
        var selectedId = SelectedTarget?.SteamID;
        var destinationId = (CbTeleportDest.SelectedItem as PlayerInfo)?.SteamID;
        var raw = await App.Rcon.SendAsync("playerlist");
        try
        {
            var players = JsonSerializer.Deserialize<PlayerInfo[]>(raw) ?? Array.Empty<PlayerInfo>();
            CbTarget.ItemsSource = players;
            CbTeleportDest.ItemsSource = players;
            CbTarget.SelectedItem = players.FirstOrDefault(p => p.SteamID == selectedId) ?? players.FirstOrDefault();
            CbTeleportDest.SelectedItem = players.FirstOrDefault(p => p.SteamID == destinationId) ?? players.FirstOrDefault();
            TxtTargetHint.Text = players.Length == 0
                ? "онлайн-игроков нет"
                : $"онлайн: {players.Length}; точка определяется прицелом";
        }
        catch
        {
            CbTarget.ItemsSource = Array.Empty<PlayerInfo>();
            CbTeleportDest.ItemsSource = Array.Empty<PlayerInfo>();
            TxtTargetHint.Text = App.Rcon.IsConnected ? "playerlist: неожиданный ответ" : "RCON не подключён";
        }
    }

    private async void RefreshPlayers_Click(object sender, RoutedEventArgs e) => await RefreshPlayers();

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedTarget is { } target)
            TxtTargetHint.Text = $"{target.DisplayName} • {target.SteamID} • точка по прицелу";
    }

    private async void SpawnAtPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        if (SelectedTarget is not { } target)
        {
            TxtResult.Text = "выберите онлайн-игрока в поле ЦЕЛЬ";
            return;
        }

        var entity = button.Tag as string ?? ParamOf(button);
        if (string.IsNullOrWhiteSpace(entity) || entity.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
        {
            TxtResult.Text = "укажите короткое имя сущности: bear, scientist, bradley...";
            return;
        }

        if (MessageBox.Show($"Создать «{entity}» в точке прицела игрока {target.DisplayName}?",
                "OctavianRSM", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run($"octa.spawnat {entity} {target.SteamID}");
    }

    // Значение параметра из контрола, который кнопка несёт в DataContext
    private static string ParamOf(FrameworkElement btn)
        => btn.DataContext switch
        {
            WpfNumberBox n => (n.Value ?? 0).ToString("0.##", CultureInfo.InvariantCulture),
            WpfTextBox t => t.Text.Trim(),
            TextBox t => t.Text.Trim(),
            Slider s => s.Value.ToString("0.##", CultureInfo.InvariantCulture),
            _ => ""
        };

    private async System.Threading.Tasks.Task Run(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return;
        TxtLast.Text = $"> {cmd}";
        var resp = await App.Rcon.SendAsync(cmd);
        var full = string.IsNullOrWhiteSpace(resp) ? "ok" : resp.Trim();
        // полный текст — в раскрываемой панели, в статус-строке только первая строка
        TxtResultFull.Text = full;
        if (full.Contains('\n') || full.Length > 120)
        {
            var lines = full.Split('\n');
            TxtResult.Text = $"{lines[0].Trim()}  … (ещё {lines.Length - 1} строк — жмите ▲)";
        }
        else
            TxtResult.Text = full;
        if (PnlResultFull.Visibility == Visibility.Visible)
            PnlResultFull.UpdateLayout();
    }

    private void RunSimple(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string cmd) _ = Run(cmd);
    }

    private void RunParam(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tpl) return;
        var cmd = tpl.Contains("{0}") ? string.Format(tpl, ParamOf(fe)) : tpl;
        _ = Run(cmd);
    }

    private void RunConfirm(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string cmd) return;
        if (System.Windows.MessageBox.Show($"Выполнить «{cmd}»?", "OctavianRSM",
                System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        // quit гасит процесс — помечаем намеренной остановкой, иначе watchdog перезапустит
        if (cmd == "quit") App.Server.NoteIntentionalStop();
        _ = Run(cmd);
    }

    private void RunParamConfirm(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tpl) return;
        var cmd = tpl.Contains("{0}") ? string.Format(tpl, ParamOf(fe)) : tpl;
        if (System.Windows.MessageBox.Show($"Выполнить «{cmd}»?", "OctavianRSM",
                System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _ = Run(cmd);
    }

    private bool TryGetTarget(out PlayerInfo target)
    {
        target = SelectedTarget!;
        if (target != null) return true;
        TxtResult.Text = "выберите онлайн-игрока в поле ЦЕЛЬ";
        return false;
    }

    private async void TargetCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTarget(out var target) || sender is not FrameworkElement { Tag: string template }) return;
        await Run(string.Format(CultureInfo.InvariantCulture, template, target.SteamID));
    }

    private async void TargetCommandConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTarget(out var target) || sender is not FrameworkElement { Tag: string template }) return;
        var command = string.Format(CultureInfo.InvariantCulture, template, target.SteamID);
        if (MessageBox.Show($"Выполнить для {target.DisplayName}?\n{command}", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run(command);
    }

    private async void TeleportTargetPos_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTarget(out var target)) return;
        var parts = In_TargetPos.Text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            TxtResult.Text = "координаты должны иметь формат: x y z";
            return;
        }
        var command = $"octa.tppos {target.SteamID} {string.Join(" ", parts)}";
        if (MessageBox.Show($"Телепортировать {target.DisplayName} в {string.Join(" ", parts)}?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run(command);
    }

    private async void TeleportTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTarget(out var target) || CbTeleportDest.SelectedItem is not PlayerInfo destination) return;
        if (target.SteamID == destination.SteamID) { TxtResult.Text = "цель и пункт назначения совпадают"; return; }
        if (MessageBox.Show($"Телепортировать {target.DisplayName} к {destination.DisplayName}?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run($"octa.tpto {target.SteamID} {destination.SteamID}");
    }

    private async void AccessIdCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string template }) return;
        var id = In_AccessSteamId.Text.Trim();
        if (!ulong.TryParse(id, out _) || id.Length != 17)
        {
            TxtResult.Text = "укажите корректный SteamID64 из 17 цифр";
            return;
        }
        await Run(string.Format(CultureInfo.InvariantCulture, template, id));
    }

    private async void MaintenanceOn_Click(object sender, RoutedEventArgs e)
    {
        var message = CleanCommandText(In_MaintMessage.Text);
        if (string.IsNullOrWhiteSpace(message)) message = "Технические работы. Попробуйте позже.";
        if (MessageBox.Show($"Закрыть вход для всех, кроме админов и allow-list?\n{message}", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run($"octa.maintenance on {message}");
    }

    private async void GiveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTarget(out var target)) return;
        var item = In_GiveItem.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(item) || item.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
        {
            TxtResult.Text = "некорректный shortname предмета";
            return;
        }
        var amount = Math.Max(1, (int)(In_GiveAmount.Value ?? 1));
        await Run($"inventory.giveto {item} {target.SteamID} {amount}");
    }

    private async void DeployLoadout_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTarget(out var target)) return;
        var name = In_Loadout.Text.Trim();
        if (string.IsNullOrEmpty(name) || name.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-'))
        {
            TxtResult.Text = "имя loadout может содержать буквы, цифры, _ и -";
            return;
        }
        if (MessageBox.Show($"Выдать loadout «{name}» игроку {target.DisplayName}?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run($"inventory.deployloadout {name} {target.SteamID}");
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    private static string CleanCommandText(string value)
        => SingleLine(value).Replace(';', ',').Replace('"', '\'');
    private static string RconQuote(string value) => $"\"{CleanCommandText(value)}\"";

    private async Task<string> ReadConvar(string name)
    {
        var raw = (await App.Rcon.SendAsync(name)).Trim();
        if (raw.Length == 0 || raw.StartsWith('[')) return "";
        var lastQuote = raw.LastIndexOf('"');
        var firstQuote = lastQuote > 0 ? raw.LastIndexOf('"', lastQuote - 1) : -1;
        if (firstQuote >= 0 && lastQuote > firstQuote) return raw[(firstQuote + 1)..lastQuote];
        var colon = raw.LastIndexOf(':');
        return colon >= 0 ? raw[(colon + 1)..].Trim() : raw;
    }

    private async Task<bool> CaptureSnapshot(Dictionary<string, string> snapshot, IEnumerable<string> names)
    {
        var required = names.ToArray();
        if (required.All(snapshot.ContainsKey)) return true;
        snapshot.Clear();
        var captured = new Dictionary<string, string>();
        foreach (var name in required)
        {
            var value = await ReadConvar(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                TxtResult.Text = $"не удалось прочитать {name}; профиль не применён";
                return false;
            }
            captured[name] = value;
        }
        foreach (var pair in captured) snapshot[pair.Key] = pair.Value;
        App.Settings.Save(App.SettingsPath);
        return true;
    }

    private async Task<bool> ApplyConvars(IReadOnlyDictionary<string, string> values)
    {
        var failed = new List<string>();
        foreach (var pair in values)
        {
            var response = await App.Rcon.SendAsync($"{pair.Key} {pair.Value}");
            if (response.StartsWith("[error]", StringComparison.OrdinalIgnoreCase)
                || response.StartsWith("[timeout]", StringComparison.OrdinalIgnoreCase)
                || response.StartsWith("[RCON", StringComparison.OrdinalIgnoreCase)
                || response.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
                || response.Contains("not found", StringComparison.OrdinalIgnoreCase))
                failed.Add(pair.Key);
        }
        TxtLast.Text = $"> применено {values.Count - failed.Count}/{values.Count} convars";
        TxtResult.Text = failed.Count == 0 ? "ok" : "ошибка: " + string.Join(", ", failed);
        return failed.Count == 0;
    }

    private async void ApplyLoadMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode }) return;
        var snapshot = App.Settings.LoadModeSnapshot;
        if (mode == "restore")
        {
            if (snapshot.Count == 0) { LoadModeStatus.Text = "нет сохранённого снимка для восстановления"; return; }
            if (!await ApplyConvars(snapshot)) return;
            snapshot.Clear();
            App.Settings.Save(App.SettingsPath);
            LoadModeStatus.Text = "исходные параметры нагрузки восстановлены";
            await RefreshModes();
            return;
        }

        var title = mode == "emergency" ? "АВАРИЙНЫЙ" : "РАЗГРУЗКА";
        if (MessageBox.Show($"Применить профиль «{title}»? Текущие значения будут сохранены для отката.", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await CaptureSnapshot(snapshot, LoadConvars)) return;
        IReadOnlyDictionary<string, string> values = mode == "emergency"
            ? new Dictionary<string, string>
            {
                ["spawn.min_rate"] = "0.10", ["spawn.max_rate"] = "0.25",
                ["spawn.min_density"] = "0.10", ["spawn.max_density"] = "0.25",
                ["ai.npc_enable"] = "false", ["ai.think"] = "false", ["ai.move"] = "false"
            }
            : new Dictionary<string, string>
            {
                ["spawn.min_rate"] = "0.50", ["spawn.max_rate"] = "1.00",
                ["spawn.min_density"] = "0.50", ["spawn.max_density"] = "1.00",
                ["ai.npc_enable"] = "true", ["ai.think"] = "true", ["ai.move"] = "true"
            };
        if (!await ApplyConvars(values)) return;
        LoadModeStatus.Text = $"активен профиль {title}; доступен откат к снимку";
        await RefreshModes();
    }

    private async void ApplyWorldMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode }) return;
        var snapshot = App.Settings.WorldModeSnapshot;
        Dictionary<string, string> values;
        if (mode == "restore")
        {
            if (snapshot.Count == 0) { WorldModeStatus.Text = "нет сохранённого снимка для восстановления"; return; }
            values = new Dictionary<string, string>(snapshot);
        }
        else
        {
            if (MessageBox.Show($"Применить режим «{mode}»?", "OctavianRSM",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (!await CaptureSnapshot(snapshot, WorldConvars)) return;
            values = mode switch
            {
                "pve:on" => new() { ["server.pve"] = "true" },
                "pve:off" => new() { ["server.pve"] = "false" },
                "decay:off" => new() { ["decay.scale"] = "0" },
                "upkeep:off" => new() { ["decay.upkeep"] = "false" },
                _ => new()
            };
        }
        if (values.Count == 0) return;
        if (!await ApplyConvars(values)) return;
        if (ChkPersistWorld.IsChecked == true)
        {
            App.ServerCfg.Load();
            foreach (var pair in values) App.ServerCfg[pair.Key] = pair.Value;
            App.ServerCfg.Save();
        }
        if (mode == "restore")
        {
            snapshot.Clear();
            App.Settings.Save(App.SettingsPath);
        }
        await RefreshModes();
    }

    private async void RefreshModes_Click(object sender, RoutedEventArgs e) => await RefreshModes();

    private async Task RefreshModes()
    {
        if (!App.Rcon.IsConnected)
        {
            LoadModeStatus.Text = "RCON не подключён";
            WorldModeStatus.Text = "RCON не подключён";
            return;
        }
        var names = LoadConvars.Concat(WorldConvars).ToArray();
        var tasks = names.Select(async name => (name, value: await ReadConvar(name))).ToArray();
        var values = (await Task.WhenAll(tasks)).ToDictionary(item => item.name, item => item.value);
        LoadModeStatus.Text = string.Join(" • ", LoadConvars.Select(name => $"{name}={values[name]}"));
        WorldModeStatus.Text = string.Join(" • ", WorldConvars.Select(name => $"{name}={values[name]}"))
            + (ChkPersistWorld.IsChecked == true ? " • запись в cfg включена" : " • только runtime");
    }

    private void LoadAnnouncements()
    {
        CbAnnTemplates.ItemsSource = null;
        CbAnnTemplates.ItemsSource = App.Settings.AnnouncementTemplates;
        if (App.Settings.AnnouncementTemplates.Count > 0) CbAnnTemplates.SelectedIndex = 0;
        In_AnnScheduleText.Text = App.Settings.ScheduledAnnouncementText;
        In_AnnMinutes.Value = Math.Max(1, App.Settings.ScheduledAnnouncementIntervalMinutes);
        ChkAnnEnabled.IsChecked = App.Settings.ScheduledAnnouncementEnabled;
        AnnScheduleStatus.Text = App.Settings.ScheduledAnnouncementEnabled
            ? $"включено: каждые {App.Settings.ScheduledAnnouncementIntervalMinutes} мин"
            : "повтор отключён";
    }

    private void AnnouncementTemplate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CbAnnTemplates.SelectedItem is not string text) return;
        In_AnnouncementNow.Text = text;
        In_AnnScheduleText.Text = text;
    }

    private async void SendAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        var text = CleanCommandText(In_AnnouncementNow.Text);
        if (string.IsNullOrWhiteSpace(text)) { TxtResult.Text = "введите текст объявления"; return; }
        await Run($"say {RconQuote(text)}");
    }

    private async void SendTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (CbAnnTemplates.SelectedItem is not string text) { TxtResult.Text = "выберите шаблон"; return; }
        await Run($"say {RconQuote(text)}");
    }

    private void AddAnnouncementTemplate_Click(object sender, RoutedEventArgs e)
    {
        var text = SingleLine(In_NewTemplate.Text);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!App.Settings.AnnouncementTemplates.Contains(text, StringComparer.OrdinalIgnoreCase))
            App.Settings.AnnouncementTemplates.Add(text);
        App.Settings.Save(App.SettingsPath);
        In_NewTemplate.Text = "";
        LoadAnnouncements();
    }

    private void DeleteAnnouncementTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (CbAnnTemplates.SelectedItem is not string text) return;
        App.Settings.AnnouncementTemplates.Remove(text);
        App.Settings.Save(App.SettingsPath);
        LoadAnnouncements();
    }

    private void SaveAnnouncementSchedule_Click(object sender, RoutedEventArgs e)
    {
        var text = SingleLine(In_AnnScheduleText.Text);
        if (ChkAnnEnabled.IsChecked == true && string.IsNullOrWhiteSpace(text))
        {
            AnnScheduleStatus.Text = "для включения укажите текст";
            return;
        }
        App.Settings.ScheduledAnnouncementText = text;
        App.Settings.ScheduledAnnouncementIntervalMinutes = Math.Max(1, (int)(In_AnnMinutes.Value ?? 30));
        App.Settings.ScheduledAnnouncementEnabled = ChkAnnEnabled.IsChecked == true;
        App.Settings.Save(App.SettingsPath);
        App.Watchdog.ResetAnnouncementTimer();
        AnnScheduleStatus.Text = App.Settings.ScheduledAnnouncementEnabled
            ? $"включено: каждые {App.Settings.ScheduledAnnouncementIntervalMinutes} мин"
            : "повтор отключён";
    }

    private void RefreshChat_Click(object sender, RoutedEventArgs e) => RefreshChat();
    private void ChatSearch_Changed(object sender, TextChangedEventArgs e) => RefreshChat();

    private void RefreshChat()
    {
        var query = In_ChatSearch.Text.Trim();
        var rows = App.Db.RecentLogs(2000)
            .Where(row => row.type.Contains("Chat", StringComparison.OrdinalIgnoreCase)
                       || row.msg.Contains("[CHAT", StringComparison.OrdinalIgnoreCase))
            .Where(row => query.Length == 0 || row.msg.Contains(query, StringComparison.OrdinalIgnoreCase))
            .TakeLast(300)
            .Select(row => $"[{row.ts}] {row.msg}")
            .ToArray();
        ChatHistory.ItemsSource = rows;
        if (rows.Length > 0) ChatHistory.ScrollIntoView(rows[^1]);
    }

    private void SldTime_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        => TxtTimeVal.Text = SldTime.Value.ToString("0.0", CultureInfo.InvariantCulture);

    private void Weather_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider s && s.Tag is string txtName
            && FindName(txtName) is TextBlock t)
            t.Text = s.Value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    // одна кнопка на все шансы динамической погоды — шлём 6 convar'ов подряд
    private async void ApplyWeather_Click(object sender, RoutedEventArgs e)
    {
        string F(Slider s) => s.Value.ToString("0.00", CultureInfo.InvariantCulture);
        await Run($"weather.clear_chance {F(SldCClear)}");
        await Run($"weather.fog_chance {F(SldCFog)}");
        await Run($"weather.dust_chance {F(SldCDust)}");
        await Run($"weather.overcast_chance {F(SldCOver)}");
        await Run($"weather.rain_chance {F(SldCRain)}");
        await Run($"weather.storm_chance {F(SldCStorm)}");
        TxtLast.Text = "> weather.*_chance ×6";
    }

    // мгновенные интенсивности явлений (Weather 2.0)
    private async void ApplyAtmos_Click(object sender, RoutedEventArgs e)
    {
        string F(Slider s) => s.Value.ToString("0.00", CultureInfo.InvariantCulture);
        await Run($"weather.rain {F(SldRain)}");
        await Run($"weather.fog {F(SldFog)}");
        await Run($"weather.wind {F(SldWind)}");
        await Run($"weather.thunder {F(SldStorm)}");
        TxtLast.Text = "> weather.rain/fog/wind/thunder";
    }

    // плотность и скорость спавна популяций мира
    private async void SpawnScale_Click(object sender, RoutedEventArgs e)
    {
        string F(Slider s) => s.Value.ToString("0.0", CultureInfo.InvariantCulture);
        await Run($"spawn.min_rate {F(SldSpMinRate)}");
        await Run($"spawn.max_rate {F(SldSpMaxRate)}");
        await Run($"spawn.min_density {F(SldSpMinDen)}");
        await Run($"spawn.max_density {F(SldSpMaxDen)}");
        TxtLast.Text = "> spawn.* rate/density";
    }

    // разворачивание панели полного ответа
    private void ExpandResult_Click(object sender, RoutedEventArgs e)
    {
        var open = PnlResultFull.Visibility != Visibility.Visible;
        PnlResultFull.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BtnExpandResult.Content = open ? "▼" : "▲";
        BtnExpandResult.ToolTip = open ? "Свернуть" : "Развернуть полный ответ";
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtResultFull.Text))
            Clipboard.SetText(TxtResultFull.Text);
        TxtLast.Text = "ответ скопирован в буфер";
    }

    private void ClearResult_Click(object sender, RoutedEventArgs e)
    {
        TxtResultFull.Text = "";
        TxtResult.Text = "";
    }
}
