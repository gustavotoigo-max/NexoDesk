using System.Text;
using NexoDesk.Shared.Protocol;

namespace NexoDesk.Shared.Pairing;

/// <summary>
/// Código de acesso único do PC REMOTO: embute o endereço, a porta e o código de pareamento em um só texto,
/// para o ADMIN colar em um único campo. Formato: "NXD1-" + base64url("host|porta|CODIGO").
/// Não é segredo diferente do código de pareamento — só junta tudo em um lugar.
/// </summary>
public static class AccessCode
{
    private const string Prefix = "NXD1-";

    public static string Create(string host, int port, string normalizedCode)
    {
        string payload = $"{host}|{port}|{normalizedCode}";
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Prefix + b64;
    }

    /// <summary>Lê um código de acesso. Aceita com ou sem o prefixo, e ignora espaços/quebras ao redor.</summary>
    public static bool TryParse(string? token, out string host, out int port, out string code)
    {
        host = "";
        port = ProtocolConstants.DefaultPort;
        code = "";
        if (string.IsNullOrWhiteSpace(token))
            return false;

        string body = string.Concat(token.Where(c => !char.IsWhiteSpace(c)));
        if (body.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            body = body[Prefix.Length..];

        body = body.Replace('-', '+').Replace('_', '/');
        switch (body.Length % 4)
        {
            case 2: body += "=="; break;
            case 3: body += "="; break;
            case 1: return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return false;
        }

        string[] parts = Encoding.UTF8.GetString(bytes).Split('|');
        if (parts.Length != 3)
            return false;

        string h = parts[0];
        if (Uri.CheckHostName(h) == UriHostNameType.Unknown)
            return false;
        if (!int.TryParse(parts[1], out int p) || p is < 1 or > 65535)
            return false;
        string? normalized = PairingCode.Normalize(parts[2]);
        if (normalized is null)
            return false;

        host = h;
        port = p;
        code = normalized;
        return true;
    }

    public static bool IsValid(string? token) => TryParse(token, out _, out _, out _);
}
