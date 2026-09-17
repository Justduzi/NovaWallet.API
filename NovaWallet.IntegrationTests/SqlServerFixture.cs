using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Repositories;
using Testcontainers.MsSql;

namespace NovaWallet.IntegrationTests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public async Task<LedgerApi> CreateApiAsync(long limit = 50_000_000, int rateLimit = 60)
    {
        var connection = new SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = "ledger_" + Guid.NewGuid().ToString("N") };
        var api = new LedgerApi(connection.ConnectionString, limit, rateLimit);
        await using var db = api.Database();
        await db.Database.MigrateAsync();
        return api;
    }
}

public sealed class TestClock : TimeProvider
{
    private long ticks = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
}

public sealed class LedgerApi(string connectionString, long limit, int rateLimit = 60) : WebApplicationFactory<Program>
{
    internal const string SigningKey = "Integration-tests-only-signing-key-1234567890";
    public TestClock Clock { get; } = new();
    public string ConnectionString => connectionString;
    public LedgerApi SecondInstance() => new(connectionString, limit, rateLimit);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:NovaWallet", connectionString);
        builder.UseSetting("Jwt:Issuer", "NovaWallet.Tests");
        builder.UseSetting("Jwt:Audience", "NovaWallet.Tests");
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("WalletOptions:DailyOutboundLimitKobo", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Database:ApplyMigrations", "false");
        builder.UseSetting("TransferRateLimit:PermitLimit", rateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("TransferRateLimit:WindowSeconds", "60");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureServices(services => {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public NovaWalletDbContext Database() => new(new DbContextOptionsBuilder<NovaWalletDbContext>().UseSqlServer(connectionString).Options);

    public HttpClient AuthenticatedClient(string subject = "integration-actor")
    {
        var client = CreateClient();
        var token = new JwtSecurityToken("NovaWallet.Tests", "NovaWallet.Tests", [new Claim("sub", subject)],
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }
}
