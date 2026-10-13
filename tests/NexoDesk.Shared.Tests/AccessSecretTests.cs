using NexoDesk.Shared.Security;
using Xunit;

namespace NexoDesk.Shared.Tests;

public class AccessSecretTests
{
    [Fact]
    public void Hash_then_verify_accepts_correct_password()
    {
        string stored = AccessSecret.Hash("senha-forte-123");
        Assert.True(AccessSecret.Verify("senha-forte-123", stored));
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        string stored = AccessSecret.Hash("senha-forte-123");
        Assert.False(AccessSecret.Verify("senha-forte-124", stored));
        Assert.False(AccessSecret.Verify("", stored));
        Assert.False(AccessSecret.Verify(null, stored));
    }

    [Fact]
    public void Verify_rejects_missing_or_malformed_hash()
    {
        Assert.False(AccessSecret.Verify("qualquer", null));
        Assert.False(AccessSecret.Verify("qualquer", ""));
        Assert.False(AccessSecret.Verify("qualquer", "lixo"));
        Assert.False(AccessSecret.Verify("qualquer", "v1|ZZ|ZZ"));
        Assert.False(AccessSecret.Verify("qualquer", "v2|00|00"));
    }

    [Fact]
    public void Hashes_differ_by_salt_for_same_password()
    {
        string a = AccessSecret.Hash("senha-forte-123");
        string b = AccessSecret.Hash("senha-forte-123");
        Assert.NotEqual(a, b);                               // sais diferentes
        Assert.True(AccessSecret.Verify("senha-forte-123", a));
        Assert.True(AccessSecret.Verify("senha-forte-123", b));
    }

    [Fact]
    public void Hash_rejects_short_password()
    {
        Assert.Throws<System.ArgumentException>(() => AccessSecret.Hash("123"));
    }

    [Fact]
    public void IsConfigured_reflects_presence()
    {
        Assert.False(AccessSecret.IsConfigured(null));
        Assert.False(AccessSecret.IsConfigured("   "));
        Assert.True(AccessSecret.IsConfigured(AccessSecret.Hash("senha-forte-123")));
    }
}
