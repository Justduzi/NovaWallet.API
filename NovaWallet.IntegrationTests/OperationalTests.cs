using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NovaWallet.API.Contracts;
using NovaWallet.Domain;

namespace NovaWallet.IntegrationTests;

public sealed class OperationalTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string CorrelationHeader = "X-Correlation-ID";

    private static async Task<WalletResponse> Create(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/wallets", new { customerId = Guid.NewGuid().ToString() });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WalletResponse>())!;
    }

    private static Task<HttpResponseMessage> Transfer(HttpClient client, Guid source, Guid destination, string key, string correlation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers") {
            Content = JsonContent.Create(new TransferRequest(source, destination, 10)) };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add(CorrelationHeader, correlation);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task LivenessDoesNotDependOnDatabaseAndReadinessRejectsMissingDatabase()
    {
        await using var ready = await fixture.CreateApiAsync();
        using var healthy = ready.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await healthy.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await healthy.GetAsync("/health/ready")).StatusCode);

        var connection = new SqlConnectionStringBuilder(ready.ConnectionString) {
            InitialCatalog = "missing_" + Guid.NewGuid().ToString("N"), ConnectTimeout = 1, ConnectRetryCount = 0 };
        await using var missing = new LedgerApi(connection.ConnectionString, 50_000_000);
        using var unhealthy = missing.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await unhealthy.GetAsync("/health/live")).StatusCode);
        using var response = await unhealthy.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_ready", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(connection.InitialCatalog, await response.Content.ReadAsStringAsync());
        Assert.Equal(response.Headers.GetValues(CorrelationHeader).Single(), problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ReadinessRejectsAnUnappliedMigration()
    {
        await using var api = await fixture.CreateApiAsync();
        await using var db = api.Database();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%AddTransferOutbox'");
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task CorrelationFlowsToAuditOutboxAndResponseWithoutChangingReplay()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var source = await Create(client);
        var destination = await Create(client);
        using var creditRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/wallets/{source.Id}/credits") {
            Content = JsonContent.Create(new { amountKobo = 100 }) };
        creditRequest.Headers.Add(CorrelationHeader, "credit-correlation");
        (await client.SendAsync(creditRequest)).EnsureSuccessStatusCode();
        var result = await Transfer(client, source.Id, destination.Id, "correlated", "original-correlation");
        result.EnsureSuccessStatusCode();
        Assert.Equal("original-correlation", result.Headers.GetValues(CorrelationHeader).Single());
        var replay = await Transfer(client, source.Id, destination.Id, "correlated", "replay-correlation");
        Assert.Equal("replay-correlation", replay.Headers.GetValues(CorrelationHeader).Single());
        Assert.Equal(await result.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());

        await using var db = api.Database();
        Assert.Equal("credit-correlation", (await db.AuditLogs.SingleAsync(x => x.MutationType == "Credit")).CorrelationId);
        var audits = await db.AuditLogs.Where(x => x.MutationType != "Credit").ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit => Assert.Equal("original-correlation", audit.CorrelationId));
        var message = await db.OutboxMessages.SingleAsync();
        Assert.Equal("original-correlation", message.CorrelationId);
        Assert.Equal("original-correlation", JsonDocument.Parse(message.Payload).RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ConcurrentRequestsKeepTheirOwnCorrelationIds()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var wallet = await Create(client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i => {
            var id = "parallel-" + i;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/wallets/{wallet.Id}/credits") {
                Content = JsonContent.Create(new { amountKobo = 1 }) };
            request.Headers.Add(CorrelationHeader, id);
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            Assert.Equal(id, response.Headers.GetValues(CorrelationHeader).Single());
            return id;
        }));
        await using var db = api.Database();
        Assert.Equal(responses.OrderBy(x => x), (await db.AuditLogs.Select(x => x.CorrelationId).ToListAsync()).OrderBy(x => x));
    }

    [Fact]
    public async Task ErrorResponsesCarryCorrelationAndTraceIncludingModelBindingErrors()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        const string correlation = "error-correlation";
        client.DefaultRequestHeaders.Add(CorrelationHeader, correlation);
        client.DefaultRequestHeaders.Add("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        using var malformed = await client.PostAsync($"/api/wallets/{Guid.NewGuid()}/credits",
            new StringContent("{\"amountKobo\":1.5}", Encoding.UTF8, "application/json"));
        using var missing = await client.GetAsync($"/api/wallets/{Guid.NewGuid()}/balance");
        foreach (var response in new[] { malformed, missing })
        {
            Assert.Equal(correlation, response.Headers.GetValues(CorrelationHeader).Single());
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(correlation, problem.GetProperty("correlationId").GetString());
            Assert.Equal("0123456789abcdef0123456789abcdef", problem.GetProperty("traceId").GetString());
        }
        using var anonymous = api.CreateClient();
        using var unauthorized = await anonymous.GetAsync($"/api/wallets/{Guid.NewGuid()}/balance");
        var body = await unauthorized.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(unauthorized.Headers.GetValues(CorrelationHeader).Single(), body.GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData("invalid identifier")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidCorrelationHeaderIsReplaced(string supplied)
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeader, supplied);
        using var response = await client.GetAsync("/health/live");
        var actual = response.Headers.GetValues(CorrelationHeader).Single();
        Assert.NotEqual(supplied, actual);
        Assert.True(Guid.TryParseExact(actual, "N", out _));
    }

    [Fact]
    public async Task RateLimitIsPerSubjectAndRejectsWithoutFinancialSideEffects()
    {
        await using var api = await fixture.CreateApiAsync(rateLimit: 2);
        using var client = api.AuthenticatedClient("limited");
        var source = await Create(client);
        var destination = await Create(client);
        (await client.PostAsJsonAsync($"/api/wallets/{source.Id}/credits", new { amountKobo = 100 })).EnsureSuccessStatusCode();
        (await Transfer(client, source.Id, destination.Id, "one", "one")).EnsureSuccessStatusCode();
        (await Transfer(client, source.Id, destination.Id, "two", "two")).EnsureSuccessStatusCode();
        using var rejected = await Transfer(client, source.Id, destination.Id, "three", "rejected-correlation");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limit_exceeded", problem.GetProperty("code").GetString());
        Assert.Equal("rejected-correlation", problem.GetProperty("correlationId").GetString());
        // Balance/credit endpoints are not subject to the transfer policy.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/wallets/{source.Id}/balance")).StatusCode);
        await using (var db = api.Database())
        {
            Assert.Equal(80, (await db.Wallets.FindAsync(source.Id))!.BalanceKobo);
            Assert.Equal(20, (await db.Wallets.FindAsync(destination.Id))!.BalanceKobo);
            Assert.Equal(2, await db.OutboxMessages.CountAsync());
            Assert.Equal(2, await db.IdempotencyRecords.CountAsync());
            Assert.Equal(5, await db.AuditLogs.CountAsync());
        }
        using var other = api.AuthenticatedClient("other-subject");
        (await Transfer(other, source.Id, destination.Id, "three", "other")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task OutboxFailureRollsBackEntireTransferAndLeavesKeyReusable()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var source = await Create(client);
        var destination = await Create(client);
        (await client.PostAsJsonAsync($"/api/wallets/{source.Id}/credits", new { amountKobo = 100 })).EnsureSuccessStatusCode();
        await using var db = api.Database();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.OutboxMessages ADD CONSTRAINT CK_TestRejectEvent CHECK (EventType <> 'TransferCompleted')");
        using var failed = await Transfer(client, source.Id, destination.Id, "outbox-failure", "outbox-failure");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("CK_TestRejectEvent", await failed.Content.ReadAsStringAsync());
        Assert.Equal(100, (await db.Wallets.FindAsync(source.Id))!.BalanceKobo);
        Assert.Equal(0, (await db.Wallets.FindAsync(destination.Id))!.BalanceKobo);
        Assert.Equal(1, await db.WalletTransactions.CountAsync());
        Assert.Equal(1, await db.AuditLogs.CountAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
        Assert.Empty(await db.IdempotencyRecords.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.OutboxMessages DROP CONSTRAINT CK_TestRejectEvent");
        (await Transfer(client, source.Id, destination.Id, "outbox-failure", "retry")).EnsureSuccessStatusCode();
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.Equal(1, await db.IdempotencyRecords.CountAsync());
    }
}
