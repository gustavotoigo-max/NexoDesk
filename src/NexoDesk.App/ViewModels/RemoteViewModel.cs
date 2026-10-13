using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NexoDesk.App.Services;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Pairing;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;
using NexoDesk.Ui;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.ViewModels;

public enum RemoteStatus
{
    Starting,
    Listening,
    InSession,
    Error,
}

public sealed record AdminRow(string Name, string Fingerprint, string ShortFingerprint, string PairedAtText);

/// <summary>Modo PC REMOTO: espera o ADMIN, gera o código de pareamento, pede permissão e mostra o aviso de acesso.</summary>
public sealed class RemoteViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan PairingValidity = TimeSpan.FromMinutes(10);

    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private readonly SessionLockMonitor _lock = new();
    private readonly PairingWindow _pairing = new();
    private readonly DispatcherTimer _pairingTimer;
    private AgentHost? _host;
    private RemoteStatus _status = RemoteStatus.Starting;
    private string? _problem;
    private string _address = "procurando…";
    private string _ipText = "—";
    private string _tailscaleText = "verificando…";
    private string? _pairingCode;
    private string _pairingRemaining = "";
    private string? _sessionAdmin;
    private string? _sessionDetail;

    public RemoteViewModel(AppState state, Dispatcher dispatcher)
    {
        _state = state;
        _dispatcher = dispatcher;
        _pairing.Changed += () => _dispatcher.InvokeAsync(OnPairingChanged);
        _pairingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pairingTimer.Tick += (_, _) => UpdatePairingRemaining();

        OpenPairingCommand = new RelayCommand(OpenPairing, () => Status is RemoteStatus.Listening or RemoteStatus.InSession);
        CancelPairingCommand = new RelayCommand(() => _pairing.Close(), () => PairingActive);
        CopyAddressCommand = new RelayCommand(() => CopyToClipboard(Address), () => Status != RemoteStatus.Starting);
        CopyCodeCommand = new RelayCommand(() => CopyToClipboard(PairingCode ?? ""), () => PairingActive);
        RemoveAdminCommand = new RelayCommand(p => { if (p is AdminRow row) RemoveAdmin(row); });
        EndSessionCommand = new RelayCommand(EndSession, () => HasSession);
        RestartCommand = new AsyncRelayCommand(RestartAsync);
        ClearLogCommand = new RelayCommand(() => Log.Clear(), () => Log.Count > 0);
        LoadAdmins();
    }

    public ObservableCollection<AdminRow> Admins { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];

    public ICommand OpenPairingCommand { get; }
    public ICommand CancelPairingCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public ICommand CopyCodeCommand { get; }
    public ICommand RemoveAdminCommand { get; }
    public ICommand EndSessionCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand ClearLogCommand { get; }

    public event Action<AgentSession>? SessionActivated;
    public event Action? SessionFinished;
    public event Action? StatusChanged;

    public string DeviceName => _state.Config.DeviceName;

    public RemoteStatus Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusKind));
                OnPropertyChanged(nameof(IsReady));
                CommandManager.InvalidateRequerySuggested();
                StatusChanged?.Invoke();
            }
        }
    }

    public bool IsReady => Status is RemoteStatus.Listening or RemoteStatus.InSession;

    public string StatusText => Status switch
    {
        RemoteStatus.Listening => "Pronto para o ADMIN",
        RemoteStatus.InSession => "Acesso remoto ativo",
        RemoteStatus.Error => "Não está aceitando conexões",
        _ => "Iniciando…",
    };

    public BadgeKind StatusKind => Status switch
    {
        RemoteStatus.Listening => BadgeKind.Success,
        RemoteStatus.InSession => BadgeKind.Warning,
        RemoteStatus.Error => BadgeKind.Danger,
        _ => BadgeKind.Neutral,
    };

    public string? Problem
    {
        get => _problem;
        private set
        {
            if (Set(ref _problem, value))
                OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => Problem is not null;

    /// <summary>O que o ADMIN digita no campo "Endereço": nome MagicDNS ou IP Tailscale.</summary>
    public string Address { get => _address; private set => Set(ref _address, value); }
    public string IpText { get => _ipText; private set => Set(ref _ipText, value); }
    public string TailscaleText { get => _tailscaleText; private set => Set(ref _tailscaleText, value); }

    public string? PairingCode
    {
        get => _pairingCode;
        private set
        {
            if (Set(ref _pairingCode, value))
            {
                OnPropertyChanged(nameof(PairingActive));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool PairingActive => PairingCode is not null;
    public string PairingRemaining { get => _pairingRemaining; private set => Set(ref _pairingRemaining, value); }

    public bool HasAdmins => Admins.Count > 0;

    public bool UnattendedAccess
    {
        get => _state.Config.Remote.UnattendedAccessEnabled;
        set
        {
            if (value == UnattendedAccess)
                return;
            if (value && !NxDialogWindow.Ask(Application.Current.MainWindow, new DialogOptions
            {
                Kind = DialogKind.Warning,
                Title = "Ligar o acesso sem confirmação?",
                Text = "Os ADMINs pareados poderão ver este computador sem que ninguém clique em Permitir. " +
                       "O aviso de acesso ativo continua aparecendo no topo da tela.",
                PrimaryText = "Ligar",
                SecondaryText = "Manter desligado",
            }))
            {
                OnPropertyChanged();
                return;
            }
            _state.Update(c => c.Remote.UnattendedAccessEnabled = value);
            AddLog(value ? LogLevel.Warn : LogLevel.Info, value ? "Acesso sem confirmação LIGADO." : "Acesso sem confirmação desligado: cada conexão pede Permitir.");
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => _state.Config.Remote.StartWithWindows;
        set
        {
            if (value == StartWithWindows)
                return;
            _state.Update(c => c.Remote.StartWithWindows = value);
            Autostart.Set(value);
            OnPropertyChanged();
        }
    }

    public string? SessionAdmin
    {
        get => _sessionAdmin;
        private set
        {
            if (Set(ref _sessionAdmin, value))
            {
                OnPropertyChanged(nameof(HasSession));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? SessionDetail { get => _sessionDetail; private set => Set(ref _sessionDetail, value); }
    public bool HasSession => SessionAdmin is not null;

    public async Task StartAsync()
    {
        Problem = null;
        Status = RemoteStatus.Starting;
        Autostart.Set(_state.Config.Remote.StartWithWindows);

        TailscaleStatus ts = await TailscaleInfo.QueryAsync();
        TailscaleText = !ts.Installed ? "não instalado"
            : !ts.Connected ? "desconectado"
            : ts.Tailnet is { Length: > 0 } net ? $"conectado · {net}" : "conectado";

        IPAddress address;
        try
        {
            address = TailscaleInterfaces.ResolveListenAddress(_state.Config.Remote.ListenAddress, _state.Config.Remote.AllowNonTailscaleAddress);
        }
        catch (ConfigException ex)
        {
            Fail(ts.Installed
                ? $"{ex.Message} Depois clique em Tentar de novo."
                : "O Tailscale não está instalado. Instale em tailscale.com/download, entre na rede da empresa e clique em Tentar de novo.");
            return;
        }

        Address = ts.DnsName ?? address.ToString();
        IpText = address.ToString();

        IAuditLog audit;
        try
        {
            audit = new CompositeAuditLog(new JsonLinesAuditLog(AppPaths.LogDirectory), new UiAuditLog(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"Não foi possível usar a pasta de logs {AppPaths.LogDirectory}: {ex.Message}");
            return;
        }

        var consentTimeout = TimeSpan.FromSeconds(_state.Config.Remote.ConsentTimeoutSeconds);
        _host = new AgentHost(new AgentHostOptions
        {
            Endpoint = new IPEndPoint(address, _state.Config.Remote.Port),
            ServerCertificate = _state.Certificate,
            Clients = new ConfigClientStore(_state),
            Pairing = _pairing,
            Session = new AgentSessionOptions
            {
                ServerId = _state.Config.DeviceId,
                DisplayName = _state.Config.DeviceName,
                AppVersion = AppInfo.Version,
                // Visualização negociada; a imagem chega no M3 e o controle no M5.
                Features = [Features.View],
                PolicyTimeout = consentTimeout + TimeSpan.FromSeconds(5),
            },
        }, new ConsentPolicy(_dispatcher, _lock, () => _state.Config.Remote.UnattendedAccessEnabled, consentTimeout), audit, new PrimaryDisplaySource());

        _host.SessionActivated += session => _dispatcher.InvokeAsync(() =>
        {
            SessionAdmin = session.ClientName;
            SessionDetail = $"{(session.Mode == SessionMode.Control ? "Controlando" : "Vendo a tela")} desde {DateTime.Now:HH:mm}";
            Status = RemoteStatus.InSession;
            SessionActivated?.Invoke(session);
        });
        _host.SessionEnded += (_, _) => _dispatcher.InvokeAsync(() =>
        {
            SessionAdmin = null;
            SessionDetail = null;
            if (Status == RemoteStatus.InSession)
                Status = RemoteStatus.Listening;
            SessionFinished?.Invoke();
        });

        try
        {
            _host.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            await _host.DisposeAsync();
            _host = null;
            Fail($"A porta {_state.Config.Remote.Port} está em uso ({ex.SocketErrorCode}). Feche outra cópia do NexoDesk e clique em Tentar de novo.");
            return;
        }

        Status = RemoteStatus.Listening;
        if (_state.IdentityRecreated && HasAdmins)
            AddLog(LogLevel.Warn, "A identidade deste computador foi recriada. Os ADMINs precisam parear de novo.");
    }

    public void EndSession() => _host?.EndCurrentSession(EndReason.LocalUserEnded);

    public async Task StopAsync()
    {
        _pairing.Close();
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
        SessionAdmin = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lock.Dispose();
    }

    private async Task RestartAsync()
    {
        await StopAsync();
        AddLog(LogLevel.Dim, "Reiniciando…");
        await StartAsync();
    }

    private void OpenPairing()
    {
        PairingCode = _pairing.Open(PairingValidity);
        UpdatePairingRemaining();
        _pairingTimer.Start();
        AddLog(LogLevel.Info, "Código de pareamento gerado (vale 10 minutos, uma vez).");
    }

    private void OnPairingChanged()
    {
        if (_pairing.IsOpen)
            return;
        PairingCode = null;
        PairingRemaining = "";
        _pairingTimer.Stop();
        LoadAdmins();
    }

    private void UpdatePairingRemaining()
    {
        TimeSpan left = _pairing.ExpiresAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero || !_pairing.IsOpen)
        {
            if (PairingActive)
                AddLog(LogLevel.Dim, "Código de pareamento expirou.");
            _pairing.Close();
            OnPairingChanged();
            return;
        }
        PairingRemaining = $"Vale por mais {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void LoadAdmins()
    {
        Admins.Clear();
        foreach (PairedAdmin a in _state.Config.Remote.Admins.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string fp = CertificateFingerprint.Normalize(a.Sha256) ?? a.Sha256;
            Admins.Add(new AdminRow(a.Name, fp, CertificateFingerprint.Short(fp) + "…", a.PairedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm")));
        }
        OnPropertyChanged(nameof(HasAdmins));
    }

    private void RemoveAdmin(AdminRow row)
    {
        bool confirm = NxDialogWindow.Ask(Application.Current.MainWindow, new DialogOptions
        {
            Kind = DialogKind.Danger,
            Title = $"Remover o ADMIN \"{row.Name}\"?",
            Text = "Ele não conseguirá mais acessar este computador. Para voltar, será preciso parear de novo.",
            PrimaryText = "Remover",
            SecondaryText = "Cancelar",
            PrimaryIsDanger = true,
        });
        if (!confirm)
            return;

        _state.Update(c => c.Remote.Admins.RemoveAll(a => CertificateFingerprint.Normalize(a.Sha256) == row.Fingerprint));
        if (_host?.CurrentSession?.Client?.Fingerprint == row.Fingerprint)
            _host.EndCurrentSession(EndReason.PermissionRevoked);
        AddLog(LogLevel.Warn, $"ADMIN \"{row.Name}\" removido.");
        LoadAdmins();
    }

    private void Fail(string message)
    {
        Problem = message;
        Status = RemoteStatus.Error;
        AddLog(LogLevel.Error, message);
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            AddLog(LogLevel.Dim, "Copiado para a área de transferência.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Área de transferência ocupada por outro programa; o texto continua visível na tela.
        }
    }

    internal void AddLog(LogLevel level, string text)
    {
        void Add()
        {
            Log.Add(new LogLine(DateTime.Now, level, text));
            while (Log.Count > 300)
                Log.RemoveAt(0);
        }

        if (_dispatcher.CheckAccess())
            Add();
        else
            _dispatcher.InvokeAsync(Add);
    }

    /// <summary>Traduz a auditoria para o cartão "Registro".</summary>
    private sealed class UiAuditLog(RemoteViewModel owner) : IAuditLog
    {
        public void Write(AuditEvent e)
        {
            string who = e.ClientName ?? "ADMIN";
            (LogLevel level, string text) = e.Event switch
            {
                AuditEventType.ListenerStarted => (LogLevel.Ok, $"Aguardando o ADMIN em {e.Result}."),
                AuditEventType.ListenerStopped => (LogLevel.Dim, "Parou de aceitar conexões."),
                AuditEventType.ConnectionRejected => (LogLevel.Dim, $"Conexão de {e.RemoteAddress} não prosseguiu: {e.Reason}"),
                AuditEventType.AuthenticationFailed => (LogLevel.Error, $"Conexão recusada de {e.RemoteAddress}: computador não pareado."),
                AuditEventType.AuthenticationSucceeded => (LogLevel.Dim, $"{who} conectou ({e.Result})."),
                AuditEventType.Paired => (LogLevel.Ok, $"ADMIN \"{who}\" pareado com sucesso."),
                AuditEventType.PairingFailed => (LogLevel.Warn, $"Pareamento recusado ({who}): {e.Reason}"),
                AuditEventType.SessionRequested => (LogLevel.Info, $"{who} pediu acesso."),
                AuditEventType.SessionDenied => (LogLevel.Warn, $"Acesso recusado para {who}: {e.Reason}"),
                AuditEventType.SessionStarted => (LogLevel.Ok, $"Acesso iniciado por {who}."),
                AuditEventType.SessionEnded => (LogLevel.Info, $"Acesso encerrado: {e.Reason}"),
                AuditEventType.ProtocolError => (LogLevel.Error, $"Erro de comunicação com {e.RemoteAddress}: {e.Reason}"),
                _ => (LogLevel.Info, $"{e.Event}: {e.Reason}"),
            };
            owner.AddLog(level, text);
            if (e.Event == AuditEventType.Paired)
                owner._dispatcher.InvokeAsync(owner.LoadAdmins);
        }
    }
}
