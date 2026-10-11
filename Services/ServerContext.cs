using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

/// <summary>
/// Единая модель состояния приложения: режим (Local/Remote), процесс сервера,
/// RCON, выбранный игрок, текущая длительная операция и черновики страниц.
///
/// Правило: страницы НЕ читают App.Settings.IsRemoteMode / App.Server.IsRunning
/// напрямую для гейтинга — только через Ctx.Capabilities, чтобы недоступные
/// операции нельзя было показать активными.
/// </summary>
public sealed class ServerContext : INotifyPropertyChanged
{
    private readonly RsmSettings _settings;
    private readonly ServerManager _server;
    private readonly RconService _rcon;

    public ServerContext(RsmSettings settings, ServerManager server, RconService rcon)
    {
        _settings = settings;
        _server = server;
        _rcon = rcon;
        _server.OnStateChanged += _ => Refresh();
        _rcon.OnConnected += Refresh;
        _rcon.OnDisconnected += Refresh;
        // процесс мог уже существовать до менеджера или умереть без события —
        // лёгкий периодический ресинк дёшево держит модель честной
        _ = ResyncLoop();
    }

    // ── Сырьевые состояния ──────────────────────────────────────────────

    private bool _serverRunning;
    private bool _rconConnected;
    private bool _isRemote;
    private bool _serverExeExists;
    private string? _operation;
    private bool _busy;

    public bool IsRemote => _isRemote;
    public bool IsLocal => !_isRemote;
    public bool ServerRunning => _serverRunning;
    public bool RconConnected => _rconConnected;
    public bool ServerExeExists => _serverExeExists;

    /// <summary>Текущая длительная операция (установка, вайп, обновление) — null = свободно.</summary>
    public string? Operation => _operation;
    public bool Busy => _busy;

    // ── Возможности (capabilities) ──────────────────────────────────────

    /// <summary>Управление процессом сервера (start/stop/kill/restart).</summary>
    public bool CanManageProcess => IsLocal && _serverExeExists;
    /// <summary>Локальные файлы сервера: конфиги, плагины, логи, вайп, карты.</summary>
    public bool CanEditFiles => IsLocal;
    /// <summary>Любая операция через RCON — только при живом подключении.</summary>
    public bool CanUseRcon => _rconConnected;
    /// <summary>Параметры, применяемые при запуске (карта, сеть, launch args) — сервер должен стоять.</summary>
    public bool CanEditLaunchParams => IsLocal && !_serverRunning;
    /// <summary>Ручной вайп — только Local и только на остановленном сервере.</summary>
    public bool CanWipe => IsLocal && !_serverRunning;
    /// <summary>Список/переключение/удаление плагинов — файловая операция.</summary>
    public bool CanManagePluginFiles => IsLocal;
    /// <summary>Скачанное из магазина можно ставить только на локальный сервер.</summary>
    public bool CanInstallFromStore => IsLocal;
    /// <summary>SteamCMD/Carbon — только Local.</summary>
    public bool CanInstall => IsLocal;

    /// <summary>Баланс по RCON без записи файла — действует только до рестарта.</summary>
    public bool CanApplyVolatile => _rconConnected;

    // ── Выбранный игрок (единственная модель выбора вместо 3 селекторов) ─

    private PlayerRef? _selectedPlayer;
    public PlayerRef? SelectedPlayer
    {
        get => _selectedPlayer;
        private set { _selectedPlayer = value; OnChanged(); }
    }
    public bool HasTarget => _selectedPlayer != null;
    public void SelectPlayer(PlayerRef? player) => SelectedPlayer = player;

    // ── Результат последней команды (единый канал для панели результата) ─

    public event Action<string, string>? CommandResultPosted; // (команда, ответ)

    /// <summary>
    /// Выполнить команду через RCON с честным гейтингом: без подключения
    /// команда не уходит и не ретраится — возвращается понятный отказ.
    /// </summary>
    public async Task<string> RunRconAsync(string command, int timeoutMs = 8000)
    {
        if (!_rconConnected)
        {
            const string offline = "Нет подключения к RCON — команда не отправлена";
            CommandResultPosted?.Invoke(command, offline);
            return offline;
        }
        var result = await _rcon.SendAsync(command, timeoutMs);
        CommandResultPosted?.Invoke(command, result);
        return result;
    }

