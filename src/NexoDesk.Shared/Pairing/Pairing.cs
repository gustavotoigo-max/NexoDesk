using System.Security.Cryptography;
using System.Text;

namespace NexoDesk.Shared.Pairing;

/// <summary>
/// Código de pareamento exibido no PC REMOTO: 12 caracteres (60 bits) em 3 grupos, ex.: K7QM-4XRT-9BWN.
/// Alfabeto sem caracteres confundíveis (sem I, O, 0, 1).
/// </summary>
public static class PairingCode
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 12;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (int i = 0; i < Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Format(new string(chars));
    }

    /// <summary>Formata em grupos de 4 separados por hífen.</summary>
    public static string Format(string normalized) =>
        normalized.Length == Length ? $"{normalized[..4]}-{normalized[4..8]}-{normalized[8..]}" : normalized;

    /// <summary>Remove hífens e espaços e põe em maiúsculas. Retorna null se tiver caractere fora do alfabeto ou tamanho errado.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var sb = new StringBuilder(Length);
        foreach (char raw in input)
        {
            if (raw is '-' or ' ' or '\t')
                continue;
            char c = char.ToUpperInvariant(raw);
            if (Alphabet.IndexOf(c) < 0)
                return null;
            sb.Append(c);
        }
        return sb.Length == Length ? sb.ToString() : null;
    }
}

/// <summary>
/// Provas do pareamento. A chave sai do código por PBKDF2-SHA256 (200 mil iterações) com sal ligado às
/// impressões dos dois certificados vistos no TLS; cada lado prova conhecer o código com um HMAC diferente.
/// Um intermediário (que apresentaria outro certificado) faz as provas não baterem.
/// </summary>
public static class PairingProof
{
    private const int Iterations = 200_000;

    public static string ForAdmin(string normalizedCode, string adminFingerprint, string remoteFingerprint) =>
        Compute(normalizedCode, adminFingerprint, remoteFingerprint, "admin");

    public static string ForRemote(string normalizedCode, string adminFingerprint, string remoteFingerprint) =>
        Compute(normalizedCode, adminFingerprint, remoteFingerprint, "remote");

    public static bool Matches(string expectedHex, string? receivedHex)
    {
        if (receivedHex is null || receivedHex.Length != expectedHex.Length)
            return false;
        byte[] a = Encoding.ASCII.GetBytes(expectedHex.ToUpperInvariant());
        byte[] b = Encoding.ASCII.GetBytes(receivedHex.ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Compute(string code, string adminFp, string remoteFp, string role)
    {
        byte[] salt = SHA256.HashData(Encoding.ASCII.GetBytes($"NXDK-PAIR-v1|{adminFp}|{remoteFp}"));
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), salt, Iterations, HashAlgorithmName.SHA256, 32);
        byte[] mac = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{role}|{adminFp}|{remoteFp}"));
        return Convert.ToHexString(mac);
    }
}

public sealed record PairingCheck(bool Accepted, string? RemoteProof, string Message);

/// <summary>
/// Janela de pareamento do PC REMOTO: aberta pelo usuário, vale alguns minutos, um único uso,
/// e fecha após 3 códigos errados.
/// </summary>
public sealed class PairingWindow
{
    public const int MaxFailures = 3;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private string? _code;
    private DateTimeOffset _expires;
    private int _failures;

    public PairingWindow(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Disparado ao abrir ou fechar (inclusive por uso, erro ou expiração detectada).</summary>
    public event Action? Changed;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _code is not null && _time.GetUtcNow() < _expires;
        }
    }

    public DateTimeOffset ExpiresAt
    {
        get { lock (_gate) return _expires; }
    }

    /// <summary>Abre (ou reabre com código novo) e devolve o código formatado para exibir.</summary>
    public string Open(TimeSpan validity)
    {
        string code = PairingCode.Generate();
        lock (_gate)
        {
            _code = PairingCode.Normalize(code);
            _expires = _time.GetUtcNow() + validity;
            _failures = 0;
        }
        Changed?.Invoke();
        return code;
    }

    public void Close()
    {
        bool changed;
        lock (_gate)
        {
            changed = _code is not null;
            _code = null;
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Confere a prova do admin. Sucesso fecha a janela (uso único).</summary>
    public PairingCheck Verify(string adminFingerprint, string remoteFingerprint, string adminProof)
    {
        bool changed = false;
        PairingCheck result;
        lock (_gate)
        {
            if (_code is null || _time.GetUtcNow() >= _expires)
            {
                changed = _code is not null;
                _code = null;
                result = new(false, null, "Nenhum código de pareamento ativo neste PC REMOTO. Gere um novo código.");
            }
            else if (PairingProof.Matches(PairingProof.ForAdmin(_code, adminFingerprint, remoteFingerprint), adminProof))
            {
                string remoteProof = PairingProof.ForRemote(_code, adminFingerprint, remoteFingerprint);
                _code = null;
                changed = true;
                result = new(true, remoteProof, "Pareado.");
            }
            else
            {
                _failures++;
                if (_failures >= MaxFailures)
                {
                    _code = null;
                    changed = true;
                    result = new(false, null, "Código incorreto. Tentativas esgotadas: gere um novo código no PC REMOTO.");
                }
                else
                {
                    result = new(false, null, "Código incorreto.");
                }
            }
        }
        if (changed)
            Changed?.Invoke();
        return result;
    }
}
