using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Repositories;

namespace NovaWallet.API.Infrastructure;

public static class DatabaseStartup
{
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:ApplyMigrations")) return;
        const int attempts = 30;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await using var scope = app.Services.CreateAsyncScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<NovaWalletDbContext>();
                await db.Database.MigrateAsync(app.Lifetime.ApplicationStopping);
                app.Logger.LogInformation("Database migrations applied successfully.");
                return;
            }
            catch (SqlException) when (attempt < attempts)
            {
                app.Logger.LogWarning("SQL Server is not ready for migrations; attempt {Attempt} of {Attempts}.", attempt, attempts);
                await Task.Delay(TimeSpan.FromSeconds(2), app.Lifetime.ApplicationStopping);
            }
        }
    }
}
