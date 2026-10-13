using System.Diagnostics;
using System.Net;
using System.Net.Security;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Transport;

/// <summary>
/// Canal autenticado (TLS mútuo) que envia e recebe mensagens delimitadas.
/// Envio serializado por trava; números de sequência crescentes por direção.
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    private readonly SslStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _sequence;
    private int _disposed;

    internal SecureChannel(SslStream stream, string localFingerprint, string remoteFingerprint, string remoteSubject, EndPoint? remoteEndPoint)
    {
        _stream = stream;
        LocalFingerprint = localFingerprint;
        RemoteFingerprint = remoteFingerprint;
        RemoteSubject = remoteSubject;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>Impressão do certificado deste lado (usada nas provas de pareamento).</summary>
    public string LocalFingerprint { get; }
    public string RemoteFingerprint { get; }
    public string RemoteSubject { get; }
    public EndPoint? RemoteEndPoint { get; }
    public string RemoteAddress => RemoteEndPoint switch
    {
        IPEndPoint ip => ip.Address.ToString(),
        null => "desconhecido",
        _ => RemoteEndPoint.ToString() ?? "desconhecido",
    };

    public string NegotiatedProtocol => _stream.SslProtocol.ToString();
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Task SendAsync<T>(T message, CancellationToken ct) where T : class, IControlMessage =>
        SendRawAsync(ProtocolJson.TypeOf<T>(), ProtocolJson.Serialize(message), ct);

    public async Task SendRawAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            uint seq = unchecked((uint)Interlocked.Increment(ref _sequence));
            await MessageFramer.WriteAsync(_stream, type, seq, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Recebe a próxima mensagem dentro do prazo. Prazo esgotado gera ProtocolException com o código informado.
    /// Fim de conexão limpo gera ProtocolException(ConnectionClosed).
    /// </summary>
    public async Task<Envelope> ReceiveAsync(TimeSpan timeout, ErrorCode timeoutCode, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            Envelope? envelope = await MessageFramer.ReadAsync(_stream, timeoutCts.Token).ConfigureAwait(false);
            return envelope ?? throw new ProtocolException(ErrorCode.ConnectionClosed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProtocolException(timeoutCode);
        }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProtocolException(ErrorCode.ConnectionClosed, ex.Message);
        }
    }

    /// <summary>Envio de melhor esforço durante encerramento: nunca lança.</summary>
    public async Task TrySendAsync<T>(T message, TimeSpan timeout) where T : class, IControlMessage
    {
        if (IsDisposed)
            return;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await SendAsync(message, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or ProtocolException or InvalidOperationException)
        {
            Debug.WriteLine($"Envio de encerramento ignorado: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _stream.ShutdownAsync().WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            // Conexão já caída; nada a fazer.
        }
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
