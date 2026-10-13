using System.IO;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Security;

namespace NexoDesk.Admin;

public static class AdminPaths
{
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexoDesk");

    public static string ConfigFile { get; } = Path.Combine(ConfigDirectory, "admin.json");

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexoDesk", "admin-logs");
}

/// <summary>Configuração carregada e certificado deste administrador, com os problemas encontrados.</summary>
public sealed class AdminContext
{
    private AdminContext(AdminConfig? config, X509Certificate2? certificate, IReadOnlyList<string> problems, bool createdTemplate)
    {
        Config = config;
        Certificate = certificate;
        Problems = problems;
        CreatedTemplate = createdTemplate;
    }

    public AdminConfig? Config { get; }
    public X509Certificate2? Certificate { get; }
    public IReadOnlyList<string> Problems { get; }
    public bool CreatedTemplate { get; }
    public bool CanConnect => Config is not null && Certificate is not null;

    public static AdminContext Load()
    {
        bool created = false;
        if (!File.Exists(AdminPaths.ConfigFile))
        {
            Directory.CreateDirectory(AdminPaths.ConfigDirectory);
            File.WriteAllText(AdminPaths.ConfigFile, Template());
            created = true;
        }

        AdminConfig config;
        try
        {
            config = ConfigStore.LoadAdmin(AdminPaths.ConfigFile);
        }
        catch (ConfigException ex)
        {
            return new AdminContext(null, null, [ex.Message], created);
        }

        var problems = config.Validate().ToList();
        X509Certificate2? cert = null;
        if (CertificateFingerprint.Normalize(config.ClientCertificateSha256) is not null)
        {
            cert = CertificateStoreLookup.FindWithPrivateKey(config.ClientCertificateSha256);
            if (cert is null)
                problems.Add("O certificado deste administrador não foi encontrado (com chave privada) no repositório Pessoal do Windows. " +
                             "Gere com scripts\\New-NexoDeskCertificate.ps1 -Role Admin e copie a impressão SHA-256 para clientCertificateSha256.");
        }

        return new AdminContext(config, cert, problems, created);
    }

    private static string Template()
    {
        string user = new(Environment.UserName.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return $$"""
        {
          // Configuração do NexoDesk Admin. Comentários são permitidos.
          "schemaVersion": 1,

          // Nome deste administrador, enviado ao Agent (apenas informativo: a identidade real é o certificado).
          "clientId": "admin-{{(user.Length == 0 ? "usuario" : user)}}",

          // Impressão SHA-256 do certificado de cliente deste computador (scripts\New-NexoDeskCertificate.ps1 -Role Admin).
          "clientCertificateSha256": "",

          // Até 5 computadores. host = nome MagicDNS ou IP Tailscale. serverCertificateSha256 = impressão do certificado do Agent.
          "machines": [
            // { "id": "pc-oficina", "displayName": "PC Oficina", "host": "pc-oficina.SEU-TAILNET.ts.net", "port": 47115, "serverCertificateSha256": "" }
          ]
        }

        """;
    }
}
