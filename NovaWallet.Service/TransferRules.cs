using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Service;

public sealed class WalletOptions
{
    public long DailyOutboundLimitKobo { get; set; } = 50_000_000;
}

public static class TransferRules
{
    public static string RequestHash(Guid source, Guid destination, long amountKobo) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{source:D}|{destination:D}|{amountKobo}"))));

    public static (DateTime StartUtc, DateTime EndUtc) WatDay(DateTimeOffset now)
    {
        var midnight = now.ToOffset(TimeSpan.FromHours(1)).Date;
        var start = new DateTimeOffset(midnight, TimeSpan.FromHours(1)).UtcDateTime;
        return (start, start.AddDays(1));
    }

    // Subtract instead of adding to avoid overflowing even with a long.MaxValue limit.
    public static bool WithinDailyLimit(long sent, long requested, long limit) =>
        sent >= 0 && requested > 0 && limit > 0 && sent <= limit && requested <= limit - sent;
}

public sealed record TransferResult(Guid Reference, Guid SourceWalletId, Guid DestinationWalletId,
    long AmountKobo, long SourceBalanceKobo, long DestinationBalanceKobo, DateTime CreatedAtUtc);

public sealed record StoredTransferResult(int StatusCode, string ResponseBody);
