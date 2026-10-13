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

    // Bomba de entrada: movimentos do mouse chegam muito rápido (a cada poucos ms). Enviar
    // um por um entope o canal e o cursor remoto fica "arrastando" posições velhas. Aqui os
    // movimentos são coalescidos (só o mais recente fica na fila) e há no máximo um envio em
    // andamento; cliques, roda e teclas nunca são descartados e mantêm a ordem — cada clique
    // já carrega a própria coordenada, então largar movimentos intermediários não erra o alvo.
    private readonly object _inputLock = new();
    private readonly Queue<Func<CancellationToken, Task>> _inputEvents = new();
    private readonly SemaphoreSlim _inputSignal = new(0);
    private readonly CancellationTokenSource _inputCts = new();
    private PointerEventMessage? _pendingMove;
    private bool _pumpIdle = true;

    private VideoFrame? _pendingFrame;
    private int _decoding;
    private int _framesThisSecond;
    private ImageSource? _frame;
    private string _latencyText = "—";
    private string _fpsText = "0";
    private bool _ended;
    private string _endReason = "";
    private string _displayText = "aguardando";
    private double _cursorX;
    private double _cursorY;
    private bool _cursorVisible;

    public SessionViewModel(AdminSession session, MachineViewModel machine, AdminViewModel main)
    {
        _session = session;
        _main = main;
        Machine = machine;
        _dispatcher = Application.Current.Dispatcher;

        _session.LatencyMeasured += l => _dispatcher.InvokeAsync(() => LatencyText = $"{l.TotalMilliseconds:0} ms");
        _session.DisplayChanged += d => _dispatcher.InvokeAsync(() => DisplayText = $"{d.Width} × {d.Height} · escala {d.DpiScale:0.##}");
        _session.FrameReceived += OnFrame;
        _session.TilesReceived += OnTiles;
        _session.H264Received += OnH264;
        _session.CursorMoved += OnCursor;
        _session.Ended += reason => _dispatcher.InvokeAsync(() => OnEnded(reason));
        _main.ShowRemotePointerChanged += () => _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(ShowRemotePointer));
            OnPropertyChanged(nameof(ShowCursorOverlay));
            CursorChanged?.Invoke();
        });

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

        _ = Task.Run(() => InputPumpAsync(_inputCts.Token));
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
        var msg = new PointerEventMessage(x, y, button, action, wheel);

        if (action == PointerAction.Move)
            EnqueueMove(msg);
        else
            Enqueue(ct => _session.SendPointerAsync(msg, ct));
    }

    public void SendKey(ushort scanCode, ushort virtualKey, bool isExtended, KeyAction action)
    {
        if (!CanControl)
            return;
        var msg = new KeyEventMessage(scanCode, virtualKey, isExtended, action);
        Enqueue(ct => _session.SendKeyAsync(msg, ct));
    }

    /// <summary>Guarda só o movimento mais recente; os anteriores ainda não enviados são descartados.</summary>
    private void EnqueueMove(PointerEventMessage move)
    {
        bool wake;
        lock (_inputLock)
        {
            _pendingMove = move;
            wake = _pumpIdle;
            if (wake)
                _pumpIdle = false;
        }
        if (wake)
            _inputSignal.Release();
    }

    /// <summary>Eventos discretos (clique, roda, tecla) entram em ordem e nunca são descartados.</summary>
    private void Enqueue(Func<CancellationToken, Task> job)
    {
        bool wake;
        lock (_inputLock)
        {
            _inputEvents.Enqueue(job);
            wake = _pumpIdle;
            if (wake)
                _pumpIdle = false;
        }
        if (wake)
            _inputSignal.Release();
    }

    private async Task InputPumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _inputSignal.WaitAsync(ct).ConfigureAwait(false);
                while (true)
                {
                    Func<CancellationToken, Task>? job;
                    lock (_inputLock)
                    {
                        if (_inputEvents.Count > 0)
                        {
                            job = _inputEvents.Dequeue();
                        }
                        else if (_pendingMove is { } move)
                        {
                            _pendingMove = null;
                            job = c => _session.SendPointerAsync(move, c);
                        }
                        else
                        {
                            _pumpIdle = true; // nada pendente: dorme até o próximo sinal.
                            break;
                        }
                    }
                    try { await job(ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or NexoDesk.Shared.Protocol.ProtocolException or InvalidOperationException or OperationCanceledException) { }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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
                OnPropertyChanged(nameof(ShowCursorOverlay));
            }
        }
    }

    public bool HasFrame => Frame is not null;

    /// <summary>Conectado e ainda sem imagem.</summary>
    public bool ShowWaiting => !HasFrame && !IsEnded;

    // --- Ponteiro do PC REMOTO (desenhado por cima da imagem; vem à parte dos frames) ---

    /// <summary>Posição X do cursor remoto, normalizada 0..1.</summary>
    public double CursorX => _cursorX;
    /// <summary>Posição Y do cursor remoto, normalizada 0..1.</summary>
    public double CursorY => _cursorY;

    /// <summary>Preferência do ADMIN de mostrar o ponteiro remoto.</summary>
    public bool ShowRemotePointer => _main.ShowRemotePointer;

    /// <summary>Desenhar o ponteiro agora: ligado na preferência, visível na tela remota e com imagem.</summary>
    public bool ShowCursorOverlay => ShowRemotePointer && _cursorVisible && HasFrame && !IsEnded;

    /// <summary>Disparado quando a posição/visibilidade do ponteiro muda (a janela reposiciona o overlay).</summary>
    public event Action? CursorChanged;

    private void OnCursor(CursorUpdateMessage c) => _dispatcher.InvokeAsync(() =>
    {
        _cursorX = c.X;
        _cursorY = c.Y;
        if (_cursorVisible != c.Visible)
        {
            _cursorVisible = c.Visible;
            OnPropertyChanged(nameof(ShowCursorOverlay));
        }
        CursorChanged?.Invoke();
    });

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
                OnPropertyChanged(nameof(ShowCursorOverlay));
                CursorChanged?.Invoke();
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

    private WriteableBitmap? _canvas;
    private NexoDesk.Codec.H264Decoder? _h264Decoder;

    /// <summary>Recebe um quadro H.264: decodifica (nesta thread) e desenha a imagem na thread de UI.</summary>
    private void OnH264(H264Frame frame)
    {
        if (_ended)
            return;
        try
        {
            _h264Decoder ??= NexoDesk.Codec.H264Decoder.TryCreate(frame.Width, frame.Height);
            if (_h264Decoder is null || !_h264Decoder.Decode(frame, out byte[] bgra, out int w, out int h))
                return;

            byte[] copy = (byte[])bgra.Clone(); // o buffer do decoder é reutilizado; copia p/ a UI
            int stride = w * 4;
            _dispatcher.InvokeAsync(() =>
            {
                if (_canvas is null || _canvas.PixelWidth != w || _canvas.PixelHeight != h)
                {
                    _canvas = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                    Frame = _canvas;
                }
                _canvas.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), copy, stride, 0, 0);
                if (!ReferenceEquals(Frame, _canvas))
                    Frame = _canvas;
                Interlocked.Increment(ref _framesThisSecond);
            }, DispatcherPriority.Render);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Recebe um quadro por regiões: decodifica cada bloco (nesta thread) e cola na imagem persistente
    /// na thread de interface. Só os retângulos que mudaram são atualizados — pouco dado, baixa latência.
    /// </summary>
    private void OnTiles(TiledFrame tf)
    {
        var writes = new List<(int X, int Y, int W, int H, byte[] Px, int Stride)>(tf.Tiles.Count);
        foreach (ScreenTile t in tf.Tiles)
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                img.StreamSource = new MemoryStream(t.Jpeg.ToArray(), writable: false);
                img.EndInit();
                var bgr = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
                int stride = t.Width * 4;
                byte[] px = new byte[stride * t.Height];
                bgr.CopyPixels(px, stride, 0);
                writes.Add((t.X, t.Y, t.Width, t.Height, px, stride));
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or OverflowException)
            {
                // Bloco inválido: ignora; a próxima mudança dele reenvia.
            }
        }

        if (writes.Count == 0)
            return;

        _dispatcher.InvokeAsync(() =>
        {
            if (_canvas is null || _canvas.PixelWidth != tf.ScreenWidth || _canvas.PixelHeight != tf.ScreenHeight)
            {
                _canvas = new WriteableBitmap(tf.ScreenWidth, tf.ScreenHeight, 96, 96, PixelFormats.Bgr32, null);
                Frame = _canvas;
            }
            foreach ((int x, int y, int w, int h, byte[] pxData, int stride) in writes)
                _canvas.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pxData, stride, x, y);
            if (!ReferenceEquals(Frame, _canvas))
                Frame = _canvas;
            Interlocked.Increment(ref _framesThisSecond);
        }, DispatcherPriority.Render);
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
        try { _inputCts.Cancel(); } catch (ObjectDisposedException) { }
        _h264Decoder?.Dispose();
        _h264Decoder = null;
        _main.OnSessionEnded(Machine, SessionId, reason);
    }
}
