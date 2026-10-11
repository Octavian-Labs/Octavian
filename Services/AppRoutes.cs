using System;
using System.Collections.Generic;
using OctavianRSM.Pages;

namespace OctavianRSM.Services;

/// <summary>
/// Реестр маршрутов оболочки. Каждый маршрут знает свою группу, иконку,
/// целевую страницу и требуемую возможность (capability): маршруты,
/// недоступные в текущем режиме, скрываются из меню целиком.
/// </summary>
public sealed class AppRoute
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required Type PageType { get; set; }
    /// <summary>Параметр для страницы (наследие табов: открыть конкретную вкладку).</summary>
    public string? Param { get; set; }
    /// <summary>Нужная возможность; null = всегда виден.</summary>
    public Func<ServerContext, bool>? Requires { get; init; }
    /// <summary>Локальная страница, частично работающая в Remote — видна, гейтинг внутри.</summary>
    public bool RemotePartial { get; init; }
}

public static class AppRoutes
{
    public sealed record Group(string Title, IReadOnlyList<AppRoute> Routes);

    /// <summary>Порядок меню: группы и маршруты внутри.</summary>
    public static readonly IReadOnlyList<Group> Groups = new[]
    {
        new Group("ГЛАВНАЯ", new[]
        {
            new AppRoute { Id = "dashboard", Title = "Обзор", Icon = "Home24",
                PageType = typeof(DashboardPage), Param = "Обзор" },
        }),
        new Group("СЕРВЕР", new[]
        {
            new AppRoute { Id = "server.face", Title = "Лицо сервера", Icon = "Tag24",
                PageType = typeof(DashboardPage), Param = "Лицо" },
            new AppRoute { Id = "server.params", Title = "Параметры", Icon = "TextBoxSettings24",
                PageType = typeof(ServerCfgPage), Param = "Лимиты",
                Requires = c => c.CanEditFiles },
            new AppRoute { Id = "server.files", Title = "Файлы", Icon = "DocumentText24",
                PageType = typeof(ServerCfgPage), Param = "Файл",
                Requires = c => c.CanEditFiles },
            new AppRoute { Id = "server.install", Title = "Установка", Icon = "ArrowDownload24",
                PageType = typeof(InstallPage),
                Requires = c => c.CanInstall },
            new AppRoute { Id = "server.wipe", Title = "Вайпы", Icon = "ArrowClockwise24",
                PageType = typeof(WipePage),
                Requires = c => c.CanEditFiles },
            new AppRoute { Id = "server.automation", Title = "Автоматизация", Icon = "ClockAlarm24",
                PageType = typeof(SettingsPage), Param = "Автоматика",
                Requires = c => c.CanEditFiles },
        }),
        new Group("ИГРА", new[]
        {
            new AppRoute { Id = "players", Title = "Игроки", Icon = "People24",
                PageType = typeof(PlayersPage) },
            new AppRoute { Id = "world", Title = "Мир", Icon = "WeatherPartlyCloudyDay24",
                PageType = typeof(CommandsPage), Param = "Время",
                Requires = c => c.CanUseRcon || c.CanEditFiles, RemotePartial = true },
            new AppRoute { Id = "events", Title = "Ивенты и спавн", Icon = "Sparkle24",
                PageType = typeof(CommandsPage), Param = "Ивенты",
                Requires = c => c.CanUseRcon || c.CanEditFiles, RemotePartial = true },
            new AppRoute { Id = "deepsea", Title = "Deep Sea", Icon = "VehicleShip24",
                PageType = typeof(CommandsPage), Param = "DeepSea",
                Requires = c => c.CanUseRcon || c.CanEditFiles, RemotePartial = true },
            new AppRoute { Id = "modes", Title = "Режимы", Icon = "ToggleMultiple24",
                PageType = typeof(CommandsPage), Param = "Режимы",
                Requires = c => c.CanUseRcon || c.CanEditFiles, RemotePartial = true },
            new AppRoute { Id = "balance", Title = "Баланс", Icon = "DataHistogram24",
                PageType = typeof(RatesPage),
                Requires = c => c.CanEditFiles || c.CanUseRcon, RemotePartial = true },
            new AppRoute { Id = "chat", Title = "Чат и объявления", Icon = "Chat24",
                PageType = typeof(CommandsPage), Param = "Чат",
                Requires = c => c.CanUseRcon || c.CanEditFiles, RemotePartial = true },
        }),
        new Group("ИНСТРУМЕНТЫ", new[]
        {
            new AppRoute { Id = "plugins", Title = "Плагины", Icon = "PuzzlePiece24",
                PageType = typeof(PluginsPage),
                Requires = c => c.CanEditFiles },
            new AppRoute { Id = "store", Title = "Магазин", Icon = "Globe24",
                PageType = typeof(StorePage) },
            new AppRoute { Id = "console", Title = "Консоль", Icon = "Code24",
                PageType = typeof(ConsolePage) },
            new AppRoute { Id = "logs", Title = "Логи", Icon = "DocumentBulletList24",
                PageType = typeof(LogsPage),
                Requires = c => c.CanEditFiles },
        }),
        new Group("ПРИЛОЖЕНИЕ", new[]
        {
            new AppRoute { Id = "connection", Title = "Подключение", Icon = "PlugConnected24",
                PageType = typeof(ConnectionPage) },
            new AppRoute { Id = "settings", Title = "Настройки", Icon = "Settings24",
                PageType = typeof(AppSettingsPage) },
        }),
    };

    private static readonly Dictionary<string, AppRoute> _byId = BuildIndex();

    private static Dictionary<string, AppRoute> BuildIndex()
    {
        var d = new Dictionary<string, AppRoute>();
        foreach (var g in Groups)
            foreach (var r in g.Routes)
                d[r.Id] = r;
        return d;
    }

    public static AppRoute? Find(string id) => _byId.TryGetValue(id, out var r) ? r : null;

    /// <summary>Первый маршрут с этим типом страницы (фолбэк, если пункт меню не опознан).</summary>
    public static AppRoute? FindByType(Type? pageType)
    {
        if (pageType == null) return null;
        foreach (var g in Groups)
            foreach (var r in g.Routes)
                if (r.PageType == pageType) return r;
        return null;
    }
}
