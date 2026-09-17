using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NovaWallet.Service;

namespace NovaWallet.UnitTests;

public class TransferRulesTests
{
    [Fact]
    public void HashUsesCanonicalGuidAndInvariantAmount()
    {
        const string source = "abcdef01-0000-0000-0000-000000000001";
        const string destination = "abcdef02-0000-0000-0000-000000000002";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{destination}|1000000")));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(expected, TransferRules.RequestHash(Guid.Parse(source.ToUpperInvariant()), Guid.Parse(destination), 1000000));
            Assert.NotEqual(expected, TransferRules.RequestHash(Guid.Parse(source), Guid.Parse(destination), 1000001));
            Assert.NotEqual(expected, TransferRules.RequestHash(Guid.Parse(destination), Guid.Parse(source), 1000000));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("2026-09-17T22:59:59Z", "2026-09-16T23:00:00Z")]
    [InlineData("2026-09-17T23:00:00Z", "2026-09-17T23:00:00Z")]
    [InlineData("2026-12-31T23:00:00Z", "2026-12-31T23:00:00Z")]
    [InlineData("2024-02-29T23:00:00Z", "2024-02-29T23:00:00Z")]
    public void WatDayUsesHalfOpenUtcBoundaries(string instant, string expectedStart)
    {
        var now = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);
        var (start, end) = TransferRules.WatDay(now);
        Assert.Equal(DateTimeOffset.Parse(expectedStart).UtcDateTime, start);
        Assert.Equal(start.AddDays(1), end);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
        Assert.True(now.UtcDateTime >= start && now.UtcDateTime < end);
    }

    [Theory]
    [InlineData(80, 20, 100, true)] [InlineData(80, 21, 100, false)]
    [InlineData(long.MaxValue - 1, 1, long.MaxValue, true)]
    [InlineData(long.MaxValue - 1, 2, long.MaxValue, false)]
    [InlineData(101, 1, 100, false)] [InlineData(0, 0, 100, false)]
    public void LimitIncludesBoundaryWithoutOverflow(long sent, long requested, long limit, bool expected) =>
        Assert.Equal(expected, TransferRules.WithinDailyLimit(sent, requested, limit));
}
