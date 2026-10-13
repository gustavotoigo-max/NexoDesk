using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;

namespace NexoDesk.App.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexoDesk");

    public static string ConfigFile { get; } = Path.Combine(DataDirectory, "config.json");

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexoDesk", "logs");

    public static string ExecutablePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NexoDesk.exe");

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Debug.WriteLine(ex.Message);
        }
    }
}

/// <summary>Monitor principal em pixels físicos (o processo é PerMonitorV2, sem virtualização de DPI).</summary>
public sealed class PrimaryDisplaySource : IDisplaySource
{
    public DisplayInfoMessage GetDisplayInfo()
    {
        int width = GetSystemMetrics(0);
        int height = GetSystemMetrics(1);
        uint dpi = GetDpiForSystem();
        return new DisplayInfoMessage(Math.Max(width, 1), Math.Max(height, 1), dpi == 0 ? 1.0 : dpi / 96.0);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}

/// <summary>Bloqueio da sessão do Windows: com a tela bloqueada não há desktop do usuário para mostrar.</summary>
public sealed class SessionLockMonitor : IDisposable
{
    private volatile bool _locked;

    public SessionLockMonitor() => SystemEvents.SessionSwitch += OnSessionSwitch;

    public bool IsLocked => _locked;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            _locked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _locked = false;
    }

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;
}

/// <summary>Início automático na sessão do usuário (HKCU\...\Run), sem precisar de administrador.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NexoDesk";

    public static void Set(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
                key.SetValue(ValueName, $"\"{AppPaths.ExecutablePath}\" --minimized");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Debug.WriteLine(ex.Message);
        }
    }
}

/// <summary>Estado do Tailscale neste computador.</summary>
public sealed record TailscaleStatus(bool Installed, bool Connected, string? DnsName, IPAddress? IPv4, string? Tailnet)
{
    /// <summary>Melhor endereço para informar ao ADMIN: nome MagicDNS, senão o IP.</summary>
    public string? BestAddress => DnsName ?? IPv4?.ToString();
}

public static class TailscaleInfo
{
    private static readonly string[] CliCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
    ];

    /// <summary>Usa "tailscale status --json"; se a linha de comando não existir, olha as interfaces de rede.</summary>
    public static async Task<TailscaleStatus> QueryAsync()
    {
        IPAddress? nicIp = TailscaleInterfaces.FindLocalAddresses()
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        string? cli = CliCandidates.FirstOrDefault(File.Exists);
        if (cli is null)
            return new TailscaleStatus(nicIp is not null, nicIp is not null, null, nicIp, null);

        try
        {
            var psi = new ProcessStartInfo(cli, "status --json")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            string json = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? state = root.TryGetProperty("BackendState", out JsonElement bs) ? bs.GetString() : null;
            string? dns = null;
            IPAddress? ip = nicIp;
            if (root.TryGetProperty("Self", out JsonElement self))
            {
                if (self.TryGetProperty("DNSName", out JsonElement d) && d.GetString() is { Length: > 0 } name)
                    dns = name.TrimEnd('.');
                if (self.TryGetProperty("TailscaleIPs", out JsonElement ips))
                {
                    foreach (JsonElement e in ips.EnumerateArray())
                    {
                        if (IPAddress.TryParse(e.GetString(), out IPAddress? parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            ip = parsed;
                            break;
                        }
                    }
                }
            }
            string? tailnet = root.TryGetProperty("CurrentTailnet", out JsonElement tn) && tn.ValueKind == JsonValueKind.Object
                && tn.TryGetProperty("Name", out JsonElement tnName) ? tnName.GetString() : null;
            return new TailscaleStatus(true, state == "Running", dns, ip, tailnet);
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new TailscaleStatus(true, nicIp is not null, null, nicIp, null);
        }
    }
}
