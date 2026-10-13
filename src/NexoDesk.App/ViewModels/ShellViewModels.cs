using System.Reflection;
using System.Windows.Input;
using NexoDesk.App.Services;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Security;
using NexoDesk.Ui;

namespace NexoDesk.App.ViewModels;

public static class AppInfo
{
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}

/// <summary>Primeira abertura: escolher se este computador é ADMIN ou PC REMOTO.</summary>
public sealed class SetupViewModel : ObservableObject
{
    private readonly AppState _state;
    private DeviceRole? _role;
    private string _deviceName;
    private string _tailscaleText = "Verificando o Tailscale…";
    private bool _tailscaleOk;

    public SetupViewModel(AppState state)
    {
        _state = state;
        _deviceName = state.Config.DeviceName;
        ContinueCommand = new RelayCommand(Continue, () => Role is not null && !string.IsNullOrWhiteSpace(DeviceName));
        _ = CheckTailscaleAsync();
    }

    public ICommand ContinueCommand { get; }
    public event Action? Completed;

    public DeviceRole? Role
    {
        get => _role;
        set
        {
            if (Set(ref _role, value))
            {
                OnPropertyChanged(nameof(IsAdmin));
                OnPropertyChanged(nameof(IsRemote));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsAdmin
    {
        get => Role == DeviceRole.Admin;
        set { if (value) Role = DeviceRole.Admin; }
    }

    public bool IsRemote
    {
        get => Role == DeviceRole.Remote;
        set { if (value) Role = DeviceRole.Remote; }
    }

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (Set(ref _deviceName, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string TailscaleText { get => _tailscaleText; private set => Set(ref _tailscaleText, value); }
    public bool TailscaleOk { get => _tailscaleOk; private set => Set(ref _tailscaleOk, value); }

    private async Task CheckTailscaleAsync()
    {
        TailscaleStatus ts = await TailscaleInfo.QueryAsync();
        TailscaleOk = ts.Installed && ts.Connected;
        TailscaleText = !ts.Installed
            ? "Tailscale não encontrado. Ele é necessário nos dois computadores: instale em tailscale.com/download."
            : !ts.Connected
                ? "Tailscale instalado, mas desconectado. Abra o Tailscale e entre na rede da empresa."
                : $"Tailscale conectado{(ts.BestAddress is { } a ? $" · este computador é {a}" : "")}.";
    }

    private void Continue()
    {
        string name = DeviceName.Trim();
        _state.Update(c =>
        {
            c.Role = Role;
            c.DeviceName = name.Length > 64 ? name[..64] : name;
            c.DeviceId = AppConfig.MakeId(name);
        });
        Completed?.Invoke();
    }
}

/// <summary>Moldura comum aos dois modos: cabeçalho, rodapé e configurações.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly AppState _state;

    public ShellViewModel(AppState state, object content, Action switchRole)
    {
        _state = state;
        Content = content;
        OpenLogsCommand = new RelayCommand(() =>
        {
            System.IO.Directory.CreateDirectory(AppPaths.LogDirectory);
            AppPaths.Open(AppPaths.LogDirectory);
        });
        Settings = new SettingsViewModel(state, switchRole);
        _state.Changed += () => OnPropertyChanged(nameof(DeviceName));
    }

    public object Content { get; }
    public SettingsViewModel Settings { get; }
    public ICommand OpenLogsCommand { get; }

    public bool IsAdmin => _state.Config.Role == DeviceRole.Admin;
    public string ModeText => IsAdmin ? "Modo ADMIN" : "Modo PC REMOTO";
    public string Tagline => IsAdmin ? "Acessar os PCs REMOTOS" : "Este computador pode ser acessado pelo ADMIN";
    public string DeviceName => _state.Config.DeviceName;
    public string Version => AppInfo.Version;
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Action _switchRole;
    private string _deviceName;

    public SettingsViewModel(AppState state, Action switchRole)
    {
        _state = state;
        _switchRole = switchRole;
        _deviceName = state.Config.DeviceName;
        SaveNameCommand = new RelayCommand(SaveName, () => !string.IsNullOrWhiteSpace(DeviceName) && DeviceName.Trim() != _state.Config.DeviceName);
        SwitchRoleCommand = new RelayCommand(() => _switchRole());
        SetThemeCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse(s, out NxThemeMode mode))
            {
                ThemeManager.Apply(mode);
                OnPropertyChanged(nameof(ThemeAuto));
                OnPropertyChanged(nameof(ThemeLight));
                OnPropertyChanged(nameof(ThemeDark));
            }
        });
        OpenDataFolderCommand = new RelayCommand(() => AppPaths.Open(AppPaths.DataDirectory));
    }

    public ICommand SaveNameCommand { get; }
    public ICommand SwitchRoleCommand { get; }
    public ICommand SetThemeCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (Set(ref _deviceName, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsAdmin => _state.Config.Role == DeviceRole.Admin;
    public string RoleText => IsAdmin ? "ADMIN — acessa os PCs REMOTOS" : "PC REMOTO — pode ser acessado pelo ADMIN";
    public string SwitchRoleText => IsAdmin ? "Mudar para PC REMOTO" : "Mudar para ADMIN";

    public bool ThemeAuto => ThemeManager.Mode == NxThemeMode.Auto;
    public bool ThemeLight => ThemeManager.Mode == NxThemeMode.Light;
    public bool ThemeDark => ThemeManager.Mode == NxThemeMode.Dark;

    public string Fingerprint => CertificateFingerprint.Short(_state.Fingerprint) + "…";
    public string FullFingerprint => _state.Fingerprint;
    public string ValidityText
    {
        get
        {
            int days = DeviceIdentity.DaysUntilExpiry(_state.Certificate);
            string date = _state.Certificate.NotAfter.ToString("dd/MM/yyyy");
            return days < 30 ? $"vence em {date} ({days} dias) — será preciso parear de novo" : $"válida até {date}";
        }
    }

    public string Version => AppInfo.Version;

    private void SaveName()
    {
        string name = DeviceName.Trim();
        _state.Update(c => c.DeviceName = name.Length > 64 ? name[..64] : name);
        DeviceName = _state.Config.DeviceName;
    }
}
