using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Security;

namespace NexoDesk.Shared.Configuration;

/// <summary>Administrador autorizado no arquivo do Agent.</summary>
public sealed record AllowedClientEntry
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// Configuração do Agent (%ProgramData%\NexoDesk\agent.json). Não contém chaves privadas:
/// o certificado é localizado no repositório do Windows pela impressão SHA-256.
/// </summary>
public sealed record AgentConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string MachineId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>IP Tailscale deste computador ou "auto" para detectar o único IP Tailscale presente.</summary>
    public string ListenAddress { get; set; } = "auto";

    public int Port { get; set; } = ProtocolConstants.DefaultPort;
    public string ServerCertificateSha256 { get; set; } = "";
    public List<AllowedClientEntry> AllowedClients { get; set; } = [];

    /// <summary>Desligado por padrão: cada conexão exige "Permitir" de quem está no computador.</summary>
    public bool UnattendedAccessEnabled { get; set; }

    public int ConsentTimeoutSeconds { get; set; } = 30;
    public int HandshakeTimeoutSeconds { get; set; } = 10;
    public int IdleTimeoutSeconds { get; set; } = 30;
    public int JpegQuality { get; set; } = 70;
    public int MaxFps { get; set; } = 12;
    public int MaxConnectionAttemptsPerMinute { get; set; } = 10;

    /// <summary>Somente para laboratório: permite escutar fora das faixas Tailscale (nunca em 0.0.0.0).</summary>
    public bool AllowNonTailscaleAddress { get; set; }

    public string? LogDirectory { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SchemaVersion != 1)
            problems.Add($"schemaVersion {SchemaVersion} não suportada (use 1).");
        if (!ConfigText.IsValidId(MachineId))
            problems.Add("machineId vazio ou inválido (use letras, números, '-', '_' ou '.').");
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > ProtocolConstants.MaxIdLength)
            problems.Add("displayName vazio ou longo demais.");
        if (Port is < 1024 or > 65535)
            problems.Add("port deve estar entre 1024 e 65535.");
        if (CertificateFingerprint.Normalize(ServerCertificateSha256) is null)
            problems.Add("serverCertificateSha256 ausente ou inválido (SHA-256 com 64 dígitos hexadecimais).");

        if (!string.Equals(ListenAddress, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(ListenAddress, out IPAddress? ip))
                problems.Add("listenAddress deve ser um IP ou \"auto\".");
            else if (TailscaleAddress.IsWildcard(ip))
                problems.Add("listenAddress não pode ser 0.0.0.0 nem ::. Use o IP Tailscale deste computador.");
            else if (!AllowNonTailscaleAddress && !TailscaleAddress.IsTailscale(ip))
                problems.Add("listenAddress não é um IP Tailscale (100.64.0.0/10 ou fd7a:115c:a1e0::/48).");
        }

        if (AllowedClients.Count == 0)
            problems.Add("allowedClients está vazio: nenhum administrador conseguiria conectar.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AllowedClientEntry c in AllowedClients)
        {
            string? fp = CertificateFingerprint.Normalize(c.Sha256);
            if (string.IsNullOrWhiteSpace(c.Name))
                problems.Add("Há um administrador sem nome em allowedClients.");
            if (fp is null)
                problems.Add($"Impressão inválida para o administrador \"{c.Name}\".");
            else if (!seen.Add(fp))
                problems.Add($"Impressão repetida em allowedClients (\"{c.Name}\").");
            else if (fp == CertificateFingerprint.Normalize(ServerCertificateSha256))
                problems.Add($"O administrador \"{c.Name}\" usa o mesmo certificado do servidor. Cada dispositivo precisa do seu.");
        }

        if (ConsentTimeoutSeconds is < 5 or > 300)
            problems.Add("consentTimeoutSeconds deve estar entre 5 e 300.");
        if (HandshakeTimeoutSeconds is < 2 or > 60)
            problems.Add("handshakeTimeoutSeconds deve estar entre 2 e 60.");
        if (IdleTimeoutSeconds is < 5 or > 600)
            problems.Add("idleTimeoutSeconds deve estar entre 5 e 600.");
        if (JpegQuality is < 10 or > 95)
            problems.Add("jpegQuality deve estar entre 10 e 95.");
        if (MaxFps is < 1 or > 30)
            problems.Add("maxFps deve estar entre 1 e 30.");
        if (MaxConnectionAttemptsPerMinute is < 1 or > 120)
            problems.Add("maxConnectionAttemptsPerMinute deve estar entre 1 e 120.");
        return problems;
    }
}

