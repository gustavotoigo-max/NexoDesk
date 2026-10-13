using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Session;

namespace NexoDesk.Agent;

public static class AgentPaths
{
    /// <summary>Configuração da máquina; o script de instalação restringe a escrita a administradores.</summary>
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexoDesk");

    public static string ConfigFile { get; } = Path.Combine(ConfigDirectory, "agent.json");

    public static string DefaultLogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexoDesk", "agent-logs");
}

/// <summary>Monitor principal em pixels físicos (o processo é PerMonitorV2, então não há virtualização de DPI).</summary>
public sealed class PrimaryDisplaySource : IDisplaySource
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    public DisplayInfoMessage GetDisplayInfo()
    {
        int width = GetSystemMetrics(SmCxScreen);
        int height = GetSystemMetrics(SmCyScreen);
        uint dpi = GetDpiForSystem();
        return new DisplayInfoMessage(Math.Max(width, 1), Math.Max(height, 1), dpi == 0 ? 1.0 : dpi / 96.0);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}

/// <summary>Acompanha bloqueio da sessão do Windows. Com a sessão bloqueada não há desktop do usuário para mostrar.</summary>
public sealed class SessionLockMonitor : IDisposable
{
    private volatile bool _locked;

    public SessionLockMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public bool IsLocked => _locked;

    /// <summary>Disparado ao bloquear (true) ou desbloquear (false).</summary>
    public event Action<bool>? LockChanged;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _locked = true;
                LockChanged?.Invoke(true);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _locked = false;
                LockChanged?.Invoke(false);
                break;
        }
    }

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;
}
