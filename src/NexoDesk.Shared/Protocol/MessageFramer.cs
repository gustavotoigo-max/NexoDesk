namespace NexoDesk.Shared.Protocol;

/// <summary>
/// Leitura e escrita de mensagens delimitadas sobre um fluxo de bytes (TCP/TLS).
/// TCP não preserva limites: cada leitura usa ReadExactlyAsync e o comprimento é validado antes de alocar.
/// </summary>
public static class MessageFramer
{
    public static async Task WriteAsync(
        Stream stream, MessageType type, uint sequence, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!type.IsDefined())
            throw new ArgumentOutOfRangeException(nameof(type));
        if (payload.Length > type.MaxPayload())
            throw new ProtocolException(ErrorCode.PayloadTooLarge, $"envio de {type} com {payload.Length} bytes");

        // Cabeçalho e payload pequenos vão num único buffer para reduzir gravações TLS.
        if (payload.Length <= 4096)
        {
            byte[] buffer = new byte[ProtocolConstants.HeaderSize + payload.Length];
            new MessageHeader(ProtocolConstants.CurrentVersion, type, (uint)payload.Length, sequence).WriteTo(buffer);
            payload.Span.CopyTo(buffer.AsSpan(ProtocolConstants.HeaderSize));
            await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        else
        {
            byte[] header = new byte[ProtocolConstants.HeaderSize];
            new MessageHeader(ProtocolConstants.CurrentVersion, type, (uint)payload.Length, sequence).WriteTo(header);
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lê a próxima mensagem. Retorna null se o outro lado fechou a conexão de forma limpa
    /// exatamente na fronteira entre mensagens. Fechamento no meio de uma mensagem gera ProtocolException.
    /// </summary>
    public static async Task<Envelope?> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] headerBytes = new byte[ProtocolConstants.HeaderSize];

        int first = await stream.ReadAsync(headerBytes.AsMemory(0, ProtocolConstants.HeaderSize), ct).ConfigureAwait(false);
        if (first == 0)
            return null;

        await ReadExactlyOrThrowAsync(stream, headerBytes.AsMemory(first), ct).ConfigureAwait(false);
        MessageHeader header = MessageHeader.Parse(headerBytes);

        byte[] payload = header.Length == 0 ? [] : new byte[header.Length];
        if (payload.Length > 0)
            await ReadExactlyOrThrowAsync(stream, payload, ct).ConfigureAwait(false);

        return new Envelope(header, payload);
    }

    private static async Task ReadExactlyOrThrowAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length == 0)
            return;
        try
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new ProtocolException(ErrorCode.MalformedPayload, "conexão encerrada no meio de uma mensagem");
        }
    }
}
