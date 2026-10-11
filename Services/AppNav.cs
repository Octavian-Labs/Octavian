using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OctavianRSM.Services;

/// <summary>
/// Параметр перехода между страницами: перед NavView.Navigate кладём
/// маршрут/вкладку, страница читает их в Loaded и выбирает нужную секцию.
/// </summary>
public static class AppNav
{
    /// <summary>Id маршрута, по которому открыта текущая страница.</summary>
    public static string? CurrentRouteId { get; set; }

    /// <summary>Параметр маршрута (имя вкладки/секции) — одноразовый.</summary>
    public static string? PendingParam { get; private set; }

    public static void Arm(string routeId, string? param)
    {
        CurrentRouteId = routeId;
        PendingParam = param;
    }

    /// <summary>Забрать параметр (одноразово).</summary>
    public static string? TakeParam()
    {
        var p = PendingParam;
        PendingParam = null;
        return p;
    }

    /// <summary>Навигация по id маршрута — используется из глубины страниц.</summary>
    public static void Go(string routeId)
    {
        var w = MainWindow.Instance;
        w?.NavigateToRoute(routeId);
    }

    /// <summary>
    /// Забрать параметр маршрута и выбрать вкладку с подходящим заголовком
    /// (подстрока без учёта регистра и пробелов). Страница вызывает в Loaded.
    /// </summary>
    public static void SelectTab(DependencyObject root)
    {
        var param = TakeParam();
        if (string.IsNullOrEmpty(param)) return;
        var tabs = FindChild<TabControl>(root);
        if (tabs == null) return;
        var needle = param.Replace(" ", "").ToUpperInvariant();
        foreach (var item in tabs.Items)
        {
            if (item is not TabItem ti) continue;
            var header = (ti.Header as string ?? "").Replace(" ", "").ToUpperInvariant();
            if (header.Contains(needle)) { tabs.SelectedItem = item; return; }
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) return t;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindChild<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
