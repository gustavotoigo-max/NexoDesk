using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;
using NexoDesk.Ui;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.ViewModels;

/// <summary>
/// Janela de sessão: mostra o vídeo remoto (a partir do M3), latência, FPS medido e estado.
/// Frames atrasados são descartados: só o mais recente é decodificado.
/// </summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly AdminSession _session;
    private readonly AdminViewModel _main;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _fpsTimer;
    private VideoFrame? _pendingFrame;
    private int _decoding;
    private int _framesThisSecond;
    private ImageSource? _frame;
    private string _latencyText = "—";
    private string _fpsText = "0";
    private bool _ended;
    private string _endReason = "";
    private string _displayText = "aguardando";

    public SessionViewModel(AdminSession session, MachineViewModel machine, AdminViewModel main)
    {
        _session = session;
        _main = main;
        Machine = machine;
        _dispatcher = Application.Current.Dispatcher;

        _session.LatencyMeasured += l => _dispatcher.InvokeAsync(() => LatencyText = $"{l.TotalMilliseconds:0} ms");
        _session.DisplayChanged += d => _dispatcher.InvokeAsync(() => DisplayText = $"{d.Width} × {d.Height} · escala {d.DpiScale:0.##}");
        _session.FrameReceived += OnFrame;
        _session.Ended += reason => _dispatcher.InvokeAsync(() => OnEnded(reason));

        if (_session.Display is { } display)
            _displayText = $"{display.Width} × {display.Height} · escala {display.DpiScale:0.##}";

        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fpsTimer.Tick += (_, _) =>
        {
            FpsText = Interlocked.Exchange(ref _framesThisSecond, 0).ToString();
        };
        _fpsTimer.Start();

        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => !IsEnded);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync, () => IsEnded);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public MachineViewModel Machine { get; }
    public string Title => Machine.DisplayName;
    public string Tagline => Machine.Address;
    public string ServerId => _session.Server.ServerId;
    public string ServerName => _session.Server.DisplayName;
    public string SessionId => _session.Accepted.SessionId;
    public string Protocol => _session.NegotiatedProtocol.Replace("Tls", "TLS ").Replace("TLS 13", "TLS 1.3").Replace("TLS 12", "TLS 1.2");
    public string Fingerprint => CertificateFingerprint.Short(_session.ServerFingerprint);
    public string ModeText => _session.Accepted.Mode == SessionMode.Control ? "Controlar" : "Visualizar";
    public string AgentVersion => _session.Server.AppVersion;

    public ICommand DisconnectCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand CloseCommand { get; }

    /// <summary>Verdadeiro quando a sessão é de controle e está ativa (envia mouse/teclado).</summary>
    public bool CanControl => _session.CanControl && !IsEnded;

    public int RemoteWidth => _session.Display?.Width ?? 1920;
    public int RemoteHeight => _session.Display?.Height ?? 1080;

    /// <summary>Envia um evento de ponteiro (coordenadas já normalizadas 0..1). Não bloqueia a interface.</summary>
    public void SendPointer(double nx, double ny, PointerButton button, PointerAction action, int wheel = 0)
    {
        if (!CanControl)
            return;
        double x = Math.Clamp(nx, 0, 1);
        double y = Math.Clamp(ny, 0, 1);
        FireAndForget(_session.SendPointerAsync(new PointerEventMessage(x, y, button, action, wheel), CancellationToken.None));
    }

    public void SendKey(ushort scanCode, ushort virtualKey, bool isExtended, KeyAction action)
    {
        if (!CanControl)
            return;
        FireAndForget(_session.SendKeyAsync(new KeyEventMessage(scanCode, virtualKey, isExtended, action), CancellationToken.None));
    }

    // Envio de entrada é de melhor esforço: uma queda de conexão é tratada pela sessão, não deve estourar aqui.
    private static async void FireAndForget(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NexoDesk.Shared.Protocol.ProtocolException or InvalidOperationException or OperationCanceledException) { }
    }

    public event Action? CloseRequested;

    public ImageSource? Frame
    {
        get => _frame;
        private set
        {
            if (Set(ref _frame, value))
            {
                OnPropertyChanged(nameof(HasFrame));
                OnPropertyChanged(nameof(ShowWaiting));
            }
        }
    }

    public bool HasFrame => Frame is not null;

    /// <summary>Conectado e ainda sem imagem.</summary>
    public bool ShowWaiting => !HasFrame && !IsEnded;

    /// <summary>Conectado em "Só ver": mostra um aviso de que o mouse/teclado não comandam.</summary>
    public bool ViewOnly => !IsEnded && !_session.CanControl;

    public string LatencyText
    {
        get => _latencyText;
        private set => Set(ref _latencyText, value);
    }

    public string FpsText
    {
        get => _fpsText;
        private set => Set(ref _fpsText, value);
    }

    public string DisplayText
    {
        get => _displayText;
        private set => Set(ref _displayText, value);
    }

    public bool IsEnded
    {
        get => _ended;
        private set
        {
            if (Set(ref _ended, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateKind));
                OnPropertyChanged(nameof(ShowWaiting));
                OnPropertyChanged(nameof(ViewOnly));
                OnPropertyChanged(nameof(CanControl));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string EndReason
    {
        get => _endReason;
        private set => Set(ref _endReason, value);
    }

    public string StateText => IsEnded ? "Encerrada" : (_session.Accepted.Mode == SessionMode.Control ? "Controlando" : "Visualizando");
    public BadgeKind StateKind => IsEnded ? BadgeKind.Neutral : BadgeKind.Success;

    public Task DisconnectAsync() => _session.DisconnectAsync();

    private async Task ReconnectAsync()
    {
        CloseRequested?.Invoke();
        await _main.ReconnectAsync(Machine);
    }

    private void OnFrame(VideoFrame frame)
    {
        // Guarda só o mais recente; se já há decodificação em andamento, o anterior é descartado.
        Interlocked.Exchange(ref _pendingFrame, frame);
        if (Interlocked.CompareExchange(ref _decoding, 1, 0) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _pendingFrame, null) is { } next)
                {
                    BitmapImage image = Decode(next);
                    _dispatcher.InvokeAsync(() =>
                    {
                        Frame = image;
                        Interlocked.Increment(ref _framesThisSecond);
                    }, DispatcherPriority.Render);
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
            {
                _main.AddLog(LogLevel.Warn, $"{Machine.DisplayName}: frame inválido descartado.");
            }
            finally
            {
                Volatile.Write(ref _decoding, 0);
            }
        });
    }

    private static BitmapImage Decode(VideoFrame frame)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = new MemoryStream(frame.Jpeg.ToArray(), writable: false);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnEnded(string reason)
    {
        _fpsTimer.Stop();
        FpsText = "0";
        EndReason = reason;
        IsEnded = true;
        _main.OnSessionEnded(Machine, SessionId, reason);
    }
}
