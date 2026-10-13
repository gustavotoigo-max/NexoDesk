using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;
using NexoDesk.Ui;
using NexoDesk.Ui.Controls;

namespace NexoDesk.Agent;

public enum AgentState
{
    Stopped,
    Listening,
    InSession,
    Error,
}

public sealed record AllowedClientRow(string Name, string Fingerprint);

/// <summary>
/// Liga a configuração, o certificado, a política de consentimento e o AgentHost, e expõe o estado para as telas.
/// </summary>
public sealed class AgentController : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SessionLockMonitor _lock = new();
    private AgentHost? _host;
    private X509Certificate2? _certificate;
    private AgentState _state = AgentState.Stopped;
    private string? _problem;
    private string _listenText = "—";
    private string _machineText = "—";
    private string _certificateText = "—";
    private bool _unattended;
    private string? _sessionAdmin;
    private string? _sessionDetail;
    private string _logDirectory = AgentPaths.DefaultLogDirectory;

    public AgentController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        EndSessionCommand = new RelayCommand(EndSession, () => HasSession);
        RestartCommand = new AsyncRelayCommand(RestartAsync);
        OpenConfigCommand = new RelayCommand(OpenConfig);
        OpenLogsCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(_logDirectory);
            Shell(_logDirectory);
        });
        ClearLogCommand = new RelayCommand(() => Log.Clear(), () => Log.Count > 0);
    }

    public static string Version { get; } =
        (typeof(AgentController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public ObservableCollection<AllowedClientRow> AllowedClients { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];

    public ICommand EndSessionCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand OpenConfigCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand ClearLogCommand { get; }

    public event Action<AgentSession>? SessionActivated;
    public event Action? SessionFinished;
    public event Action? StateChanged;

    public string ConfigPath => AgentPaths.ConfigFile;
    public string VersionText => Version;

    public AgentState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateKind));
                StateChanged?.Invoke();
            }
        }
    }

    public string StateText => State switch
    {
        AgentState.Listening => "Aguardando conexões",
        AgentState.InSession => "Acesso remoto ativo",
        AgentState.Error => "Não iniciado",
        _ => "Parado",
    };

    public BadgeKind StateKind => State switch
    {
        AgentState.Listening => BadgeKind.Success,
        AgentState.InSession => BadgeKind.Warning,
        AgentState.Error => BadgeKind.Danger,
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

    public string ListenText { get => _listenText; private set => Set(ref _listenText, value); }
    public string MachineText { get => _machineText; private set => Set(ref _machineText, value); }
    public string CertificateText { get => _certificateText; private set => Set(ref _certificateText, value); }

    public bool Unattended
    {
        get => _unattended;
        private set
        {
            if (Set(ref _unattended, value))
            {
                OnPropertyChanged(nameof(UnattendedText));
                OnPropertyChanged(nameof(UnattendedKind));
            }
        }
    }

    public string UnattendedText => Unattended ? "Ligado — conexões autorizadas entram sem confirmação" : "Desligado — cada conexão pede Permitir neste computador";
    public BadgeKind UnattendedKind => Unattended ? BadgeKind.Warning : BadgeKind.Success;

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
        AgentConfig config;
        try
        {
            if (!File.Exists(AgentPaths.ConfigFile))
                throw new ConfigException($"Configuração não encontrada em {AgentPaths.ConfigFile}. Rode scripts\\Setup-NexoDeskAgent.ps1 como administrador.");
            config = ConfigStore.LoadAgent(AgentPaths.ConfigFile);
            IReadOnlyList<string> problems = config.Validate();
            if (problems.Count > 0)
                throw new ConfigException(string.Join("\n", problems.Select(p => "• " + p)));
        }
        catch (ConfigException ex)
        {
            Fail(ex.Message);
            return;
        }

        MachineText = $"{config.DisplayName} ({config.MachineId})";
        Unattended = config.UnattendedAccessEnabled;
        _logDirectory = string.IsNullOrWhiteSpace(config.LogDirectory) ? AgentPaths.DefaultLogDirectory : config.LogDirectory;
        AllowedClients.Clear();
        foreach (AllowedClientEntry c in config.AllowedClients)
            AllowedClients.Add(new AllowedClientRow(c.Name, CertificateFingerprint.Short(CertificateFingerprint.Normalize(c.Sha256)!) + "…"));

        _certificate?.Dispose();
        _certificate = CertificateStoreLookup.FindWithPrivateKey(config.ServerCertificateSha256);
        if (_certificate is null)
        {
            Fail("O certificado deste computador não foi encontrado (com chave privada) no repositório Pessoal do Windows. " +
                 "Gere com scripts\\New-NexoDeskCertificate.ps1 -Role Agent e confira serverCertificateSha256.");
            return;
        }
        CertificateText = CertificateFingerprint.Short(CertificateFingerprint.Sha256(_certificate)) + $"… · válido até {_certificate.NotAfter:dd/MM/yyyy}";

        IPAddress address;
        try
        {
            address = TailscaleInterfaces.ResolveListenAddress(config.ListenAddress, config.AllowNonTailscaleAddress);
        }
        catch (ConfigException ex)
        {
            Fail(ex.Message);
            return;
        }

        IAuditLog audit;
        try
        {
            audit = new CompositeAuditLog(new JsonLinesAuditLog(_logDirectory), new UiAuditLog(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"Não foi possível usar a pasta de logs {_logDirectory}: {ex.Message}");
            return;
        }

        var consentTimeout = TimeSpan.FromSeconds(config.ConsentTimeoutSeconds);
        _host = new AgentHost(new AgentHostOptions
        {
            Endpoint = new IPEndPoint(address, config.Port),
            ServerCertificate = _certificate,
            AllowedClients = config.AllowedClients.Select(c => new AuthorizedClient(c.Name, c.Sha256)).ToList(),
            MaxConnectionAttemptsPerMinute = config.MaxConnectionAttemptsPerMinute,
            TlsTimeout = TimeSpan.FromSeconds(config.HandshakeTimeoutSeconds),
            Session = new AgentSessionOptions
            {
                ServerId = config.MachineId,
                DisplayName = config.DisplayName,
                AppVersion = Version,
                // M2: visualização negociada; a imagem chega no M3 e o controle no M5.
                Features = [Features.View],
                HandshakeTimeout = TimeSpan.FromSeconds(config.HandshakeTimeoutSeconds),
                IdleTimeout = TimeSpan.FromSeconds(config.IdleTimeoutSeconds),
                PolicyTimeout = consentTimeout + TimeSpan.FromSeconds(5),
            },
        }, new ConsentPolicy(_dispatcher, _lock, config.UnattendedAccessEnabled, consentTimeout), audit, new PrimaryDisplaySource());

        _host.SessionActivated += session => _dispatcher.InvokeAsync(() =>
        {
            SessionAdmin = session.Client.Name;
            SessionDetail = $"{(session.Mode == SessionMode.Control ? "Controlando" : "Visualizando")} desde {DateTime.Now:HH:mm} · sessão {session.SessionId}";
            State = AgentState.InSession;
            SessionActivated?.Invoke(session);
        });
        _host.SessionEnded += (_, _) => _dispatcher.InvokeAsync(() =>
        {
            SessionAdmin = null;
            SessionDetail = null;
            if (State == AgentState.InSession)
                State = AgentState.Listening;
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
            Fail($"Não foi possível escutar em {address}:{config.Port} ({ex.SocketErrorCode}). Outra cópia do Agent pode estar aberta ou a porta está em uso.");
            return;
        }

        ListenText = $"{address}:{config.Port}";
        State = AgentState.Listening;
    }

    public void EndSession() => _host?.EndCurrentSession(EndReason.LocalUserEnded);

    public async Task StopAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }
        SessionAdmin = null;
        if (State != AgentState.Error)
            State = AgentState.Stopped;
    }

    private async Task RestartAsync()
    {
        await StopAsync();
        AddLog(LogLevel.Dim, "Reiniciando com a configuração atual…");
        await StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lock.Dispose();
        _certificate?.Dispose();
    }

    private void Fail(string message)
    {
        Problem = message;
        State = AgentState.Error;
        ListenText = "—";
        AddLog(LogLevel.Error, message.Replace("\n", " "));
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

    private void OpenConfig()
    {
        if (File.Exists(AgentPaths.ConfigFile))
            Shell(AgentPaths.ConfigFile);
        else
        {
            Directory.CreateDirectory(AgentPaths.ConfigDirectory);
            Shell(AgentPaths.ConfigDirectory);
        }
    }

    private void Shell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            AddLog(LogLevel.Error, $"Não foi possível abrir {path}: {ex.Message}");
        }
    }

    /// <summary>Traduz os eventos de auditoria para o cartão "Registro" da tela do Agent.</summary>
    private sealed class UiAuditLog(AgentController owner) : IAuditLog
    {
        public void Write(AuditEvent e)
        {
            string who = e.ClientName ?? "Cliente";
            string fp = e.ClientFingerprint is { Length: >= 16 } f ? CertificateFingerprint.Short(f) + "…" : "sem certificado";
            (LogLevel level, string text) = e.Event switch
            {
                AuditEventType.ListenerStarted => (LogLevel.Ok, $"Aguardando conexões em {e.Result}."),
                AuditEventType.ListenerStopped => (LogLevel.Dim, "Escuta encerrada."),
                AuditEventType.ConnectionRejected => (LogLevel.Dim, $"Conexão de {e.RemoteAddress} não prosseguiu: {e.Reason}"),
                AuditEventType.AuthenticationFailed => (LogLevel.Error, $"Certificado recusado de {e.RemoteAddress} ({fp}): {e.Reason}"),
                AuditEventType.AuthenticationSucceeded => (LogLevel.Dim, $"{who} autenticado ({e.Result})."),
                AuditEventType.SessionRequested => (LogLevel.Info, $"{who} pediu acesso ({(e.Result == nameof(SessionMode.Control) ? "controlar" : "visualizar")})."),
                AuditEventType.SessionDenied => (LogLevel.Warn, $"Acesso recusado para {who}: {e.Reason}"),
                AuditEventType.SessionStarted => (LogLevel.Ok, $"Sessão {e.SessionId} iniciada com {who}."),
                AuditEventType.SessionEnded => (LogLevel.Info, $"Sessão {e.SessionId} encerrada: {e.Reason}"),
                AuditEventType.ProtocolError => (LogLevel.Error, $"Erro de protocolo com {e.RemoteAddress}: {e.Reason}"),
                _ => (LogLevel.Info, $"{e.Event}: {e.Reason}"),
            };
            owner.AddLog(level, text);
        }
    }
}
