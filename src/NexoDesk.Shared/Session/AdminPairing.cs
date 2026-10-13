using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Pairing;
using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Transport;

namespace NexoDesk.Shared.Session;

public sealed record AdminPairOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = ProtocolConstants.DefaultPort;
    public required X509Certificate2 ClientCertificate { get; init; }
    public required string ClientId { get; init; }

    /// <summary>Nome com que este ADMIN aparece no PC REMOTO.</summary>
    public required string ClientName { get; init; }

    /// <summary>Código mostrado no PC REMOTO (com ou sem hífens).</summary>
    public required string Code { get; init; }

    public required string AppVersion { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>PC REMOTO pareado: identidade fixada daqui em diante.</summary>
public sealed record PairedRemote(string ServerId, string DisplayName, string Fingerprint);

public sealed class PairingFailedException(string message) : Exception(message);

/// <summary>
/// Lado ADMIN do pareamento. Aceita o certificado ainda desconhecido do PC REMOTO só durante esta troca,
/// prova que conhece o código e exige a prova do PC REMOTO. Se alguém estiver no meio, as provas não batem.
/// </summary>
public static class AdminPairing
{
    public static async Task<PairedRemote> PairAsync(AdminPairOptions options, CancellationToken ct)
    {
        string code = PairingCode.Normalize(options.Code)
            ?? throw new PairingFailedException("Código inválido. Ele tem 12 letras e números, como K7QM-4XRT-9BWN.");

        var acceptForPairing = new PinnedCertificateValidator(_ => false, () => true, CertificateUsage.Server);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);

        await using SecureChannel channel = await TlsConnector.ConnectAsync(
            options.Host, options.Port, options.ClientCertificate, acceptForPairing, options.Timeout, timeout.Token).ConfigureAwait(false);

        var sm = new SessionStateMachine(SessionRole.Admin);
        sm.TransitionTo(SessionState.Connecting);
        sm.TransitionTo(SessionState.Authenticated);

        await channel.SendAsync(new HelloMessage(ProtocolConstants.CurrentVersion, options.ClientId, [], options.AppVersion), timeout.Token).ConfigureAwait(false);
        Envelope ackEnv = await channel.ReceiveAsync(options.Timeout, ErrorCode.HandshakeTimeout, timeout.Token).ConfigureAwait(false);
        sm.EnsureIncomingAllowed(ackEnv.Type);
        if (ackEnv.Type == MessageType.Error)
            throw new PairingFailedException(ProtocolJson.Deserialize<ErrorMessage>(ackEnv).Code.Describe());
        ProtocolJson.Deserialize<HelloAckMessage>(ackEnv);
        sm.TransitionTo(SessionState.Negotiating);

        // PBKDF2 é propositalmente lento: roda fora da thread chamadora.
        string adminFp = channel.LocalFingerprint;
        string remoteFp = channel.RemoteFingerprint;
        string proof = await Task.Run(() => PairingProof.ForAdmin(code, adminFp, remoteFp), timeout.Token).ConfigureAwait(false);
        await channel.SendAsync(new PairRequestMessage(options.ClientName, proof), timeout.Token).ConfigureAwait(false);

        Envelope resultEnv = await channel.ReceiveAsync(options.Timeout, ErrorCode.HandshakeTimeout, timeout.Token).ConfigureAwait(false);
        sm.EnsureIncomingAllowed(resultEnv.Type);
        if (resultEnv.Type == MessageType.Error)
            throw new PairingFailedException(ProtocolJson.Deserialize<ErrorMessage>(resultEnv).Code.Describe());
        PairResultMessage result = ProtocolJson.Deserialize<PairResultMessage>(resultEnv);

        if (!result.Accepted)
            throw new PairingFailedException(result.Message);

        string expected = await Task.Run(() => PairingProof.ForRemote(code, adminFp, remoteFp), timeout.Token).ConfigureAwait(false);
        if (!PairingProof.Matches(expected, result.Proof))
            throw new PairingFailedException("O PC REMOTO não comprovou o código. Pode haver algo interceptando a conexão: não use este pareamento.");

        await channel.TrySendAsync(new EndSessionMessage(EndReason.UserRequested), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        return new PairedRemote(result.ServerId, result.DisplayName, remoteFp);
    }
}
