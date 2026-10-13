using System.Net.Sockets;
using System.Windows.Input;
using NexoDesk.App.Services;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Pairing;
using NexoDesk.Shared.Session;
using NexoDesk.Shared.Transport;
using NexoDesk.Ui;

namespace NexoDesk.App.ViewModels;

/// <summary>Diálogo "Adicionar PC REMOTO": endereço + código mostrado no PC REMOTO.</summary>
public sealed class AddComputerViewModel : ObservableObject
{
    private readonly AppState _state;
    private CancellationTokenSource? _cts;
    private string _address = "";
    private string _code = "";
    private string _name = "";
    private string _statusText = "No PC REMOTO, abra o NexoDesk e clique em Gerar código de pareamento.";
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

    public string Address
    {
        get => _address;
        set
        {
            if (Set(ref _address, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Code
    {
        get => _code;
        set
        {
            if (Set(ref _code, value))
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

    private bool CanPair =>
        Uri.CheckHostName(Address.Trim()) != UriHostNameType.Unknown && PairingCode.Normalize(Code) is not null;

    private async Task PairAsync()
    {
        string host = Address.Trim();
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
                ClientCertificate = _state.Certificate,
                ClientId = _state.Config.DeviceId,
                ClientName = _state.Config.DeviceName,
                Code = Code,
                AppVersion = AppInfo.Version,
            }, _cts.Token);

            string displayName = string.IsNullOrWhiteSpace(Name) ? paired.DisplayName : Name.Trim();
            var computer = new PairedComputer
            {
                Id = UniqueId(paired.ServerId),
                Name = displayName,
                Host = host,
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

    private string UniqueId(string baseId)
    {
        string id = AppConfig.MakeId(baseId);
        string candidate = id;
        int n = 2;
        while (_state.Config.Admin.Computers.Any(c => c.Id == candidate && !string.Equals(c.Host, Address.Trim(), StringComparison.OrdinalIgnoreCase)))
            candidate = $"{id}-{n++}";
        return candidate;
    }

    private void Show(string text, string tag)
    {
        StatusText = text;
        StatusTag = tag;
    }
}
