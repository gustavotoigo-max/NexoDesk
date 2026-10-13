using NexoDesk.Shared.Pairing;

namespace NexoDesk.Shared.Tests;

public class PairingTests
{
    private static readonly string AdminFp = new('A', 64);
    private static readonly string RemoteFp = new('B', 64);

    [Fact]
    public void Code_HasExpectedShape_AndNormalizes()
    {
        string code = PairingCode.Generate();
        Assert.Equal(14, code.Length);
        Assert.Equal('-', code[4]);
        Assert.Equal('-', code[9]);
        string? n = PairingCode.Normalize(code.ToLowerInvariant().Replace("-", " "));
        Assert.Equal(code.Replace("-", ""), n);
    }

    [Theory]
    [InlineData("ABCD-EFGH-JKL")]       // curto
    [InlineData("ABCD-EFGH-JKLMN")]     // longo
    [InlineData("ABCD-EFGH-JKL0")]      // zero não existe no alfabeto
    [InlineData("")]
    public void Code_InvalidIsRejected(string input)
    {
        Assert.Null(PairingCode.Normalize(input));
    }

    [Fact]
    public void Codes_AreRandom()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => PairingCode.Generate()).ToHashSet();
        Assert.Equal(50, codes.Count);
    }

    [Fact]
    public void Proofs_DependOnCodeRoleAndBothFingerprints()
    {
        string code = PairingCode.Normalize(PairingCode.Generate())!;
        string admin = PairingProof.ForAdmin(code, AdminFp, RemoteFp);

        Assert.Equal(64, admin.Length);
        Assert.NotEqual(admin, PairingProof.ForRemote(code, AdminFp, RemoteFp));
        Assert.NotEqual(admin, PairingProof.ForAdmin(code, AdminFp, new string('C', 64)));
        Assert.NotEqual(admin, PairingProof.ForAdmin(PairingCode.Normalize(PairingCode.Generate())!, AdminFp, RemoteFp));
        Assert.True(PairingProof.Matches(admin, admin.ToLowerInvariant()));
        Assert.False(PairingProof.Matches(admin, null));
    }

    [Fact]
    public void Window_AcceptsOnce()
    {
        var window = new PairingWindow();
        string code = PairingCode.Normalize(window.Open(TimeSpan.FromMinutes(5)))!;
        Assert.True(window.IsOpen);

        PairingCheck ok = window.Verify(AdminFp, RemoteFp, PairingProof.ForAdmin(code, AdminFp, RemoteFp));
        Assert.True(ok.Accepted);
        Assert.Equal(PairingProof.ForRemote(code, AdminFp, RemoteFp), ok.RemoteProof);
        Assert.False(window.IsOpen);

        Assert.False(window.Verify(AdminFp, RemoteFp, PairingProof.ForAdmin(code, AdminFp, RemoteFp)).Accepted);
    }

    [Fact]
    public void Window_ClosesAfterThreeWrongCodes()
    {
        var window = new PairingWindow();
        string code = PairingCode.Normalize(window.Open(TimeSpan.FromMinutes(5)))!;
        string wrong = new('0', 64);
        Assert.False(window.Verify(AdminFp, RemoteFp, wrong).Accepted);
        Assert.False(window.Verify(AdminFp, RemoteFp, wrong).Accepted);
        Assert.True(window.IsOpen);
        Assert.Contains("esgotadas", window.Verify(AdminFp, RemoteFp, wrong).Message);
        Assert.False(window.IsOpen);

        // Mesmo o código certo não vale mais.
        Assert.False(window.Verify(AdminFp, RemoteFp, PairingProof.ForAdmin(code, AdminFp, RemoteFp)).Accepted);
    }

    [Fact]
    public void Window_Expires()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var window = new PairingWindow(time);
        string code = PairingCode.Normalize(window.Open(TimeSpan.FromMinutes(10)))!;
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(window.IsOpen);
        Assert.False(window.Verify(AdminFp, RemoteFp, PairingProof.ForAdmin(code, AdminFp, RemoteFp)).Accepted);
    }

    [Fact]
    public void Window_RaisesChanged()
    {
        var window = new PairingWindow();
        int changes = 0;
        window.Changed += () => changes++;
        window.Open(TimeSpan.FromMinutes(1));
        window.Close();
        window.Close();
        Assert.Equal(2, changes);
    }
}

public class AccessCodeTests
{
    [Fact]
    public void RoundTrip_CarriesHostPortAndCode()
    {
        string code = NexoDesk.Shared.Pairing.PairingCode.Normalize(NexoDesk.Shared.Pairing.PairingCode.Generate())!;
        string token = NexoDesk.Shared.Pairing.AccessCode.Create("pc-oficina.tail1234.ts.net", 47115, code);

        Assert.StartsWith("NXD1-", token);
        Assert.True(NexoDesk.Shared.Pairing.AccessCode.TryParse(token, out string host, out int port, out string back));
        Assert.Equal("pc-oficina.tail1234.ts.net", host);
        Assert.Equal(47115, port);
        Assert.Equal(code, back);
    }

    [Fact]
    public void TryParse_AcceptsWhitespaceAndMissingPrefix()
    {
        string code = new string('A', 12);
        string token = NexoDesk.Shared.Pairing.AccessCode.Create("100.101.102.103", 50000, code);
        string messy = "  " + token + "\n";
        Assert.True(NexoDesk.Shared.Pairing.AccessCode.TryParse(messy, out string host, out int port, out _));
        Assert.Equal("100.101.102.103", host);
        Assert.Equal(50000, port);

        // Sem o prefixo também vale.
        string noPrefix = token.Substring("NXD1-".Length);
        Assert.True(NexoDesk.Shared.Pairing.AccessCode.TryParse(noPrefix, out _, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("qualquer coisa")]
    [InlineData("NXD1-###")]
    public void TryParse_RejectsInvalid(string token)
    {
        Assert.False(NexoDesk.Shared.Pairing.AccessCode.TryParse(token, out _, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsBadHostOrCode()
    {
        // host inválido
        string bad = "NXD1-" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("host com espaço|47115|AAAAAAAAAAAA")).TrimEnd('=').Replace('+','-').Replace('/','_');
        Assert.False(NexoDesk.Shared.Pairing.AccessCode.TryParse(bad, out _, out _, out _));
        // código inválido (tamanho errado)
        string badCode = "NXD1-" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("pc.ts.net|47115|ABC")).TrimEnd('=').Replace('+','-').Replace('/','_');
        Assert.False(NexoDesk.Shared.Pairing.AccessCode.TryParse(badCode, out _, out _, out _));
    }
}
