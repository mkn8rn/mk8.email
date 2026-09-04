using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class PasswordHasherTests
{
    [TestMethod]
    public void HashUsesDovecotCompatibleBcrypt()
    {
        const string password = "correct-horse-battery-staple";

        var hash = PasswordHasher.Hash(password);

        StringAssert.StartsWith(hash, PasswordHasher.DovecotSchemePrefix);
        Assert.IsTrue(PasswordHasher.Verify(password, hash));
        Assert.IsFalse(PasswordHasher.Verify("different-password", hash));
        Assert.IsFalse(PasswordHasher.Verify(password, "invalid-hash"));
    }
}