    public void FireRcon(string command)
    {
        if (!_rconConnected)
        {
            CommandResultPosted?.Invoke(command, "Нет подключения к RCON — команда не отправлена");
            return;
        }
        _ = _rcon.FireAsync(command);
        CommandResultPosted?.Invoke(command, "(отправлено без ожидания ответа)");
    }

    // ── Длительные операции ─────────────────────────────────────────────

    /// <summary>Открыть операцию; вернуть освободитель. Параллельную операцию запрещаем.</summary>
    public IDisposable BeginOperation(string name)
    {
        if (_busy)
            throw new InvalidOperationException($"Уже выполняется: {_operation}");
        _busy = true;
        _operation = name;
        OnChanged();
        return new OperationScope(this);
    }

    public bool TryBeginOperation(string name, out IDisposable? scope)
    {
        scope = null;
        if (_busy) return false;
        scope = BeginOperation(name);
        return true;
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly ServerContext _ctx;
        private bool _done;
        public OperationScope(ServerContext ctx) => _ctx = ctx;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _ctx._busy = false;
            _ctx._operation = null;
            _ctx.OnChanged();
        }
    }

    // ── Черновики страниц ───────────────────────────────────────────────

    private readonly List<IDirtyPage> _drafts = new();

    public void RegisterDraft(IDirtyPage page) { if (!_drafts.Contains(page)) _drafts.Add(page); }
    public void UnregisterDraft(IDirtyPage page) => _drafts.Remove(page);
    public bool HasDirtyDrafts => _drafts.Any(d => d.IsDirty);

    /// <summary>
    /// Спросить пользователя при уходе с грязной страницы:
    /// Да = сохранить и уйти, Нет = оставить черновик, Отмена = остаться.
    /// </summary>
    public bool ConfirmLeaveDirty(Window? owner)
    {
        var dirty = _drafts.FirstOrDefault(d => d.IsDirty);
        if (dirty == null) return true;
        var res = MessageBox.Show(owner,
            $"«{dirty.DraftTitle}» содержит несохранённые изменения.\n\n" +
            "Да — сохранить и продолжить\n" +
            "Нет — уйти, черновик останется на странице\n" +
            "Отмена — остаться",
            "OctavianRSM — несохранённые изменения",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (res == MessageBoxResult.Cancel) return false;
        if (res == MessageBoxResult.Yes) dirty.SaveDraft();
        return true;
    }

    // ── Обновление состояния ────────────────────────────────────────────

    /// <summary>Перечитать все источники (после смены настроек/режима).</summary>
    public void Refresh()
    {
        _isRemote = _settings.IsRemoteMode;
        _serverRunning = !_isRemote && _server.IsRunning;
        _serverExeExists = File.Exists(_settings.ServerExePath);
        _rconConnected = _rcon.IsConnected;
        OnChanged();
    }

    private async Task ResyncLoop()
    {
        while (true)
        {
            await Task.Delay(5000);
            if (Application.Current?.Dispatcher.HasShutdownStarted != false) return;
            var running = !_settings.IsRemoteMode && _server.IsRunning;
            var rcon = _rcon.IsConnected;
            var remote = _settings.IsRemoteMode;
            var exe = File.Exists(_settings.ServerExePath);
            if (running != _serverRunning || rcon != _rconConnected
                || remote != _isRemote || exe != _serverExeExists)
            {
                _serverRunning = running; _rconConnected = rcon;
                _isRemote = remote; _serverExeExists = exe;
                Application.Current.Dispatcher.Invoke(OnChanged);
            }
        }
    }

    // ── INotifyPropertyChanged + зонтичное событие для императивных подписчиков ─

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    private void OnChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke();
    }
}

/// <summary>Ссылка на игрока для адресных действий (единая на все страницы).</summary>
public sealed record PlayerRef(string SteamId, string DisplayName, bool Online)
{
    public override string ToString() => DisplayName;
}

/// <summary>Страница с несохранённым черновиком — регистрируется в ServerContext.</summary>
public interface IDirtyPage
{
    bool IsDirty { get; }
    string DraftTitle { get; }
    void SaveDraft();
}
