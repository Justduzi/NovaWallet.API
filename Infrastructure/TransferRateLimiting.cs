using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace NovaWallet.API.Infrastructure;

public sealed class TransferRateLimitOptions
{
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
}

public sealed class TransferRateLimitPolicy(IOptions<TransferRateLimitOptions> options) : IRateLimiterPolicy<string>
{
    public const string Name = "transfers";

    public RateLimitPartition<string> GetPartition(HttpContext context)
    {
        var subject = context.User.FindFirst("sub")?.Value;
        // Authorization runs first; this fallback never turns anonymous traffic into financial work.
        if (subject is null) return RateLimitPartition.GetNoLimiter("anonymous");
        return RateLimitPartition.GetFixedWindowLimiter(subject, _ => new FixedWindowRateLimiterOptions {
            PermitLimit = options.Value.PermitLimit,
            Window = TimeSpan.FromSeconds(options.Value.WindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, title: "Transfer request rate limit exceeded.",
            extensions: new Dictionary<string, object?> { ["code"] = "rate_limit_exceeded" })
            .ExecuteAsync(context.HttpContext);
    };
}
