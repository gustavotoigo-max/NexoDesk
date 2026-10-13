using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using NexoDesk.App.Services;
using NexoDesk.App.Views;
using NexoDesk.Shared.Audit;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;
using NexoDesk.Shared.Transport;
using NexoDesk.Ui;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.ViewModels;

/// <summary>Modo ADMIN: lista dos PCs REMOTOS pareados, pareamento de novos, conexão e sessões.</summary>
public sealed class AdminViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IAuditLog _audit;
    private CancellationTokenSource? _connectCts;
    private string _statusText = "Pronto.";
    private string _statusTag = "Ready";
    private bool _isConnecting;
    private bool _controlMode = true;

    public AdminViewModel(AppState state)
    {
        _state = state;
        try
        {
            _audit = new JsonLinesAuditLog(AppPaths.LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _audit = new MemoryAuditLog();
        }

        AddComputerCommand = new RelayCommand(AddComputer, () => Machines.Count < AppConfig.MaxComputers && !IsConnecting);
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => Machines.Count > 0 && !IsConnecting);
        CheckCommand = new AsyncRelayCommand(p => p is MachineViewModel m ? CheckAsync(m) : Task.CompletedTask,
            p => p is MachineViewModel { IsIdle: true });
        ConnectCommand = new AsyncRelayCommand(p => p is MachineViewModel m ? ConnectAsync(m) : Task.CompletedTask,
            p => p is MachineViewModel { IsIdle: true } && !IsConnecting);
        RemoveCommand = new RelayCommand(p => { if (p is MachineViewModel m) Remove(m); }, p => p is MachineViewModel { IsIdle: true });
        CancelConnectCommand = new RelayCommand(() => _connectCts?.Cancel(), () => IsConnecting);
        ClearLogCommand = new RelayCommand(() => Log.Clear(), () => Log.Count > 0);

        LoadMachines();
    }

    public ObservableCollection<MachineViewModel> Machines { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];

    public ICommand AddComputerCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CancelConnectCommand { get; }
    public ICommand ClearLogCommand { get; }

    public bool HasMachines => Machines.Count > 0;
    public string CountText => $"{Machines.Count} de {AppConfig.MaxComputers}";

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
            return parts.Count == 0 ? "Nenhum PC REMOTO" : string.Join(" · ", parts);
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Ready | Working | Done | Warn | Cancelled | Error.</summary>
    public string StatusTag { get => _statusTag; private set => Set(ref _statusTag, value); }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (Set(ref _isConnecting, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

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
        if (_state.IdentityRecreated && HasMachines)
            AddLog(LogLevel.Warn, "A identidade deste computador foi recriada. Pareie de novo com cada PC REMOTO.");
        if (HasMachines)
            CheckAllCommand.Execute(null);
    }

    private void LoadMachines()
    {
        Machines.Clear();
        foreach (PairedComputer c in _state.Config.Admin.Computers)
        {
            var vm = new MachineViewModel(c);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MachineViewModel.Status))
                    OnPropertyChanged(nameof(Summary));
            };
            Machines.Add(vm);
        }
        OnPropertyChanged(nameof(HasMachines));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CountText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void AddComputer()
    {
        var vm = new AddComputerViewModel(_state);
        var window = new AddComputerWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (vm.Result is not { } paired)
            return;

        AddLog(LogLevel.Ok, $"PC REMOTO \"{paired.Name}\" pareado ({CertificateFingerprint.Short(paired.Sha256)}…).");
        _audit.Write(new AuditEvent(DateTimeOffset.UtcNow, AuditEventType.Paired, paired.Id, ClientName: _state.Config.DeviceName,
            ClientFingerprint: paired.Sha256, RemoteAddress: paired.Host, AppVersion: AppInfo.Version));
        LoadMachines();
        MachineViewModel? added = Machines.FirstOrDefault(m => m.Id == paired.Id);
        if (added is not null)
            _ = CheckAsync(added);
    }

    private void Remove(MachineViewModel machine)
    {
        bool confirm = NxDialogWindow.Ask(Application.Current.MainWindow, new DialogOptions
        {
            Kind = DialogKind.Danger,
            Title = $"Remover \"{machine.DisplayName}\"?",
            Text = "Ele sai da sua lista. Para acessá-lo de novo, será preciso parear com um novo código. " +
                   "Para cortar o acesso também do lado de lá, remova este ADMIN na tela do PC REMOTO.",
            PrimaryText = "Remover",
            SecondaryText = "Cancelar",
            PrimaryIsDanger = true,
        });
        if (!confirm)
            return;
        _state.Update(c => c.Admin.Computers.RemoveAll(x => x.Id == machine.Id));
        AddLog(LogLevel.Warn, $"PC REMOTO \"{machine.DisplayName}\" removido da lista.");
        LoadMachines();
    }

    private async Task CheckAllAsync()
    {
        SetStatus("Verificando os PCs REMOTOS…", "Working");
        await Task.WhenAll(Machines.Where(m => m.IsIdle).Select(CheckAsync));
        SetStatus($"Verificação concluída: {Summary}.", Machines.Any(m => m.Status == MachineStatus.Unavailable) ? "Warn" : "Done");
    }

    /// <summary>Sondagem TCP na porta do PC REMOTO pela rede Tailscale. A identidade só é conferida ao conectar.</summary>
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
            machine.Detail = "O NexoDesk respondeu no PC REMOTO.";
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            machine.Latency = null;
            machine.Status = MachineStatus.Unavailable;
            machine.Detail = ex switch
            {
                OperationCanceledException => "Sem resposta. O PC REMOTO pode estar desligado ou fora do Tailscale.",
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "O NexoDesk não está aberto no PC REMOTO.",
                SocketException { SocketErrorCode: SocketError.HostNotFound } => "Endereço não encontrado. Confira o nome no Tailscale.",
                SocketException se => $"Falha de rede ({se.SocketErrorCode}).",
                _ => ex.Message,
            };
        }
        finally
        {
            machine.LastCheck = DateTime.Now;
        }
    }

    private async Task ConnectAsync(MachineViewModel machine)
    {
        SessionMode mode = ControlMode ? SessionMode.Control : SessionMode.View;
        _connectCts = new CancellationTokenSource();
        IsConnecting = true;
        machine.Status = MachineStatus.Connecting;
        SetStatus($"Conectando a {machine.DisplayName}… Se o PC REMOTO pedir confirmação, alguém lá precisa clicar em Permitir.", "Working");
        AddLog(LogLevel.Info, $"Conectando a {machine.DisplayName}.");

        try
        {
            AdminSession session = await AdminSession.ConnectAsync(new AdminConnectOptions
            {
                Host = machine.Entry.Host,
                Port = machine.Entry.Port,
                ClientCertificate = _state.Certificate,
                ServerFingerprints = [machine.Entry.Sha256],
                ClientId = _state.Config.DeviceId,
                AppVersion = AppInfo.Version,
                Mode = mode,
            }, _connectCts.Token);

            machine.Status = MachineStatus.InSession;
            SetStatus($"Conectado a {machine.DisplayName}.", "Done");
            AddLog(LogLevel.Ok, $"{machine.DisplayName}: acesso aceito · {session.NegotiatedProtocol}.");
            Audit(AuditEventType.SessionStarted, machine, sessionId: session.Accepted.SessionId);
            new SessionWindow(new SessionViewModel(session, machine, this)).Show();
        }
        catch (OperationCanceledException)
        {
            machine.Status = MachineStatus.Unknown;
            SetStatus("Conexão cancelada.", "Cancelled");
        }
        catch (ChannelAuthenticationException ex)
        {
            machine.Status = MachineStatus.Unavailable;
            string message = ex.RemoteFingerprint is { } fp && fp != CertificateFingerprint.Normalize(machine.Entry.Sha256)
                ? "A identidade do PC REMOTO mudou desde o pareamento. Se ele foi reinstalado, remova-o e pareie de novo. Se não, não conecte."
                : ex.Message;
            Fail(machine, message);
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
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            machine.Status = MachineStatus.Unavailable;
            Fail(machine, $"A conexão caiu: {ex.Message}");
        }
        finally
        {
            IsConnecting = false;
            _connectCts.Dispose();
            _connectCts = null;
        }
    }

    internal void OnSessionEnded(MachineViewModel machine, string sessionId, string reason)
    {
        machine.Status = MachineStatus.Unknown;
        AddLog(LogLevel.Info, $"{machine.DisplayName}: {reason}");
        Audit(AuditEventType.SessionEnded, machine, sessionId: sessionId, reason: reason);
        SetStatus($"Acesso a {machine.DisplayName} encerrado.", "Ready");
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
                ClientName: _state.Config.DeviceName, RemoteAddress: machine.Address, SessionId: sessionId,
                Result: result, Reason: reason, AppVersion: AppInfo.Version));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(LogLevel.Warn, "Não foi possível gravar o registro local.");
        }
    }
}
