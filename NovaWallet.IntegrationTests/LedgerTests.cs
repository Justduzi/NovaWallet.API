using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NovaWallet.API.Contracts;
using NovaWallet.Domain;
using NovaWallet.Service;

namespace NovaWallet.IntegrationTests;

public sealed class LedgerTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static async Task<WalletResponse> Create(HttpClient client, string? customer = null)
    {
        using var response = await client.PostAsJsonAsync("/api/wallets", new { customerId = customer ?? Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WalletResponse>())!;
    }

    private static async Task Credit(HttpClient client, Guid id, long amount)
    {
        using var response = await client.PostAsJsonAsync($"/api/wallets/{id}/credits", new { amountKobo = amount });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Transfer(HttpClient client, Guid source, Guid destination, long amount, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers") {
            Content = JsonContent.Create(new TransferRequest(source, destination, amount)) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
    }

    // All requests wait at the same gate; alternating hosts also exercises independent DI containers.
    private static async Task<HttpResponseMessage[]> Race(int count, Func<int, Task<HttpResponseMessage>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Enumerable.Range(0, count).Select(async i => { await gate.Task; return await action(i); }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(pending);
    }

    [Fact]
    public async Task TwentyTransfersAcrossTwoInstancesCannotOverspend()
    {
        await using var api = await fixture.CreateApiAsync();
        await using var second = api.SecondInstance();
        using var client = api.AuthenticatedClient();
        using var other = second.AuthenticatedClient();
        var source = await Create(client);
        var destination = await Create(client);
        await Credit(client, source.Id, 10_000_000);
        var responses = await Race(20, i => Transfer(i % 2 == 0 ? client : other, source.Id, destination.Id, 1_000_000, Guid.NewGuid().ToString()));
        Assert.Equal(10, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        var rejected = responses.Where(x => x.StatusCode != HttpStatusCode.OK).ToArray();
        Assert.Equal(10, rejected.Length);
        foreach (var response in rejected) await AssertError(response, HttpStatusCode.UnprocessableEntity, "insufficient_funds");
        await using var db = api.Database();
        Assert.Equal(0, (await db.Wallets.FindAsync(source.Id))!.BalanceKobo);
        Assert.Equal(10_000_000, (await db.Wallets.FindAsync(destination.Id))!.BalanceKobo);
        Assert.False(await db.Wallets.AnyAsync(x => x.BalanceKobo < 0));
        Assert.False(await db.WalletTransactions.AnyAsync(x => x.BalanceBeforeKobo < 0 || x.BalanceAfterKobo < 0));
        Assert.False(await db.AuditLogs.AnyAsync(x => x.BalanceBeforeKobo < 0 || x.BalanceAfterKobo < 0));
        Assert.Equal(10, await db.WalletTransactions.CountAsync(x => x.Type == WalletTransactionType.TransferDebit));
        Assert.Equal(10, await db.WalletTransactions.CountAsync(x => x.Type == WalletTransactionType.TransferCredit));
        Assert.Equal(20, await db.AuditLogs.CountAsync(x => x.MutationType != "Credit"));
        Assert.Equal(10, await db.IdempotencyRecords.CountAsync());
        Assert.Equal(10, await db.OutboxMessages.CountAsync());
        Assert.All(await db.AuditLogs.ToListAsync(), x => Assert.Equal("integration-actor", x.ActorSubject));
    }

    [Fact]
    public async Task ConcurrentIdempotencyReplaysExactlyOnceAcrossInstancesAndRestart()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        await using var second = api.SecondInstance();
        using var other = second.AuthenticatedClient();
        var source = await Create(client);
        var destination = await Create(client);
        await Credit(client, source.Id, 10_000_000);
        const string key = "same-key";
        var responses = await Race(20, i => Transfer(i % 2 == 0 ? client : other, source.Id, destination.Id, 1_000_000, key));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        await using var restarted = api.SecondInstance();
        using var freshClient = restarted.AuthenticatedClient();
        var replay = await Transfer(freshClient, source.Id, destination.Id, 1_000_000, "  same-key  ");
        Assert.Equal(bodies[0], await replay.Content.ReadAsStringAsync());
        await AssertError(await Transfer(client, source.Id, destination.Id, 2_000_000, key), HttpStatusCode.Conflict, "idempotency_conflict");
        await using var db = api.Database();
        Assert.Equal(9_000_000, (await db.Wallets.FindAsync(source.Id))!.BalanceKobo);
        Assert.Equal(1_000_000, (await db.Wallets.FindAsync(destination.Id))!.BalanceKobo);
        Assert.Equal(1, await db.WalletTransactions.CountAsync(x => x.Type == WalletTransactionType.TransferDebit));
        Assert.Equal(1, await db.WalletTransactions.CountAsync(x => x.Type == WalletTransactionType.TransferCredit));
        Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.MutationType != "Credit"));
        Assert.Equal(1, await db.IdempotencyRecords.CountAsync());
        var message = await db.OutboxMessages.SingleAsync();
        Assert.Equal(nameof(TransferCompleted), message.EventType);
        Assert.Equal(1, message.SchemaVersion);
        Assert.Null(message.PublishedAtUtc);
        var completed = JsonSerializer.Deserialize<TransferCompleted>(message.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var result = JsonSerializer.Deserialize<TransferResult>(bodies[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(message.Id, completed.EventId);
        Assert.Equal(result.Reference, completed.Reference);
        Assert.Equal(result.Reference, message.TransferReference);
        Assert.Equal(source.Id, completed.SourceWalletId);
        Assert.Equal(destination.Id, completed.DestinationWalletId);
        Assert.Equal(1_000_000, completed.AmountKobo);
        Assert.Equal("NGN", completed.Currency);
    }

    [Fact]
    public async Task ConcurrentDailyLimitCannotBeBypassed()
    {
        await using var api = await fixture.CreateApiAsync(3_000_000);
        using var client = api.AuthenticatedClient();
        var source = await Create(client);
        var destinations = new List<Guid>();
        for (var i = 0; i < 10; i++) destinations.Add((await Create(client)).Id);
        await Credit(client, source.Id, 20_000_000);
        var responses = await Race(10, i => Transfer(client, source.Id, destinations[i], 1_000_000, Guid.NewGuid().ToString()));
        Assert.Equal(3, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        foreach (var rejected in responses.Where(x => x.StatusCode != HttpStatusCode.OK))
            await AssertError(rejected, HttpStatusCode.UnprocessableEntity, "daily_limit_exceeded");
        await using var db = api.Database();
        Assert.Equal(3_000_000, await db.WalletTransactions.Where(x => x.Type == WalletTransactionType.TransferDebit).SumAsync(x => x.AmountKobo));
        Assert.Equal(17_000_000, (await db.Wallets.FindAsync(source.Id))!.BalanceKobo);
        Assert.Equal(3_000_000, await db.Wallets.Where(x => x.Id != source.Id).SumAsync(x => x.BalanceKobo));
        Assert.Equal(6, await db.AuditLogs.CountAsync(x => x.MutationType != "Credit"));
    }

    [Fact]
    public async Task AuditUpdateAndDeleteAreRejectedBySqlServer()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var wallet = await Create(client);
        await Credit(client, wallet.Id, 100);
        await using var db = api.Database();
        var update = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.AuditLogs SET DeltaKobo = 999"));
        var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.AuditLogs"));
        Assert.Equal(51000, update.Number);
        Assert.Equal(51000, delete.Number);
        Assert.Equal(100, (await db.AuditLogs.SingleAsync()).DeltaKobo);
    }

    [Fact]
    public async Task AuditWriteFailureRollsBackBalancesLedgerAndIdempotency()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var source = await Create(client);
        var destination = await Create(client);
        await Credit(client, source.Id, 100);
        await using var db = api.Database();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TestFailAudit ON dbo.AuditLogs AFTER INSERT AS THROW 51009, 'Test-only audit insertion failure.', 1;");
        var response = await Transfer(client, source.Id, destination.Id, 50, "rollback");
        await AssertError(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain("Test-only", await response.Content.ReadAsStringAsync());
        Assert.Equal(100, (await db.Wallets.FindAsync(source.Id))!.BalanceKobo);
        Assert.Equal(0, (await db.Wallets.FindAsync(destination.Id))!.BalanceKobo);
        Assert.Equal(1, await db.WalletTransactions.CountAsync());
        Assert.Equal(1, await db.AuditLogs.CountAsync());
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TestFailAudit");
        Assert.Equal(HttpStatusCode.OK, (await Transfer(client, source.Id, destination.Id, 50, "rollback")).StatusCode);
    }

    [Fact]
    public async Task DestinationOverflowRollsBackSourceAndDoesNotReserveKey()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var source = await Create(client);
        var destination = await Create(client);
        await Credit(client, source.Id, 100);
        await Credit(client, destination.Id, long.MaxValue);
        await AssertError(await Transfer(client, source.Id, destination.Id, 1, "overflow"), HttpStatusCode.UnprocessableEntity, "balance_overflow");
        using var credit = await client.PostAsJsonAsync($"/api/wallets/{destination.Id}/credits", new { amountKobo = 1 });
        await AssertError(credit, HttpStatusCode.UnprocessableEntity, "balance_overflow");
        await using var db = api.Database();
        Assert.Equal(100, (await db.Wallets.FindAsync(source.Id))!.BalanceKobo);
        Assert.Equal(long.MaxValue, (await db.Wallets.FindAsync(destination.Id))!.BalanceKobo);
        Assert.Equal(2, await db.WalletTransactions.CountAsync());
        Assert.Equal(2, await db.AuditLogs.CountAsync());
        Assert.Empty(await db.IdempotencyRecords.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task LimitResetsExactlyAtWatMidnight()
    {
        await using var api = await fixture.CreateApiAsync(100);
        using var client = api.AuthenticatedClient();
        var source = await Create(client);
        var destination = await Create(client);
        await Credit(client, source.Id, 1000);
        api.Clock.Set(DateTimeOffset.Parse("2026-09-17T22:59:59Z"));
        Assert.Equal(HttpStatusCode.OK, (await Transfer(client, source.Id, destination.Id, 100, "before-midnight")).StatusCode);
        await AssertError(await Transfer(client, source.Id, destination.Id, 1, "blocked"), HttpStatusCode.UnprocessableEntity, "daily_limit_exceeded");
        api.Clock.Set(DateTimeOffset.Parse("2026-09-17T23:00:00Z"));
        Assert.Equal(HttpStatusCode.OK, (await Transfer(client, source.Id, destination.Id, 100, "after-midnight")).StatusCode);
        await AssertError(await Transfer(client, source.Id, destination.Id, 1, "blocked"), HttpStatusCode.UnprocessableEntity, "daily_limit_exceeded");
    }

    [Fact]
    public async Task OpposingTransfersAndConcurrentCreditsConserveBalances()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var a = await Create(client);
        var b = await Create(client);
        await Credit(client, a.Id, 1000);
        await Credit(client, b.Id, 1000);
        var responses = await Race(20, i => i < 10
            ? Transfer(client, i % 2 == 0 ? a.Id : b.Id, i % 2 == 0 ? b.Id : a.Id, 10, Guid.NewGuid().ToString())
            : client.PostAsJsonAsync($"/api/wallets/{a.Id}/credits", new { amountKobo = 10 }));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var db = api.Database();
        Assert.Equal(1100, (await db.Wallets.FindAsync(a.Id))!.BalanceKobo);
        Assert.Equal(1000, (await db.Wallets.FindAsync(b.Id))!.BalanceKobo);
        Assert.Equal(32, await db.WalletTransactions.CountAsync());
        Assert.Equal(32, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task EndpointsRequireRealJwtAndProductionDoesNotIssueTokens()
    {
        await using var api = await fixture.CreateApiAsync();
        using var anonymous = api.CreateClient();
        var id = Guid.NewGuid();
        var responses = new[] {
            await anonymous.PostAsJsonAsync("/api/wallets", new { customerId = "anonymous" }),
            await anonymous.GetAsync($"/api/wallets/{id}/balance"),
            await anonymous.PostAsJsonAsync($"/api/wallets/{id}/credits", new { amountKobo = 1 }),
            await anonymous.GetAsync($"/api/wallets/{id}/statement"),
            await Transfer(anonymous, id, Guid.NewGuid(), 1, "anonymous") };
        foreach (var response in responses) await AssertError(response, HttpStatusCode.Unauthorized, "unauthorized");
        anonymous.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer invalid-token");
        await AssertError(await anonymous.GetAsync($"/api/wallets/{id}/balance"), HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync("/dev/token", null)).StatusCode);
    }

    [Fact]
    public async Task ValidationDuplicateCustomersAndMissingWalletUseProblemDetails()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var wallet = await Create(client, "customer");
        Assert.Equal(0, wallet.BalanceKobo);
        Assert.Equal("NGN", wallet.Currency);
        await AssertError(await client.PostAsJsonAsync("/api/wallets", new { customerId = "customer" }), HttpStatusCode.Conflict, "duplicate_customer");
        await AssertError(await client.PostAsJsonAsync("/api/wallets", new { customerId = " " }), HttpStatusCode.BadRequest, "validation");
        await AssertError(await client.PostAsync($"/api/wallets/{wallet.Id}/credits", new StringContent("{\"amountKobo\":1.5}", Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest, "validation");
        await AssertError(await client.GetAsync($"/api/wallets/{Guid.NewGuid()}/balance"), HttpStatusCode.NotFound, "wallet_not_found");
        await AssertError(await client.GetAsync($"/api/wallets/{wallet.Id}/statement?pageSize=101"), HttpStatusCode.BadRequest, "validation");
        await AssertError(await Transfer(client, wallet.Id, Guid.NewGuid(), 1, null), HttpStatusCode.BadRequest, "validation");
        await AssertError(await Transfer(client, wallet.Id, wallet.Id, 1, "same-wallet"), HttpStatusCode.BadRequest, "validation");
        await AssertError(await Transfer(client, wallet.Id, Guid.NewGuid(), 1, "missing-wallet"), HttpStatusCode.NotFound, "wallet_not_found");
    }

    [Fact]
    public async Task StatementPaginationIsStableWhenTimestampsTie()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var wallet = await Create(client);
        for (var i = 0; i < 5; i++) await Credit(client, wallet.Id, i + 1);
        var first = (await client.GetFromJsonAsync<StatementPage>($"/api/wallets/{wallet.Id}/statement?page=1&pageSize=3"))!;
        var second = (await client.GetFromJsonAsync<StatementPage>($"/api/wallets/{wallet.Id}/statement?page=2&pageSize=3"))!;
        Assert.Equal(5, first.TotalCount);
        Assert.Equal(3, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal(DateTimeKind.Utc, item.CreatedAtUtc.Kind));
        await using var db = api.Database();
        var orderedIds = await db.WalletTransactions.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Select(x => x.Id).ToListAsync();
        Assert.Equal(orderedIds, first.Items.Concat(second.Items).Select(x => x.Id));
        var repeated = (await client.GetFromJsonAsync<StatementPage>($"/api/wallets/{wallet.Id}/statement?page=1&pageSize=3"))!;
        Assert.Equal(first.Items.Select(x => x.Id), repeated.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task CaseDistinctIdempotencyKeysAreDistinctInBothLockAndDatabase()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var source = await Create(client);
        var destination = await Create(client);
        await Credit(client, source.Id, 100);
        var results = await Race(2, i => Transfer(client, source.Id, destination.Id, 10, i == 0 ? "KEY" : "key"));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var db = api.Database();
        Assert.Equal(2, await db.IdempotencyRecords.CountAsync());
        Assert.Equal(80, (await db.Wallets.FindAsync(source.Id))!.BalanceKobo);
    }

    [Fact]
    public async Task ConcurrentDuplicateCustomerCreatesOneWallet()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var responses = await Race(10, _ => client.PostAsJsonAsync("/api/wallets", new { customerId = "one-customer" }));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
            await AssertError(response, HttpStatusCode.Conflict, "duplicate_customer");
        await using var db = api.Database();
        Assert.Equal(1, await db.Wallets.CountAsync());
    }

    [Fact]
    public async Task DatabaseRejectsNegativeBalanceAndInvalidLedgerAmounts()
    {
        await using var api = await fixture.CreateApiAsync();
        using var client = api.AuthenticatedClient();
        var wallet = await Create(client);
        await Credit(client, wallet.Id, 10);
        await using var db = api.Database();
        var balance = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.Wallets SET BalanceKobo = -1"));
        var amount = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.WalletTransactions SET AmountKobo = 0"));
        Assert.Equal(547, balance.Number);
        Assert.Equal(547, amount.Number);
        Assert.Equal(10, (await db.Wallets.SingleAsync()).BalanceKobo);
        Assert.Equal(10, (await db.WalletTransactions.SingleAsync()).AmountKobo);
    }
}
