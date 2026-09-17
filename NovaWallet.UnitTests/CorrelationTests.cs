using NovaWallet.API.Infrastructure;

namespace NovaWallet.UnitTests;

public class CorrelationTests
{
    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData("a b", false)]
    [InlineData("a\r\nb", false)] [InlineData("<tag>", false)]
    [InlineData("request-1_A.b", true)]
    public void CorrelationIdsUseSafePrintableCharacters(string? value, bool expected) =>
        Assert.Equal(expected, CorrelationMiddleware.IsValid(value));

    [Fact]
    public void CorrelationIdFitsAuditColumn()
    {
        Assert.True(CorrelationMiddleware.IsValid(new string('a', 100)));
        Assert.False(CorrelationMiddleware.IsValid(new string('a', 101)));
    }
}
