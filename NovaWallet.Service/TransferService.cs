using System.Text.Json;
using Microsoft.Extensions.Options;
using NovaWallet.Domain;

namespace NovaWallet.Service;

public sealed class TransferService(ILedgerRepository repository, TimeProvider clock, IOptions<WalletOptions> options, OperationContext context)
{
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);

    public async Task<StoredTransferResult> TransferAsync(Guid sourceId, Guid destinationId, long amountKobo,
        string key, string? actor, CancellationToken ct)
    {
        key = key?.Trim() ?? "";
        if (sourceId == Guid.Empty || destinationId == Guid.Empty || sourceId == destinationId || amountKobo <= 0
            || key.Length is < 1 or > 128)
            throw new LedgerException("validation", "Invalid transfer request or idempotency key.");

        var hash = TransferRules.RequestHash(sourceId, destinationId, amountKobo);
        await using var transaction = await repository.BeginTransactionAsync(ct);
        await repository.LockIdempotencyAsync(key, ct);
        var previous = await repository.FindIdempotencyAsync(key, ct);
        if (previous is not null)
        {
            if (previous.RequestHash != hash)
                throw new LedgerException("idempotency_conflict", "This idempotency key was used with a different transfer payload.");
            await repository.CommitAsync(ct);
            // Return exactly the stored body and status, including original balance snapshots.
            return new(previous.ResponseStatusCode, previous.ResponseBody);
        }

        // Separate queries ensure lock acquisition order; do not rely on an IN query's plan.
        var ids = new[] { sourceId, destinationId }.OrderBy(x => x).ToArray();
        var first = await repository.GetWalletAsync(ids[0], true, ct) ?? throw WalletService.MissingWallet();
        var second = await repository.GetWalletAsync(ids[1], true, ct) ?? throw WalletService.MissingWallet();
        var source = first.Id == sourceId ? first : second;
        var destination = first.Id == destinationId ? first : second;
        if (source.BalanceKobo < amountKobo)
            throw new LedgerException("insufficient_funds", "The source wallet has insufficient funds.");

        // Capture the time after waiting for locks, including requests spanning WAT midnight.
        var now = clock.GetUtcNow();
        var (start, end) = TransferRules.WatDay(now);
        var sent = await repository.GetOutboundAsync(sourceId, start, end, ct);
        if (!TransferRules.WithinDailyLimit(sent, amountKobo, options.Value.DailyOutboundLimitKobo))
            throw new LedgerException("daily_limit_exceeded", "The daily outbound transfer limit would be exceeded.");

        var reference = Guid.NewGuid();
        WalletService.RecordMutation(repository, source, amountKobo, WalletTransactionType.TransferDebit,
            reference, destinationId, actor, now.UtcDateTime, context.CorrelationId);
        WalletService.RecordMutation(repository, destination, amountKobo, WalletTransactionType.TransferCredit,
            reference, sourceId, actor, now.UtcDateTime, context.CorrelationId);
        var result = new TransferResult(reference, sourceId, destinationId, amountKobo,
            source.BalanceKobo, destination.BalanceKobo, now.UtcDateTime);
        var body = JsonSerializer.Serialize(result, ResponseJson);
        repository.Add(new IdempotencyRecord { IdempotencyKey = key, RequestHash = hash, ResponseStatusCode = 200,
            ResponseBody = body, CreatedAtUtc = now.UtcDateTime });
        var eventId = Guid.NewGuid();
        var completed = new TransferCompleted(eventId, reference, sourceId, destinationId, amountKobo,
            source.Currency, now.UtcDateTime, context.CorrelationId);
        repository.Add(new OutboxMessage { Id = eventId, TransferReference = reference,
            EventType = nameof(TransferCompleted), Payload = JsonSerializer.Serialize(completed, ResponseJson),
            CorrelationId = context.CorrelationId, CreatedAtUtc = now.UtcDateTime });
        await repository.SaveAsync(ct);
        await repository.CommitAsync(ct);
        return new(200, body);
    }
}
