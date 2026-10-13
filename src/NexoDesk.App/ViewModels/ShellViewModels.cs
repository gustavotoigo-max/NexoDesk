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

    public ShellViewModel(AppState state, object content, Action switchRole, Action requestExit)
    {
        _state = state;
        Content = content;
        OpenLogsCommand = new RelayCommand(() =>
        {
            System.IO.Directory.CreateDirectory(AppPaths.LogDirectory);
            AppPaths.Open(AppPaths.LogDirectory);
        });
        Update = new UpdateViewModel(requestExit);
        Settings = new SettingsViewModel(state, switchRole, Update);
        _state.Changed += () => OnPropertyChanged(nameof(DeviceName));
        _ = Update.CheckAsync();
    }

    public object Content { get; }
    public SettingsViewModel Settings { get; }
    public UpdateViewModel Update { get; }
    public ICommand OpenLogsCommand { get; }

    public bool IsAdmin => _state.Config.Role == DeviceRole.Admin;
    public string ModeText => IsAdmin ? "Modo ADMIN" : "Modo PC REMOTO";
    public string Tagline => IsAdmin ? "Acessar os PCs REMOTOS" : "Este computador pode ser acessado pelo ADMIN";
    public string DeviceName => _state.Config.DeviceName;
    public string Version => AppInfo.Version;
}

/// <summary>Atualização pela interface: consulta o GitHub e, se houver versão nova, baixa e instala.</summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly Action _requestExit;
    private UpdateInfo? _info;
    private string _status = "";
    private bool _busy;
    private bool _checking;

    public UpdateViewModel(Action requestExit)
    {
        _requestExit = requestExit;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !_busy && !_checking);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => Available && !_busy);
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }

    public bool Available => _info is not null;
    public string AvailableText => _info is null ? "" : $"Atualização disponível: versão {_info.Version}";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public async Task CheckAsync()
    {
        if (_checking)
            return;
        _checking = true;
        CommandManager.InvalidateRequerySuggested();
        Status = "Procurando atualizações…";
        try
        {
            _info = await UpdateService.CheckAsync();
            OnPropertyChanged(nameof(Available));
            OnPropertyChanged(nameof(AvailableText));
            Status = _info is null ? "Você está na versão mais recente." : AvailableText;
        }
        finally
        {
            _checking = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallAsync()
    {
        if (_info is null)
            return;
        Busy = true;
        Status = "Baixando a atualização…";
        try
        {
            var progress = new Progress<double>(p => Status = $"Baixando a atualização… {p:P0}");
            bool ok = await UpdateService.DownloadAndRunAsync(_info, progress);
            if (ok)
            {
                // Fecha o NexoDesk para o instalador conseguir substituir os arquivos.
                // Sem isso, o instalador tenta fechar sozinho, mas no PC REMOTO o X só esconde
                // para a bandeja — então o arquivo fica em uso e o processo fica preso.
                Status = "Instalador aberto. Fechando o NexoDesk para concluir a atualização…";
                await Task.Delay(1500);
                _requestExit();
            }
            else
            {
                Status = "Não foi possível baixar a atualização. Tente mais tarde.";
            }
        }
        finally
        {
            Busy = false;
        }
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Action _switchRole;
    private string _deviceName;

    public SettingsViewModel(AppState state, Action switchRole, UpdateViewModel update)
    {
        _state = state;
        _switchRole = switchRole;
        Update = update;
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
        SetAccentCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse(s, out NxAccent accent))
            {
                ThemeManager.ApplyAccent(accent);
                foreach (NxAccent a in Enum.GetValues<NxAccent>())
                    OnPropertyChanged($"Accent{a}");
            }
        });
    }

    public UpdateViewModel Update { get; }

    public ICommand SaveNameCommand { get; }
    public ICommand SwitchRoleCommand { get; }
    public ICommand SetThemeCommand { get; }
    public ICommand SetAccentCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public bool AccentAzul => ThemeManager.Accent == NxAccent.Azul;
    public bool AccentEsmeralda => ThemeManager.Accent == NxAccent.Esmeralda;
    public bool AccentVioleta => ThemeManager.Accent == NxAccent.Violeta;
    public bool AccentGrafite => ThemeManager.Accent == NxAccent.Grafite;
    public bool AccentAmbar => ThemeManager.Accent == NxAccent.Ambar;

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
