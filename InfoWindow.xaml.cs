using System.Windows;

namespace OctavianRSM;

// Универсальное окно просмотра текста (stats/combatlog и подобный вывод RCON)
public partial class InfoWindow : Window
{
    public InfoWindow(string title, string body, Window? owner)
    {
        InitializeComponent();
        Title = $"OctavianRSM — {title}";
        TxtTitle.Text = title;
        TxtBody.Text = string.IsNullOrWhiteSpace(body) ? "сервер не вернул данных" : body;
        Owner = owner;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
