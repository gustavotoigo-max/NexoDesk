using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using NexoDesk.Admin.Views;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;
using NexoDesk.Shared.Transport;
using NexoDesk.Ui;

namespace NexoDesk.Admin.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IAuditLog _audit;
    private AdminContext? _context;
    private CancellationTokenSource? _connectCts;
    private string _statusText = "Pronto.";
    private string _statusTag = "Ready";
    private bool _isConnecting;
    private string? _configProblem;
    private string _identityText = "—";
    private bool _controlMode;

    public MainViewModel()
    {
        try
        {
            _audit = new JsonLinesAuditLog(AdminPaths.LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _audit = new MemoryAuditLog();
        }

        ReloadCommand = new RelayCommand(Reload, () => !IsConnecting);
        OpenConfigCommand = new RelayCommand(() => OpenInShell(AdminPaths.ConfigFile));
        OpenLogsCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AdminPaths.LogDirectory);
            OpenInShell(AdminPaths.LogDirectory);
        });
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => Machines.Count > 0 && !IsConnecting);
        CheckCommand = new AsyncRelayCommand(p => p is MachineViewModel m ? CheckAsync(m) : Task.CompletedTask,
            p => p is MachineViewModel { IsIdle: true });
        ConnectCommand = new AsyncRelayCommand(p => p is MachineViewModel m ? ConnectAsync(m) : Task.CompletedTask,
            p => p is MachineViewModel { IsIdle: true } && !IsConnecting && (_context?.CanConnect ?? false));
        CancelConnectCommand = new RelayCommand(() => _connectCts?.Cancel(), () => IsConnecting);
        ClearLogCommand = new RelayCommand(() => Log.Clear(), () => Log.Count > 0);
        SetThemeCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse(s, out ThemeMode mode))
            {
                ThemeManager.Apply(mode);
                OnPropertyChanged(nameof(ThemeAuto));
                OnPropertyChanged(nameof(ThemeLight));
                OnPropertyChanged(nameof(ThemeDark));
            }
        });
    }

    public ObservableCollection<MachineViewModel> Machines { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];

    public ICommand ReloadCommand { get; }
    public ICommand OpenConfigCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand CancelConnectCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand SetThemeCommand { get; }

    public string Version => App.Version;
    public string ConfigPath => AdminPaths.ConfigFile;

    public bool ThemeAuto => ThemeManager.Mode == ThemeMode.Auto;
    public bool ThemeLight => ThemeManager.Mode == ThemeMode.Light;
    public bool ThemeDark => ThemeManager.Mode == ThemeMode.Dark;

    public bool HasMachines => Machines.Count > 0;

    public string Summary
    {
        get
        {
            int ok = Machines.Count(m => m.Status is MachineStatus.Available or MachineStatus.InSession);
            int down = Machines.Count(m => m.Status == MachineStatus.Unavailable);
            int unknown = Machines.Count - ok - down;
            var parts = new List<string>();
            if (ok > 0) parts.Add($"{ok} disponíve{(ok == 1 ? "l" : "is")}");
            if (down > 0) parts.Add($"{down} indisponíve{(down == 1 ? "l" : "is")}");
            if (unknown > 0) parts.Add($"{unknown} sem verificação");
            return parts.Count == 0 ? "Nenhum computador" : string.Join(" · ", parts);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>Ready | Working | Done | Warn | Cancelled | Error (cor da linha de estado).</summary>
    public string StatusTag
    {
        get => _statusTag;
        private set => Set(ref _statusTag, value);
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (Set(ref _isConnecting, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? ConfigProblem
    {
        get => _configProblem;
        private set
        {
            if (Set(ref _configProblem, value))
                OnPropertyChanged(nameof(HasConfigProblem));
        }
    }

    public bool HasConfigProblem => ConfigProblem is not null;

    public string IdentityText
    {
        get => _identityText;
        private set => Set(ref _identityText, value);
    }

    /// <summary>Modo pedido ao conectar. "Controlar" depende do marco M5 no Agent.</summary>
    public bool ViewMode
    {
        get => !_controlMode;
        set
        {
            if (value && Set(ref _controlMode, false, nameof(ControlMode)))
                OnPropertyChanged();
        }
    }

    public bool ControlMode
    {
        get => _controlMode;
        set
        {
            if (value && Set(ref _controlMode, true))
                OnPropertyChanged(nameof(ViewMode));
        }
    }

    public bool HasOpenSessions => Application.Current.Windows.OfType<SessionWindow>().Any();

    public void Initialize()
    {
        Reload();
        if (Machines.Count > 0 && _context?.CanConnect == true)
            CheckAllCommand.Execute(null);
    }

    private void Reload()
    {
        _context = AdminContext.Load();
        Machines.Clear();
        foreach (var entry in _context.Config?.Machines ?? [])
        {
            var vm = new MachineViewModel(entry);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MachineViewModel.Status))
                    OnPropertyChanged(nameof(Summary));
            };
            Machines.Add(vm);
        }

        OnPropertyChanged(nameof(HasMachines));
        OnPropertyChanged(nameof(Summary));

        if (_context.Problems.Count > 0)
        {
            ConfigProblem = string.Join("\n", _context.Problems.Select(p => "• " + p));
            AddLog(LogLevel.Warn, $"Configuração com {_context.Problems.Count} pendência(s).");
        }
        else
        {
            ConfigProblem = null;
        }

        IdentityText = _context.Certificate is { } cert
            ? $"{_context.Config!.ClientId} · {CertificateFingerprint.Short(CertificateFingerprint.Sha256(cert))}"
            : "certificado não configurado";

        if (_context.CreatedTemplate)
            AddLog(LogLevel.Info, $"Arquivo de configuração criado em {AdminPaths.ConfigFile}.");
        AddLog(LogLevel.Dim, $"Catálogo carregado: {Machines.Count} computador(es).");
        SetStatus(_context.CanConnect ? "Pronto." : "Configure o administrador para conectar.", _context.CanConnect ? "Ready" : "Warn");
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CheckAllAsync()
    {
        SetStatus("Verificando computadores…", "Working");
        await Task.WhenAll(Machines.Where(m => m.IsIdle).Select(CheckAsync));
        SetStatus($"Verificação concluída: {Summary}.", Machines.Any(m => m.Status == MachineStatus.Unavailable) ? "Warn" : "Done");
    }

    /// <summary>
    /// Sondagem: abre e fecha uma conexão TCP com a porta do Agent pela rede Tailscale.
    /// Prova que o Agent está escutando; a identidade só é confirmada ao conectar (mTLS).
    /// </summary>
    private async Task CheckAsync(MachineViewModel machine)
    {
        machine.Status = MachineStatus.Checking;
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(machine.Entry.Host, machine.Entry.Port, cts.Token);
            machine.Latency = sw.Elapsed;
            machine.Status = MachineStatus.Available;
            machine.Detail = "O Agent respondeu na porta.";
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            machine.Latency = null;
            machine.Status = MachineStatus.Unavailable;
            machine.Detail = ex switch
            {
                OperationCanceledException => "Sem resposta em 4 s. O computador pode estar desligado ou fora do Tailscale.",
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "Porta fechada: o NexoDesk Agent não está em execução.",
                SocketException { SocketErrorCode: SocketError.HostNotFound } => "Nome não encontrado. Confira o MagicDNS.",
                SocketException se => $"Falha de rede ({se.SocketErrorCode}).",
                _ => ex.Message,
            };
            AddLog(LogLevel.Warn, $"{machine.DisplayName}: {machine.Detail}");
        }
        finally
        {
            machine.LastCheck = DateTime.Now;
        }
    }

    private async Task ConnectAsync(MachineViewModel machine)
    {
        if (_context is not { Config: { } config, Certificate: { } cert })
            return;

        SessionMode mode = ControlMode ? SessionMode.Control : SessionMode.View;
        _connectCts = new CancellationTokenSource();
        IsConnecting = true;
        machine.Status = MachineStatus.Connecting;
        SetStatus($"Conectando a {machine.DisplayName} ({machine.Address})… Se o acesso não assistido estiver desligado, quem estiver no computador precisa clicar em Permitir.", "Working");
        AddLog(LogLevel.Info, $"Conectando a {machine.DisplayName} em modo {(mode == SessionMode.View ? "Visualizar" : "Controlar")}.");
        Audit(AuditEventType.SessionRequested, machine, result: mode.ToString());

        try
        {
            AdminSession session = await AdminSession.ConnectAsync(new AdminConnectOptions
            {
                Host = machine.Entry.Host,
                Port = machine.Entry.Port,
                ClientCertificate = cert,
                ServerFingerprints = [machine.Entry.ServerCertificateSha256],
                ClientId = config.ClientId,
                AppVersion = App.Version,
                Mode = mode,
            }, _connectCts.Token);

            machine.Status = MachineStatus.InSession;
            SetStatus($"Conectado a {machine.DisplayName} ({session.NegotiatedProtocol}).", "Done");
            AddLog(LogLevel.Ok, $"{machine.DisplayName}: sessão {session.Accepted.SessionId} aceita · {session.NegotiatedProtocol}.");
            Audit(AuditEventType.SessionStarted, machine, sessionId: session.Accepted.SessionId);

            var window = new SessionWindow(new SessionViewModel(session, machine, this))
            {
                Owner = null,
            };
            window.Show();
        }
        catch (OperationCanceledException)
        {
            machine.Status = MachineStatus.Unknown;
            SetStatus("Conexão cancelada.", "Cancelled");
            AddLog(LogLevel.Warn, $"{machine.DisplayName}: conexão cancelada por você.");
        }
        catch (ChannelAuthenticationException ex)
        {
            machine.Status = MachineStatus.Unavailable;
            string extra = ex.RemoteFingerprint is { } fp && fp != CertificateFingerprint.Normalize(machine.Entry.ServerCertificateSha256)
                ? $" O computador apresentou o certificado {CertificateFingerprint.Short(fp)}…, diferente do cadastrado. Se ele foi reinstalado, atualize serverCertificateSha256; se não, não conecte."
                : "";
            Fail(machine, ex.Message + extra);
            Audit(AuditEventType.AuthenticationFailed, machine, reason: ex.Message);
        }
        catch (SessionDeniedException ex)
        {
            machine.Status = MachineStatus.Available;
            Fail(machine, $"Acesso recusado: {ex.Message}", tag: "Warn");
            Audit(AuditEventType.SessionDenied, machine, result: ex.Reason.ToString(), reason: ex.Message);
        }
        catch (ProtocolException ex)
        {
            machine.Status = MachineStatus.Unknown;
            Fail(machine, ex.Message);
            Audit(AuditEventType.ProtocolError, machine, result: ex.Code.ToString(), reason: ex.Message);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            machine.Status = MachineStatus.Unavailable;
            Fail(machine, $"A conexão caiu durante a negociação: {ex.Message}");
        }
        finally
        {
            IsConnecting = false;
            _connectCts.Dispose();
            _connectCts = null;
        }
    }

    /// <summary>Chamado pela janela de sessão quando ela termina.</summary>
    internal void OnSessionEnded(MachineViewModel machine, string sessionId, string reason)
    {
        machine.Status = MachineStatus.Unknown;
        AddLog(LogLevel.Info, $"{machine.DisplayName}: {reason}");
        Audit(AuditEventType.SessionEnded, machine, sessionId: sessionId, reason: reason);
        SetStatus($"Sessão com {machine.DisplayName} encerrada.", "Ready");
    }

    internal Task ReconnectAsync(MachineViewModel machine) =>
        ConnectCommand.CanExecute(machine) ? ConnectAsync(machine) : Task.CompletedTask;

    private void Fail(MachineViewModel machine, string message, string tag = "Error")
    {
        machine.Detail = message;
        SetStatus(message, tag);
        AddLog(tag == "Error" ? LogLevel.Error : LogLevel.Warn, $"{machine.DisplayName}: {message}");
    }

    private void SetStatus(string text, string tag)
    {
        StatusText = text;
        StatusTag = tag;
    }

    public void AddLog(LogLevel level, string text)
    {
        void Add()
        {
            Log.Add(new LogLine(DateTime.Now, level, text));
            while (Log.Count > 300)
                Log.RemoveAt(0);
        }

        if (Application.Current.Dispatcher.CheckAccess())
            Add();
        else
            Application.Current.Dispatcher.InvokeAsync(Add);
    }

    private void Audit(AuditEventType type, MachineViewModel machine, string? sessionId = null, string? result = null, string? reason = null)
    {
        try
        {
            _audit.Write(new AuditEvent(DateTimeOffset.UtcNow, type, machine.Id,
                ClientName: _context?.Config?.ClientId, RemoteAddress: machine.Address, SessionId: sessionId,
                Result: result, Reason: reason, AppVersion: App.Version));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(LogLevel.Warn, "Não foi possível gravar o log de auditoria local.");
        }
    }

    private void OpenInShell(string path)
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
}
