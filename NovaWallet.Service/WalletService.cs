using NovaWallet.Domain;

namespace NovaWallet.Service;

public sealed class WalletService(ILedgerRepository repository, TimeProvider clock)
{
    public async Task<Wallet> CreateAsync(string customerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId) || customerId.Trim().Length > 100)
            throw new LedgerException("validation", "Customer ID must contain 1 to 100 characters.");
        var now = clock.GetUtcNow().UtcDateTime;
        var wallet = new Wallet { CustomerId = customerId.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now };
        repository.Add(wallet);
        await repository.SaveAsync(ct);
        return wallet;
    }

    public async Task<Wallet> BalanceAsync(Guid walletId, CancellationToken ct) =>
        await repository.GetWalletAsync(walletId, false, ct) ?? throw MissingWallet();

    public async Task<WalletTransaction> CreditAsync(Guid walletId, long amountKobo, string? actor, CancellationToken ct)
    {
        if (amountKobo <= 0) throw new LedgerException("validation", "Amount must be positive.");
        await using var transaction = await repository.BeginTransactionAsync(ct);
        var wallet = await repository.GetWalletAsync(walletId, true, ct) ?? throw MissingWallet();
        var mutation = RecordMutation(repository, wallet, amountKobo, WalletTransactionType.Credit,
            Guid.NewGuid(), null, actor, clock.GetUtcNow().UtcDateTime);
        await repository.SaveAsync(ct);
        await repository.CommitAsync(ct);
        return mutation;
    }

    public async Task<StatementPage> StatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
            throw new LedgerException("validation", "Pagination is out of range.");
        _ = await BalanceAsync(walletId, ct);
        return await repository.GetStatementAsync(walletId, page, pageSize, ct);
    }

    internal static LedgerException MissingWallet() => new("wallet_not_found", "Wallet was not found.");

    internal static WalletTransaction RecordMutation(ILedgerRepository repository, Wallet wallet, long amount,
        WalletTransactionType type, Guid reference, Guid? counterparty, string? actor, DateTime now)
    {
        var before = wallet.BalanceKobo;
        var delta = type == WalletTransactionType.TransferDebit ? -amount : amount;
        long after;
        try { after = checked(before + delta); }
        catch (OverflowException) { throw new LedgerException("balance_overflow", "The resulting balance exceeds the supported range."); }
        if (after < 0) throw new LedgerException("insufficient_funds", "The source wallet has insufficient funds.");
        wallet.BalanceKobo = after;
        wallet.UpdatedAtUtc = now;
        var entry = new WalletTransaction { WalletId = wallet.Id, Reference = reference, Type = type,
            AmountKobo = amount, BalanceBeforeKobo = before, BalanceAfterKobo = after,
            CounterpartyWalletId = counterparty, CreatedAtUtc = now };
        repository.Add(entry);
        repository.Add(new AuditLog { WalletId = wallet.Id, WalletTransactionId = entry.Id,
            MutationType = type.ToString(), DeltaKobo = delta, BalanceBeforeKobo = before,
            BalanceAfterKobo = after, ActorSubject = actor, CreatedAtUtc = now });
        return entry;
    }
}
