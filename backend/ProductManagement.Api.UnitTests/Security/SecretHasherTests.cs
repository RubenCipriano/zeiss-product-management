using ProductManagement.Api.Security;

namespace ProductManagement.Api.UnitTests.Security;

public sealed class SecretHasherTests
{
    private static readonly string Hash = SecretHasher.Hash("correct horse");

    [Fact]
    public void TheRightSecret_Verifies()
    {
        SecretHasher.Verify("correct horse", Hash).ShouldBeTrue();
    }

    [Fact]
    public void AnyOtherSecret_DoesNot()
    {
        SecretHasher.Verify("correct horse ", Hash).ShouldBeFalse();
        SecretHasher.Verify(string.Empty, Hash).ShouldBeFalse();
    }

    [Fact]
    public void HashingIsSalted()
    {
        SecretHasher.Hash("correct horse").ShouldNotBe(Hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text-secret")]
    [InlineData("md5$1$abc$def")]
    [InlineData("pbkdf2-sha256$many$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$not base64$aGFzaA==")]
    public void AMalformedHash_NeverVerifies(string encodedHash)
    {
        SecretHasher.Verify("anything", encodedHash).ShouldBeFalse();
    }

    [Fact]
    public void IsWellFormed_RecognisesTheFormat()
    {
        SecretHasher.IsWellFormed(Hash).ShouldBeTrue();
        SecretHasher.IsWellFormed("plain-text-secret").ShouldBeFalse();
    }
}
