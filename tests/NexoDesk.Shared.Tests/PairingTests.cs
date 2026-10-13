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
