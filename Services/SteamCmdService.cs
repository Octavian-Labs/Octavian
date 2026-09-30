using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using OctavianRSM.Models;

namespace OctavianRSM.Services;

// Обёртка над steamcmd для установки/валидации сервера (app 258550)
public class SteamCmdService
{
    private readonly RsmSettings _settings;
    public event Action<string>? OnOutput;

    public SteamCmdService(RsmSettings settings) => _settings = settings;

    public async Task<int> InstallOrUpdateAsync(bool validate, string branch = "")
    {
        var args = new StringBuilder();
        args.Append("+force_install_dir \"").Append(_settings.ServerDirPath).Append("\" ");
        args.Append("+login anonymous +app_update 258550");
        if (!string.IsNullOrEmpty(branch)) args.Append(" -beta ").Append(branch);
        if (validate) args.Append(" validate"); // steamcmd: validate — голое слово, без дефиса
        args.Append(" +quit");

        var psi = new ProcessStartInfo
        {
            FileName = _settings.SteamCmdExePath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) OnOutput?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) OnOutput?.Invoke(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
