using System.Net.Sockets;
using System.Windows.Input;
using NexoDesk.App.Services;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Pairing;
using NexoDesk.Shared.Session;
using NexoDesk.Shared.Transport;
using NexoDesk.Ui;

namespace NexoDesk.App.ViewModels;

/// <summary>Diálogo "Adicionar PC REMOTO": um único código de acesso (já leva o endereço) colado do PC REMOTO.</summary>
public sealed class AddComputerViewModel : ObservableObject
{
    private readonly AppState _state;
    private CancellationTokenSource? _cts;
    private string _accessCode = "";
    private string _name = "";
    private string _statusText = "No PC REMOTO, clique em Gerar código de acesso e cole o código aqui.";
    private string _statusTag = "Ready";
    private bool _isBusy;

    public AddComputerViewModel(AppState state)
    {
        _state = state;
        PairCommand = new AsyncRelayCommand(PairAsync, () => !IsBusy && CanPair);
        CancelCommand = new RelayCommand(() =>
        {
            if (IsBusy)
                _cts?.Cancel();
            else
                CloseRequested?.Invoke();
        });
    }

    public ICommand PairCommand { get; }
    public ICommand CancelCommand { get; }
    public event Action? CloseRequested;

    /// <summary>Preenchido quando o pareamento dá certo.</summary>
    public PairedComputer? Result { get; private set; }

    /// <summary>Código único gerado no PC REMOTO (embute endereço, porta e código de pareamento).</summary>
    public string AccessCode
    {
        get => _accessCode;
        set
        {
            if (Set(ref _accessCode, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Opcional: se vazio, usa o nome que o PC REMOTO informar.</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StatusTag { get => _statusTag; private set => Set(ref _statusTag, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CancelText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string CancelText => IsBusy ? "Parar" : "Cancelar";

    private bool CanPair => NexoDesk.Shared.Pairing.AccessCode.IsValid(AccessCode);

    private async Task PairAsync()
    {
        if (!NexoDesk.Shared.Pairing.AccessCode.TryParse(AccessCode, out string host, out int port, out string code))
        {
            Show("Código de acesso inválido. Copie de novo no PC REMOTO.", "Error");
            return;
        }
        if (_state.Config.Admin.Computers.Count >= AppConfig.MaxComputers)
        {
            Show($"O limite é de {AppConfig.MaxComputers} PCs REMOTOS. Remova um antes de adicionar outro.", "Error");
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        Show("Conectando e conferindo o código…", "Working");
        try
        {
            PairedRemote paired = await AdminPairing.PairAsync(new AdminPairOptions
            {
                Host = host,
                Port = port,
                ClientCertificate = _state.Certificate,
                ClientId = _state.Config.DeviceId,
                ClientName = _state.Config.DeviceName,
                Code = code,
                AppVersion = AppInfo.Version,
            }, _cts.Token);

            string displayName = string.IsNullOrWhiteSpace(Name) ? paired.DisplayName : Name.Trim();
            var computer = new PairedComputer
            {
                Id = UniqueId(paired.ServerId, host),
                Name = displayName,
                Host = host,
                Port = port,
                Sha256 = paired.Fingerprint,
                PairedAt = DateTimeOffset.UtcNow,
            };
            _state.Update(c =>
            {
                // O mesmo PC pareado de novo substitui o anterior.
                c.Admin.Computers.RemoveAll(x => x.Sha256 == computer.Sha256 || string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
                c.Admin.Computers.Add(computer);
            });
            Result = computer;
            Show($"Pareado com \"{displayName}\".", "Done");
            CloseRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Show("Pareamento interrompido.", "Cancelled");
        }
        catch (PairingFailedException ex)
        {
            Show(ex.Message, "Error");
        }
        catch (ChannelAuthenticationException ex)
        {
            Show(ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                ? "O NexoDesk não está aberto no PC REMOTO (ou está em modo ADMIN)."
                : ex.Message.Contains("negociação segura", StringComparison.Ordinal)
                    ? "O PC REMOTO recusou a conexão. Confira se o código ainda está na tela dele (vale 10 minutos)."
                    : ex.Message, "Error");
        }
        catch (Exception ex) when (ex is NexoDesk.Shared.Protocol.ProtocolException or System.IO.IOException or SocketException)
        {
            Show($"A conexão caiu durante o pareamento: {ex.Message}", "Error");
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private string UniqueId(string baseId, string host)
    {
        string id = AppConfig.MakeId(baseId);
        string candidate = id;
        int n = 2;
        while (_state.Config.Admin.Computers.Any(c => c.Id == candidate && !string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{id}-{n++}";
        return candidate;
    }

    private void Show(string text, string tag)
    {
        StatusText = text;
        StatusTag = tag;
    }
}