/// <summary>Computador no catálogo do Admin. A identidade vem da impressão do certificado, não do nome.</summary>
public sealed record MachineEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = ProtocolConstants.DefaultPort;
    public string ServerCertificateSha256 { get; set; } = "";
}

/// <summary>Configuração do Admin (%AppData%\NexoDesk\admin.json): identidade deste administrador e catálogo.</summary>
public sealed record AdminConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string ClientId { get; set; } = "";
    public string ClientCertificateSha256 { get; set; } = "";
    public List<MachineEntry> Machines { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SchemaVersion != 1)
            problems.Add($"schemaVersion {SchemaVersion} não suportada (use 1).");
        if (!ConfigText.IsValidId(ClientId))
            problems.Add("clientId vazio ou inválido.");
        if (CertificateFingerprint.Normalize(ClientCertificateSha256) is null)
            problems.Add("clientCertificateSha256 ausente ou inválido.");
        if (Machines.Count > 5)
            problems.Add($"O catálogo tem {Machines.Count} computadores; o NexoDesk foi planejado para até 5.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MachineEntry m in Machines)
        {
            string label = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName;
            if (!ConfigText.IsValidId(m.Id))
                problems.Add($"Computador \"{label}\": id inválido.");
            else if (!ids.Add(m.Id))
                problems.Add($"Computador \"{label}\": id repetido.");
            if (string.IsNullOrWhiteSpace(m.DisplayName))
                problems.Add($"Computador \"{m.Id}\": displayName vazio.");
            if (Uri.CheckHostName(m.Host) == UriHostNameType.Unknown)
                problems.Add($"Computador \"{label}\": host inválido.");
            if (m.Port is < 1 or > 65535)
                problems.Add($"Computador \"{label}\": porta inválida.");
            if (CertificateFingerprint.Normalize(m.ServerCertificateSha256) is null)
                problems.Add($"Computador \"{label}\": serverCertificateSha256 ausente ou inválido.");
        }
        return problems;
    }
}

internal static class ConfigText
{
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= ProtocolConstants.MaxIdLength
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(AdminConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Leitura e escrita dos arquivos de configuração. Campos desconhecidos são erro (evita erro de digitação silencioso).</summary>
public static class ConfigStore
{
    public static AgentConfig LoadAgent(string path) => Load(path, ConfigJsonContext.Default.AgentConfig);
    public static AdminConfig LoadAdmin(string path) => Load(path, ConfigJsonContext.Default.AdminConfig);

    public static string Serialize(AgentConfig config) => JsonSerializer.Serialize(config, ConfigJsonContext.Default.AgentConfig);
    public static string Serialize(AdminConfig config) => JsonSerializer.Serialize(config, ConfigJsonContext.Default.AdminConfig);

    public static AgentConfig ParseAgent(string json) => Parse(json, ConfigJsonContext.Default.AgentConfig, "agent.json");
    public static AdminConfig ParseAdmin(string json) => Parse(json, ConfigJsonContext.Default.AdminConfig, "admin.json");

    private static T Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        if (!File.Exists(path))
            throw new ConfigException($"Arquivo de configuração não encontrado: {path}");
        return Parse(File.ReadAllText(path), info, path);
    }

    private static T Parse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, string source)
    {
        try
        {
            return JsonSerializer.Deserialize(json, info) ?? throw new ConfigException($"{source} está vazio.");
        }
        catch (JsonException ex)
        {
            string where = ex.LineNumber is long line ? $" (linha {line + 1})" : "";
            throw new ConfigException($"{source} tem JSON inválido{where}: {ex.Message}", ex);
        }
    }
}
