using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Security;

namespace NexoDesk.Shared.Transport;

/// <summary>Falha ao estabelecer o canal seguro, com motivo em português seguro para exibir e registrar.</summary>
public sealed class ChannelAuthenticationException : Exception
{
    public ChannelAuthenticationException(string reason, string? remoteFingerprint, Exception? inner = null)
        : base(reason, inner)
    {
        RemoteFingerprint = remoteFingerprint;
    }

    public string? RemoteFingerprint { get; }
}

/// <summary>Abre e aceita conexões TCP com TLS mútuo (TLS 1.2/1.3) e validação por pinning nos dois lados.</summary>
public static class TlsConnector
{
    private const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>Lado Admin: conecta ao Agent, apresenta o certificado de cliente e exige o certificado fixado do Agent.</summary>
    public static async Task<SecureChannel> ConnectAsync(
        string host,
        int port,
        X509Certificate2 clientCertificate,
        PinnedCertificateValidator serverValidator,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        if (!clientCertificate.HasPrivateKey)
            throw new ArgumentException("O certificado de cliente precisa da chave privada.", nameof(clientCertificate));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var tcp = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        CertificateCheckResult? check = null;
        try
        {
            await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            var options = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = serverValidator.CreateCallback(r => check = r),
                // Sempre apresenta o certificado deste Admin, mesmo que o servidor não anuncie emissores conhecidos.
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate,
            };

            await ssl.AuthenticateAsClientAsync(options, timeoutCts.Token).ConfigureAwait(false);
            return new SecureChannel(ssl, CertificateFingerprint.Sha256(clientCertificate), check!.Fingerprint!, check.Subject ?? "", tcp.Client.RemoteEndPoint);
        }
        catch (Exception ex)
        {
            if (ssl is not null)
                await ssl.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();

            if (ct.IsCancellationRequested)
                throw;
            throw Translate(ex, check, timeoutCts.IsCancellationRequested, server: true);
        }
    }

    /// <summary>Lado Agent: conclui o TLS de uma conexão aceita exigindo certificado de cliente fixado.</summary>
    public static async Task<SecureChannel> AcceptAsync(
        TcpClient client,
        X509Certificate2 serverCertificate,
        PinnedCertificateValidator clientValidator,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        if (!serverCertificate.HasPrivateKey)
            throw new ArgumentException("O certificado do servidor precisa da chave privada.", nameof(serverCertificate));

        client.NoDelay = true;
        EndPoint? remote = client.Client.RemoteEndPoint;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        CertificateCheckResult? check = null;
        try
        {
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(serverCertificate, additionalCertificates: null, offline: true),
                ClientCertificateRequired = true,
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = clientValidator.CreateCallback(r => check = r),
            };

            await ssl.AuthenticateAsServerAsync(options, timeoutCts.Token).ConfigureAwait(false);
            return new SecureChannel(ssl, CertificateFingerprint.Sha256(serverCertificate), check!.Fingerprint!, check.Subject ?? "", remote);
        }
        catch (Exception ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            client.Dispose();

            if (ct.IsCancellationRequested)
                throw;
            throw Translate(ex, check, timeoutCts.IsCancellationRequested, server: false);
        }
    }

    private static ChannelAuthenticationException Translate(Exception ex, CertificateCheckResult? check, bool timedOut, bool server)
    {
        string who = server ? "do PC REMOTO" : "do ADMIN";
        if (check is { Accepted: false })
            return new ChannelAuthenticationException($"Certificado {who} recusado: {check.Reason}", check.Fingerprint, ex);
        if (timedOut)
            return new ChannelAuthenticationException("Tempo esgotado ao estabelecer a conexão segura.", check?.Fingerprint, ex);

        return ex switch
        {
            SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused =>
                new ChannelAuthenticationException("Conexão recusada: o NexoDesk não está aberto no PC REMOTO (ou está em modo ADMIN).", null, ex),
            SocketException se when se.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData =>
                new ChannelAuthenticationException("Endereço não encontrado. Confira o nome MagicDNS ou o IP Tailscale.", null, ex),
            SocketException se =>
                new ChannelAuthenticationException($"Falha de rede ({se.SocketErrorCode}). Verifique se o Tailscale está conectado nos dois computadores.", null, ex),
            AuthenticationException =>
                new ChannelAuthenticationException(server
                    ? "O PC REMOTO não reconhece este ADMIN. Ele pode ter sido removido lá: pareie de novo."
                    : "Falha na autenticação com o ADMIN.", check?.Fingerprint, ex),
            IOException =>
                new ChannelAuthenticationException("A conexão foi encerrada durante a negociação segura. O PC REMOTO pode não reconhecer este ADMIN (pareie de novo) ou o código de pareamento expirou.", check?.Fingerprint, ex),
            _ => new ChannelAuthenticationException($"Falha ao conectar: {ex.Message}", check?.Fingerprint, ex),
        };
    }
}
