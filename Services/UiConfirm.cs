using System.Windows;

namespace OctavianRSM.Services;

/// <summary>
/// Единая точка подтверждений: опасное действие всегда показывает
/// точную цель и последствия в одинаковом формате, а не разнородный текст.
/// </summary>
public static class UiConfirm
{
    /// <summary>
    /// Подтверждение действия.
    /// </summary>
    /// <param name="action">что делаем: «Забанить», «Вайп карты», «Удалить плагин»</param>
    /// <param name="target">точная цель: «ник (7656…)», «OctaBoss.cs», «server.cfg»</param>
    /// <param name="consequences">последствия: «игрок не сможет войти», «файлы уйдут в _wipe_backup»</param>
    /// <param name="danger">danger-стиль вопроса (бан/удаление/остановка)</param>
    public static bool Ask(Window? owner, string action, string target,
        string consequences, bool danger = false)
        => MessageBox.Show(owner,
            $"{action}\n\nЦель: {target}\nПоследствия: {consequences}",
            "OctavianRSM — подтверждение",
            MessageBoxButton.YesNo,
            danger ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Короткая форма без владельца окна.</summary>
    public static bool Ask(string action, string target, string consequences, bool danger = false)
        => Ask(null, action, target, consequences, danger);

    /// <summary>Информационное сообщение единого вида.</summary>
    public static void Info(Window? owner, string title, string body)
        => MessageBox.Show(owner, body, $"OctavianRSM — {title}",
            MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Ошибка единого вида.</summary>
    public static void Error(Window? owner, string title, string body)
        => MessageBox.Show(owner, body, $"OctavianRSM — {title}",
            MessageBoxButton.OK, MessageBoxImage.Error);
}
