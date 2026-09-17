using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NovaWallet.Repositories;

namespace NovaWallet.API.Infrastructure;

public sealed class LedgerHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
            if (!await db.Database.CanConnectAsync(ct)) return HealthCheckResult.Unhealthy("Database unavailable.");
            if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
                return HealthCheckResult.Unhealthy("Database migrations pending.");
            // Also verify that the critical tables are accessible to the application account.
            await db.Wallets.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
            await db.OutboxMessages.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Database unavailable.");
        }
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions {
            Predicate = _ => false, ResponseWriter = WriteResponse
        }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions {
            Predicate = check => check.Tags.Contains("ready"), ResponseWriter = WriteResponse
        }).AllowAnonymous();
    }

    private static Task WriteResponse(HttpContext context, HealthReport report) =>
        (report.Status == HealthStatus.Healthy
            ? Results.Ok(new { status = "Healthy" })
            : Results.Problem(statusCode: 503, title: "Service is not ready.",
                extensions: new Dictionary<string, object?> { ["code"] = "not_ready" }))
        .ExecuteAsync(context);
}
