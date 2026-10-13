using System.IO;
using System.Security.Cryptography.X509Certificates;
using NexoDesk.Shared.Configuration;
using NexoDesk.Shared.Security;
using NexoDesk.Shared.Session;

namespace NexoDesk.App.Services;

/// <summary>
/// Configuração e identidade deste computador. Na primeira abertura cria a identidade (certificado) sozinha;
/// toda alteração é gravada na hora em %AppData%\NexoDesk\config.json.
/// </summary>
public sealed class AppState
{
    private readonly object _gate = new();

    private AppState(AppConfig config, X509Certificate2 certificate, bool identityRecreated)
    {
        Config = config;
        Certificate = certificate;
        IdentityRecreated = identityRecreated;
    }

    public AppConfig Config { get; }
    public X509Certificate2 Certificate { get; private set; }

    /// <summary>True se o certificado sumiu (Windows reinstalado, outra conta) e foi criado outro: pareamentos antigos deixam de valer.</summary>
    public bool IdentityRecreated { get; }

    public string Fingerprint => CertificateFingerprint.Sha256(Certificate);
    public bool IsFirstRun => Config.Role is null;

    public event Action? Changed;

    public static AppState Load()
    {
        AppConfig config;
        try
        {
            config = ConfigStore.Load(AppPaths.ConfigFile) ?? NewConfig();
        }
        catch (ConfigException)
        {
            // Arquivo danificado: guarda uma cópia e recomeça, para o programa nunca ficar travado.
            File.Copy(AppPaths.ConfigFile, AppPaths.ConfigFile + $".danificado-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            config = NewConfig();
        }

        bool recreated = false;
        X509Certificate2? cert = CertificateFingerprint.Normalize(config.CertificateSha256) is null
            ? null
            : CertificateStoreLookup.FindWithPrivateKey(config.CertificateSha256);
        if (cert is null)
        {
            recreated = !string.IsNullOrEmpty(config.CertificateSha256);
            cert = DeviceIdentity.Create(config.DeviceName);
            config.CertificateSha256 = CertificateFingerprint.Sha256(cert);
        }

        var state = new AppState(config, cert, recreated);
        state.Save();
        return state;
    }

    private static AppConfig NewConfig()
    {
        string name = Environment.MachineName;
        return new AppConfig { DeviceName = name, DeviceId = AppConfig.MakeId(name) };
    }

    /// <summary>Altera e grava. Thread-safe.</summary>
    public void Update(Action<AppConfig> change)
    {
        lock (_gate)
        {
            change(Config);
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        lock (_gate)
            ConfigStore.Save(AppPaths.ConfigFile, Config);
    }
}

/// <summary>ADMINs pareados, lidos e gravados na configuração.</summary>
public sealed class ConfigClientStore(AppState state) : IClientStore
{
    public AuthorizedClient? Find(string fingerprint)
    {
        string? fp = CertificateFingerprint.Normalize(fingerprint);
        if (fp is null)
            return null;
        lock (state.Config.Remote.Admins)
        {
            PairedAdmin? admin = state.Config.Remote.Admins.FirstOrDefault(a => CertificateFingerprint.Normalize(a.Sha256) == fp);
            return admin is null ? null : new AuthorizedClient(admin.Name, fp);
        }
    }

    public void Add(AuthorizedClient client)
    {
        state.Update(cfg =>
        {
            lock (cfg.Remote.Admins)
            {
                string fp = CertificateFingerprint.Normalize(client.Fingerprint)!;
                cfg.Remote.Admins.RemoveAll(a => CertificateFingerprint.Normalize(a.Sha256) == fp);
                cfg.Remote.Admins.Add(new PairedAdmin { Name = client.Name, Sha256 = fp, PairedAt = DateTimeOffset.UtcNow });
            }
        });
    }
}
