using System;
using System.IO;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Пишет все входящие RCON-пуши типа Chat в лог-файл — как RCON.Events→chats.db в RSM-FMX
public class ChatLogService
{
    private readonly RsmSettings _settings;
    public ChatLogService(RsmSettings settings) => _settings = settings;

    public string LogDir => Path.Combine(_settings.ServerDirPath, "carbon", "logs");
    public string LogPath => Path.Combine(LogDir, "chat_history.log");

    public void OnRconMessage(string type, string msg)
    {
        if (!type.Equals("Chat", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(msg)) return;
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }
}
