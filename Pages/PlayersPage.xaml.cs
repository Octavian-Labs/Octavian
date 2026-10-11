using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OctavianRSM.Models;
using OctavianRSM.Services;

namespace OctavianRSM.Pages;

public partial class PlayersPage : Page
{
    private PlayerInfo[] _all = Array.Empty<PlayerInfo>();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(15) };

    public PlayersPage()
    {
        InitializeComponent();
        Grid.SelectionChanged += (_, _) =>
        {
            if (Grid.SelectedItem != null) GridRoster.UnselectAll();
            UpdateTargetLabel();
        };
        GridRoster.SelectionChanged += (_, _) =>
        {
            if (GridRoster.SelectedItem != null) Grid.UnselectAll();
            UpdateTargetLabel();
        };
        // периодический опрос пока страница открыта — копится время на сервере,
        // обновляются страны и ростер
        _poll.Tick += (_, _) => _ = Refresh();
        Loaded += (_, _) => { _poll.Start(); ApplyContextTarget(); };
        Unloaded += (_, _) => _poll.Stop();
        _ = Refresh();
    }

    private PlayerInfo? Selected => Grid.SelectedItem as PlayerInfo;
    private KnownPlayer? SelectedRoster => GridRoster.SelectedItem as KnownPlayer;

    // Текущая цель действий: приоритет — выбранному онлайн-игроку,
    // иначе берём запись из базы (бан/права работают и с оффлайном)
    private (string id, string name, bool online)? Target =>
        Selected is { } p ? (p.SteamID, p.DisplayName, true) :
        SelectedRoster is { } r ? (r.SteamId, r.Name, false) : null;

    private void UpdateTargetLabel()
    {
        var t = Target;
        TxtSelected.Text = t is { } tt ? $"цель: {tt.name} {(tt.online ? "(онлайн)" : "(оффлайн)")}" : "";
        TxtActTarget.Text = t is { } ta
            ? $"цель: {ta.name} ({ta.id}) {(ta.online ? "— онлайн" : "— оффлайн")}"
            : "цель: не выбрана — выберите игрока во вкладке «Онлайн» или «База»";
        // единая модель цели: контекстная панель оболочки видит того же игрока
        App.Ctx.SelectPlayer(t is { } tp ? new PlayerRef(tp.id, tp.name, tp.online) : null);
    }

    /// <summary>Прийти сюда с уже выбранной целью (например, из панели контекста).</summary>
    private void ApplyContextTarget()
    {
        var want = App.Ctx.SelectedPlayer;
        if (want == null || Target?.id == want.SteamId) return;
        var online = Grid.ItemsSource as IEnumerable<PlayerInfo>;
        var hit = online?.FirstOrDefault(p => p.SteamID == want.SteamId);
        if (hit != null) { Grid.SelectedItem = hit; Grid.ScrollIntoView(hit); return; }
        var roster = GridRoster.ItemsSource as IEnumerable<KnownPlayer>;
        var rhit = roster?.FirstOrDefault(r => r.SteamId == want.SteamId);
        if (rhit != null) { GridRoster.SelectedItem = rhit; GridRoster.ScrollIntoView(rhit); }
    }

    private void ApplyFilter()
    {
        var q = TxtSearch.Text.Trim();
        var items = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(p => p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || p.SteamID.Contains(q)
                              || p.Address.Contains(q)).ToArray();
        Grid.ItemsSource = items;
        TxtCount.Text = _all.Length == 0
            ? "никого нет онлайн"
            : $"онлайн: {items.Length} из {_all.Length}";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async System.Threading.Tasks.Task Refresh()
    {
        var raw = await App.Rcon.SendAsync("playerlist");
        try
        {
            _all = JsonSerializer.Deserialize<PlayerInfo[]>(raw) ?? Array.Empty<PlayerInfo>();
            var db = App.Db.GetPlayers().ToDictionary(x => x.SteamId);
            foreach (var p in _all)
            {
                if (db.TryGetValue(p.SteamID, out var k))
                {
                    p.CountryCode = k.CountryCode;
                    p.CountryName = k.CountryName;
                    p.TotalSecondsBase = k.TotalSeconds;
                }
                App.Db.UpsertPlayer(p.SteamID, p.DisplayName, p.Ip, p.ConnectedSeconds);
            }
            App.Db.FlushOfflineSessions();
            CmbTpTarget.ItemsSource = _all;
            ApplyFilter();
            ApplyContextTarget();
            _ = ResolveCountries();
            RefreshRoster();
        }
        catch
        {
            TxtCount.Text = App.Rcon.IsConnected ? "playerlist: неожиданный ответ" : "RCON не подключён";
        }
    }

    // Геолокация: резолвим IP игроков без страны (онлайн + хвост ростера)
    private async System.Threading.Tasks.Task ResolveCountries()
    {
        var pending = _all.Where(p => string.IsNullOrEmpty(p.CountryCode))
            .Select(p => (id: p.SteamID, ip: p.Ip))
            .Concat((GridRoster.ItemsSource as List<KnownPlayer> ?? App.Db.GetPlayers())
                .Where(r => string.IsNullOrEmpty(r.CountryCode) && !string.IsNullOrEmpty(r.Ip))
                .Take(20)
                .Select(r => (id: r.SteamId, ip: r.Ip)))
            .GroupBy(x => x.id).Select(g => g.First()).ToArray();
        if (pending.Length == 0) return;

        var resolved = false;
        foreach (var (id, ip) in pending)
        {
            var hit = await App.Geo.LookupAsync(ip);
            if (hit is { } geo)
            {
                App.Db.SetCountry(id, geo.code, geo.name);
                resolved = true;
            }
        }
        if (!resolved) return;

        var db = App.Db.GetPlayers().ToDictionary(x => x.SteamId);
        foreach (var p in _all)
            if (db.TryGetValue(p.SteamID, out var k) && string.IsNullOrEmpty(p.CountryCode))
            {
                p.CountryCode = k.CountryCode;
                p.CountryName = k.CountryName;
            }
        ApplyFilter();
        RefreshRoster();
    }

    private void RefreshRoster()
    {
        var all = App.Db.GetPlayers();
        GridRoster.ItemsSource = all;
        TxtRoster.Text = $"в базе: {all.Count} игроков";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshRoster();
        _ = Refresh();
    }

    private string Reason(string fallback) =>
        string.IsNullOrWhiteSpace(TxtReason.Text) ? fallback : TxtReason.Text.Trim();

    private bool NeedOnline((string id, string name, bool online)? t)
    {
        if (t is { online: true }) return true;
        MessageBox.Show(t == null ? "Выберите игрока." : $"{t!.Value.name} сейчас оффлайн — действие только для онлайн.",
            "OctavianRSM", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private async void Kick_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedOnline(Target)) return;
        var t = Target!.Value;
        if (MessageBox.Show($"Кикнуть {t.name}?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await App.Rcon.SendAsync($"kick {t.id} \"{Esc(Reason("Kicked by admin"))}\"");
        await Refresh();
    }

    private async void Ban_Click(object sender, RoutedEventArgs e)
    {
        if (Target is not { } t) return;
        if (MessageBox.Show($"ЗАБАНИТЬ {t.name} ({t.id})?\nПостоянный бан по SteamID.", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var reason = Reason("Banned by admin");
        await App.Rcon.SendAsync($"banid {t.id} \"{Esc(t.name)}\" \"{Esc(reason)}\"");
        if (t.online) await App.Rcon.FireAsync($"kick {t.id} \"{Esc(reason)}\"");
        await Refresh();
    }

    private async void Unban_Click(object sender, RoutedEventArgs e)
    {
        if (Target is not { } t) return;
        if (MessageBox.Show($"Снять бан с {t.name} ({t.id})?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await App.Rcon.SendAsync($"unban {t.id}");
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedOnline(Target)) return;
        var t = Target!.Value;
        var seconds = (CmbMute.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "600";
        await App.Rcon.SendAsync($"mute {t.id} {seconds}");
        await Refresh();
    }

    private async void MuteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedOnline(Target)) return;
        var t = Target!.Value;
        await App.Rcon.SendAsync($"mute {t.id} 600");
        await Refresh();
    }

    private async void Unmute_Click(object sender, RoutedEventArgs e)
    {
        if (Target is not { } t) return;
        await App.Rcon.SendAsync($"unmute {t.id}");
    }

    private async void Owner_Click(object sender, RoutedEventArgs e)
    {
        if (Target is not { } t) return;
        if (MessageBox.Show($"Выдать {t.name} права OWNER (полный доступ)?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await App.Rcon.SendAsync($"ownerid {t.id} \"{Esc(t.name)}\" \"{Esc(Reason("granted via OctavianRSM"))}\"");
        await App.Rcon.FireAsync("server.writecfg");
    }

    private async void Mod_Click(object sender, RoutedEventArgs e)
    {
        if (Target is not { } t) return;
        if (MessageBox.Show($"Выдать {t.name} права модератора?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await App.Rcon.SendAsync($"moderatorid {t.id} \"{Esc(t.name)}\" \"{Esc(Reason("granted via OctavianRSM"))}\"");
        await App.Rcon.FireAsync("server.writecfg");
    }

    private async void ClearRights_Click(object sender, RoutedEventArgs e)
    {
        if (Target is not { } t) return;
        if (MessageBox.Show($"Снять с {t.name} права owner/moderator?", "OctavianRSM",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await App.Rcon.FireAsync($"removeowner {t.id}");
        await App.Rcon.FireAsync($"removemoderator {t.id}");
        await App.Rcon.FireAsync("server.writecfg");
    }

    private async void Teleport_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedOnline(Target)) return;
        var t = Target!.Value;
        if (CmbTpTarget.SelectedItem is not PlayerInfo dest)
        {
            MessageBox.Show("Выберите игрока-цель телепорта в списке справа.", "OctavianRSM",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await App.Rcon.SendAsync($"teleport \"{t.id}\" \"{dest.SteamID}\"");
    }

    private async void Stats_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedOnline(Target)) return;
        var t = Target!.Value;
        var raw = await App.Rcon.SendAsync($"stats {t.id}");
        new InfoWindow($"Статистика — {t.name}", PrettyStats(raw),
            Window.GetWindow(this)).Show();
    }

    private async void CombatLog_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedOnline(Target)) return;
        var t = Target!.Value;
        var raw = await App.Rcon.SendAsync($"combatlog {t.id}");
        new InfoWindow($"Лог боя — {t.name}", raw.Trim(),
            Window.GetWindow(this)).Show();
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (Target is { } t) Clipboard.SetText(t.id);
    }

    // ── Вкладка «Действия»: адресные команды OctaAdminTools/Rust ────────
    // Ответы идут в единый канал ServerContext → CommandResultBar + панель контекста.

    private bool TryTarget(bool needOnline)
    {
        if (Target is not { } t) { ResultBar.StateText = "цель не выбрана"; return false; }
        if (needOnline && !t.online)
        {
            ResultBar.StateText = $"{t.name} оффлайн — действие только для онлайн";
            return false;
        }
        return true;
    }

    private async void PlayerCmd_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(needOnline: true) || sender is not FrameworkElement { Tag: string tpl }) return;
        await App.Ctx.RunRconAsync(string.Format(CultureInfo.InvariantCulture, tpl, Target!.Value.id));
    }

    private async void PlayerCmdConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(needOnline: true) || sender is not FrameworkElement { Tag: string tpl }) return;
        var t = Target!.Value;
        var cmd = string.Format(CultureInfo.InvariantCulture, tpl, t.id);
        if (!UiConfirm.Ask(Window.GetWindow(this), "Выполнить команду", cmd,
                $"цель: {t.name} ({t.id})", danger: true)) return;
        await App.Ctx.RunRconAsync(cmd);
    }

    private async void Cmd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cmd }) await App.Ctx.RunRconAsync(cmd);
    }

    private async void CmdConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string cmd }) return;
        if (!UiConfirm.Ask(Window.GetWindow(this), "Выполнить команду", cmd,
                "действует на весь сервер", danger: true)) return;
        await App.Ctx.RunRconAsync(cmd);
    }

    private async void TpPos_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(needOnline: true)) return;
        var t = Target!.Value;
        var parts = In_TpPos.Text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            ResultBar.StateText = "координаты должны иметь формат: x y z";
            return;
        }
        if (!UiConfirm.Ask(Window.GetWindow(this), "Телепортировать игрока",
                $"{t.name} → {string.Join(" ", parts)}", "мгновенный телепорт")) return;
        await App.Ctx.RunRconAsync($"octa.tppos {t.id} {string.Join(" ", parts)}");
    }

    private async void AccessId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tpl }) return;
        var id = In_AccessId.Text.Trim();
        if (!ulong.TryParse(id, out _) || id.Length != 17)
        {
            ResultBar.StateText = "укажите корректный SteamID64 из 17 цифр";
            return;
        }
        await App.Ctx.RunRconAsync(string.Format(CultureInfo.InvariantCulture, tpl, id));
    }

    private async void MaintenanceOn_Click(object sender, RoutedEventArgs e)
    {
        var message = In_MaintMsg.Text.Replace('\r', ' ').Replace('\n', ' ')
            .Replace(';', ',').Replace('"', '\'').Trim();
        if (string.IsNullOrWhiteSpace(message)) message = "Технические работы. Попробуйте позже.";
        if (!UiConfirm.Ask(Window.GetWindow(this), "Включить режим техработ",
                message, "вход закрыт для всех, кроме админов и allow-list", danger: true)) return;
        await App.Ctx.RunRconAsync($"octa.maintenance on {message}");
    }

    private async void GiveItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(needOnline: true)) return;
        var t = Target!.Value;
        var item = In_GiveItem.Text.Trim().ToLowerInvariant();
        if (item.Length == 0 || item.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
        {
            ResultBar.StateText = "некорректный shortname предмета";
            return;
        }
        var amount = Math.Max(1, (int)(In_GiveAmount.Value ?? 1));
        await App.Ctx.RunRconAsync($"inventory.giveto {item} {t.id} {amount}");
    }

    private async void Loadout_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTarget(needOnline: true)) return;
        var t = Target!.Value;
        var name = In_LoadoutName.Text.Trim();
        if (name.Length == 0 || name.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-'))
        {
            ResultBar.StateText = "имя loadout может содержать буквы, цифры, _ и -";
            return;
        }
        if (!UiConfirm.Ask(Window.GetWindow(this), "Выдать loadout",
                $"«{name}» → {t.name}", "заменит текущий инвентарь игрока")) return;
        await App.Ctx.RunRconAsync($"inventory.deployloadout {name} {t.id}");
    }

    // stats возвращает JSON — разворачиваем в читаемый список ключ: значение
    private static string PrettyStats(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var sb = new StringBuilder();
            foreach (var p in doc.RootElement.EnumerateObject())
                sb.AppendLine($"{p.Name,-22} {p.Value}");
            return sb.ToString();
        }
        catch { return raw; }
    }
}
