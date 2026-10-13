using System.Text.Json;
using System.Text.Json.Serialization;
using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Security;

namespace NexoDesk.Shared.Configuration;

/// <summary>Papel deste computador. O mesmo programa (e o mesmo instalador) serve para os dois.</summary>
public enum DeviceRole
{
    /// <summary>Quem acessa outros computadores.</summary>
    Admin,

    /// <summary>O computador que é acessado.</summary>
    Remote,
}

/// <summary>ADMIN autorizado a acessar este PC REMOTO (criado pelo pareamento).</summary>
public sealed record PairedAdmin
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
}

/// <summary>PC REMOTO que este ADMIN acessa (criado pelo pareamento).</summary>
public sealed record PairedComputer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = ProtocolConstants.DefaultPort;
    public string Sha256 { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
}

public sealed record RemoteSettings
{
    public int Port { get; set; } = ProtocolConstants.DefaultPort;

    /// <summary>Desligado por padrão: cada conexão pede "Permitir" neste computador.</summary>
    public bool UnattendedAccessEnabled { get; set; }

    public int ConsentTimeoutSeconds { get; set; } = 30;
    public bool StartWithWindows { get; set; } = true;

    /// <summary>"auto" (IP Tailscale) ou um IP. 0.0.0.0/:: nunca são aceitos.</summary>
    public string ListenAddress { get; set; } = "auto";

    /// <summary>Somente para laboratório.</summary>
    public bool AllowNonTailscaleAddress { get; set; }

    public List<PairedAdmin> Admins { get; set; } = [];
}

public sealed record AdminSettings
{
    public List<PairedComputer> Computers { get; set; } = [];
}

/// <summary>
/// Configuração única do NexoDesk, em %AppData%\NexoDesk\config.json. Mantida pelo programa
/// (ninguém precisa editar à mão) e sem segredos: só impressões públicas de certificados.
/// </summary>
public sealed record AppConfig
{
    public const int CurrentSchema = 2;
    public const int MaxComputers = 5;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>Null até a escolha na primeira abertura.</summary>
    public DeviceRole? Role { get; set; }

    public string DeviceName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public RemoteSettings Remote { get; set; } = new();
    public AdminSettings Admin { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SchemaVersion != CurrentSchema)
            problems.Add($"Versão de configuração {SchemaVersion} não suportada.");
        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Length > ProtocolConstants.MaxIdLength)
            problems.Add("Nome do computador vazio ou longo demais.");
        if (!ConfigText.IsValidId(DeviceId))
            problems.Add("Identificador do computador inválido.");
        if (CertificateFingerprint.Normalize(CertificateSha256) is null)
            problems.Add("Identidade (certificado) deste computador ausente.");
        if (Remote.Port is < 1024 or > 65535)
            problems.Add("Porta do PC REMOTO deve estar entre 1024 e 65535.");
        if (Remote.ConsentTimeoutSeconds is < 5 or > 300)
            problems.Add("Tempo para permitir o acesso deve estar entre 5 e 300 segundos.");
        if (!string.Equals(Remote.ListenAddress, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!System.Net.IPAddress.TryParse(Remote.ListenAddress, out var ip))
                problems.Add("Endereço de escuta deve ser um IP ou \"auto\".");
            else if (TailscaleAddress.IsWildcard(ip))
                problems.Add("Endereço de escuta não pode ser 0.0.0.0 nem ::.");
            else if (!Remote.AllowNonTailscaleAddress && !TailscaleAddress.IsTailscale(ip))
                problems.Add("Endereço de escuta não é um IP Tailscale.");
        }
        foreach (PairedAdmin a in Remote.Admins)
        {
            if (string.IsNullOrWhiteSpace(a.Name) || CertificateFingerprint.Normalize(a.Sha256) is null)
                problems.Add($"ADMIN pareado inválido: \"{a.Name}\".");
        }
        if (Admin.Computers.Count > MaxComputers)
            problems.Add($"Há {Admin.Computers.Count} computadores; o limite é {MaxComputers}.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PairedComputer c in Admin.Computers)
        {
            if (!ConfigText.IsValidId(c.Id) || !ids.Add(c.Id))
                problems.Add($"Computador \"{c.Name}\" com identificador inválido ou repetido.");
            if (Uri.CheckHostName(c.Host) == UriHostNameType.Unknown)
                problems.Add($"Computador \"{c.Name}\" com endereço inválido.");
            if (CertificateFingerprint.Normalize(c.Sha256) is null)
                problems.Add($"Computador \"{c.Name}\" sem identidade válida.");
        }
        return problems;
    }

    /// <summary>Identificador a partir do nome do Windows: minúsculas, letras, números e hífen.</summary>
    public static string MakeId(string name)
    {
        string id = new(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (id.Contains("--", StringComparison.Ordinal))
            id = id.Replace("--", "-", StringComparison.Ordinal);
        id = id.Trim('-');
        return id.Length == 0 ? "pc" : id[..Math.Min(id.Length, 48)];
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
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Leitura e gravação atômica da configuração.</summary>
public static class ConfigStore
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexoDesk", "config.json");

    public static AppConfig Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig) ?? throw new ConfigException("Configuração vazia.");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Configuração com JSON inválido: {ex.Message}", ex);
        }
    }

    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);

    /// <summary>Retorna null se o arquivo ainda não existe (primeira abertura).</summary>
    public static AppConfig? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Grava em arquivo temporário e substitui, para nunca deixar um arquivo pela metade.</summary>
    public static void Save(string path, AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(config));
        File.Move(temp, path, overwrite: true);
    }
}
