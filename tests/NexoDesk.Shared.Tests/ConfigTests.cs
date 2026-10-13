using NexoDesk.Shared.Configuration;

namespace NexoDesk.Shared.Tests;

public class ConfigTests
{
    private static readonly string A = new('A', 64);
    private static readonly string B = new('B', 64);

    private static AppConfig Valid() => new()
    {
        Role = DeviceRole.Remote,
        DeviceName = "PC Oficina",
        DeviceId = "pc-oficina",
        CertificateSha256 = A,
    };

    [Fact]
    public void Valid_HasNoProblems()
    {
        Assert.Empty(Valid().Validate());
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var cfg = new AppConfig();
        Assert.Null(cfg.Role);
        Assert.False(cfg.Remote.UnattendedAccessEnabled);
        Assert.Equal("auto", cfg.Remote.ListenAddress);
        Assert.Equal(47115, cfg.Remote.Port);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.0.10")]
    [InlineData("nao-e-ip")]
    public void RejectsUnsafeListenAddress(string address)
    {
        AppConfig cfg = Valid();
        cfg.Remote.ListenAddress = address;
        Assert.NotEmpty(cfg.Validate());
    }

    [Fact]
    public void LabMode_AllowsPrivateAddress_ButNeverWildcard()
    {
        AppConfig cfg = Valid();
        cfg.Remote.AllowNonTailscaleAddress = true;
        cfg.Remote.ListenAddress = "192.168.0.10";
        Assert.Empty(cfg.Validate());
        cfg.Remote.ListenAddress = "0.0.0.0";
        Assert.NotEmpty(cfg.Validate());
    }

    [Fact]
    public void RejectsMoreThanFiveComputers_AndDuplicates()
    {
        AppConfig cfg = Valid();
        for (int i = 0; i < 6; i++)
            cfg.Admin.Computers.Add(new PairedComputer { Id = $"pc{i}", Name = $"PC {i}", Host = $"pc{i}.ts.net", Sha256 = B });
        Assert.Contains(cfg.Validate(), p => p.Contains("limite"));

        cfg.Admin.Computers = [new() { Id = "pc", Name = "A", Host = "a.ts.net", Sha256 = B }, new() { Id = "PC", Name = "B", Host = "b.ts.net", Sha256 = B }];
        Assert.Contains(cfg.Validate(), p => p.Contains("repetido"));
    }

    [Fact]
    public void RoundTrip_KeepsDefaultsAndRole()
    {
        AppConfig cfg = Valid();
        cfg.Remote.Admins.Add(new PairedAdmin { Name = "Gustavo", Sha256 = B, PairedAt = DateTimeOffset.UnixEpoch });
        string json = ConfigStore.Serialize(cfg);
        Assert.Contains("\"role\": \"Remote\"", json);

        AppConfig back = ConfigStore.Parse(json);
        Assert.Equal(DeviceRole.Remote, back.Role);
        Assert.Equal("Gustavo", back.Remote.Admins[0].Name);

        // Campos omitidos mantêm os padrões (regressão: o gerador de JSON zerava propriedades init).
        AppConfig partial = ConfigStore.Parse("{\"deviceName\":\"X\"}");
        Assert.Equal(47115, partial.Remote.Port);
        Assert.Equal(30, partial.Remote.ConsentTimeoutSeconds);
        Assert.True(partial.Remote.StartWithWindows);
    }

    [Fact]
    public void SaveAndLoad_File()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nexodesk-{Guid.NewGuid():N}", "config.json");
        try
        {
            Assert.Null(ConfigStore.Load(path));
            ConfigStore.Save(path, Valid());
            Assert.Equal("pc-oficina", ConfigStore.Load(path)!.DeviceId);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("PC-OFICINA", "pc-oficina")]
    [InlineData("Recepção 2", "recep-o-2")]
    [InlineData("---", "pc")]
    public void MakeId_ProducesValidIds(string name, string expected)
    {
        Assert.Equal(expected, AppConfig.MakeId(name));
    }

    [Fact]
    public void InvalidJson_IsConfigException()
    {
        Assert.Throws<ConfigException>(() => ConfigStore.Parse("{ quebrado"));
    }
}
