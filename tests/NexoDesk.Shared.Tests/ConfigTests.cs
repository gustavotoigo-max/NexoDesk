using NexoDesk.Shared.Configuration;

namespace NexoDesk.Shared.Tests;

public class ConfigTests
{
    private static readonly string A = new('A', 64);
    private static readonly string B = new('B', 64);
    private static readonly string C = new('C', 64);

    private static AgentConfig ValidAgent() => new()
    {
        MachineId = "pc-oficina",
        DisplayName = "PC Oficina",
        ListenAddress = "100.101.102.103",
        ServerCertificateSha256 = A,
        AllowedClients = [new AllowedClientEntry { Name = "Gustavo", Sha256 = B }],
    };

    [Fact]
    public void Agent_Valid_HasNoProblems()
    {
        Assert.Empty(ValidAgent().Validate());
        Assert.Empty((ValidAgent() with { ListenAddress = "auto" }).Validate());
    }

    [Fact]
    public void Agent_UnattendedAccess_IsOffByDefault()
    {
        Assert.False(new AgentConfig().UnattendedAccessEnabled);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.0.10")]
    [InlineData("nao-e-ip")]
    public void Agent_RejectsUnsafeListenAddress(string address)
    {
        Assert.NotEmpty((ValidAgent() with { ListenAddress = address }).Validate());
    }

    [Fact]
    public void Agent_LabMode_AllowsPrivateAddress_ButNeverWildcard()
    {
        Assert.Empty((ValidAgent() with { ListenAddress = "192.168.0.10", AllowNonTailscaleAddress = true }).Validate());
        Assert.NotEmpty((ValidAgent() with { ListenAddress = "0.0.0.0", AllowNonTailscaleAddress = true }).Validate());
    }

    [Fact]
    public void Agent_RequiresAtLeastOneAdmin()
    {
        Assert.Contains((ValidAgent() with { AllowedClients = [] }).Validate(), p => p.Contains("allowedClients"));
    }

    [Fact]
    public void Agent_RejectsDuplicateOrSharedCertificates()
    {
        var dup = ValidAgent() with { AllowedClients = [new() { Name = "X", Sha256 = B }, new() { Name = "Y", Sha256 = B.ToLowerInvariant() }] };
        Assert.Contains(dup.Validate(), p => p.Contains("repetida"));

        var shared = ValidAgent() with { AllowedClients = [new() { Name = "X", Sha256 = A }] };
        Assert.Contains(shared.Validate(), p => p.Contains("mesmo certificado"));
    }

    [Fact]
    public void Agent_ParsesJsonWithComments_AndRejectsUnknownFields()
    {
        string json = $$"""
        {
          // comentário permitido
          "machineId": "pc-oficina",
          "displayName": "PC Oficina",
          "serverCertificateSha256": "{{A}}",
          "allowedClients": [ { "name": "Gustavo", "sha256": "{{B}}" } ],
        }
        """;
        AgentConfig cfg = ConfigStore.ParseAgent(json);
        Assert.Equal("pc-oficina", cfg.MachineId);
        // Campos omitidos mantêm os padrões (regressão: o gerador de JSON zerava propriedades init).
        Assert.Equal("auto", cfg.ListenAddress);
        Assert.Equal(47115, cfg.Port);
        Assert.Equal(30, cfg.ConsentTimeoutSeconds);
        Assert.False(cfg.UnattendedAccessEnabled);

        Assert.Throws<ConfigException>(() => ConfigStore.ParseAgent("{\"machineID\":\"x\",\"unattendedAcess\":true}"));
    }

    [Fact]
    public void Agent_SerializeRoundTrips()
    {
        AgentConfig back = ConfigStore.ParseAgent(ConfigStore.Serialize(ValidAgent()));
        Assert.Equal(ValidAgent().MachineId, back.MachineId);
        Assert.Equal(B, back.AllowedClients[0].Sha256);
    }

    private static AdminConfig ValidAdmin() => new()
    {
        ClientId = "admin-gustavo",
        ClientCertificateSha256 = B,
        Machines =
        [
            new MachineEntry { Id = "pc-oficina", DisplayName = "PC Oficina", Host = "pc-oficina.exemplo.ts.net", ServerCertificateSha256 = A },
            new MachineEntry { Id = "pc-recepcao", DisplayName = "PC Recepção", Host = "100.64.1.2", ServerCertificateSha256 = C },
        ],
    };

    [Fact]
    public void Admin_Valid_HasNoProblems()
    {
        Assert.Empty(ValidAdmin().Validate());
    }

    [Fact]
    public void Admin_RejectsDuplicateIds_BadHosts_AndMissingPins()
    {
        AdminConfig cfg = ValidAdmin() with
        {
            Machines =
            [
                new MachineEntry { Id = "pc", DisplayName = "A", Host = "pc.ts.net", ServerCertificateSha256 = A },
                new MachineEntry { Id = "PC", DisplayName = "B", Host = "host com espaço", ServerCertificateSha256 = "" },
            ],
        };
        IReadOnlyList<string> problems = cfg.Validate();
        Assert.Contains(problems, p => p.Contains("id repetido"));
        Assert.Contains(problems, p => p.Contains("host inválido"));
        Assert.Contains(problems, p => p.Contains("serverCertificateSha256"));
    }

    [Fact]
    public void Admin_WarnsAboveFiveMachines()
    {
        var many = Enumerable.Range(1, 6).Select(i => new MachineEntry { Id = $"pc{i}", DisplayName = $"PC {i}", Host = $"pc{i}.ts.net", ServerCertificateSha256 = A }).ToList();
        Assert.Contains((ValidAdmin() with { Machines = many }).Validate(), p => p.Contains("até 5"));
    }
}
