using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;

namespace NovaWallet.Repositories;

public sealed class LedgerRepository(NovaWalletDbContext db) : ILedgerRepository
{
    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct) => await db.Database.BeginTransactionAsync(ct);
    public Task CommitAsync(CancellationToken ct) => db.Database.CurrentTransaction!.CommitAsync(ct);
    public async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }
                                          && ex.Entries.Any(e => e.Entity is Wallet && e.State == EntityState.Added))
        { throw new LedgerException("duplicate_customer", "A wallet already exists for this customer."); }
    }

    public async Task LockIdempotencyAsync(string key, CancellationToken ct)
    {
        var resource = "transfer-idempotency:" + key;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=15000;
            IF @result < 0 THROW 51001, 'Unable to acquire transfer lock.', 1;
            """, ct);
    }

    public Task<IdempotencyRecord?> FindIdempotencyAsync(string key, CancellationToken ct) =>
        db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);

    public Task<Wallet?> GetWalletAsync(Guid id, bool forUpdate, CancellationToken ct) => forUpdate
        ? db.Wallets.FromSqlInterpolated($"SELECT * FROM Wallets WITH (UPDLOCK, HOLDLOCK) WHERE Id = {id}").SingleOrDefaultAsync(ct)
        : db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<long> GetOutboundAsync(Guid walletId, DateTime startUtc, DateTime endUtc, CancellationToken ct) =>
        await db.WalletTransactions.Where(x => x.WalletId == walletId && x.Type == WalletTransactionType.TransferDebit
            && x.CreatedAtUtc >= startUtc && x.CreatedAtUtc < endUtc).SumAsync(x => (long?)x.AmountKobo, ct) ?? 0;

    public async Task<StatementPage> GetStatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.WalletTransactions.AsNoTracking().Where(x => x.WalletId == walletId);
        var total = await query.LongCountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip(checked((page - 1) * pageSize)).Take(pageSize).ToListAsync(ct);
        return new(page, pageSize, total, items);
    }

    public void Add(Wallet wallet) => db.Wallets.Add(wallet);
    public void Add(WalletTransaction transaction) => db.WalletTransactions.Add(transaction);
    public void Add(AuditLog audit) => db.AuditLogs.Add(audit);
    public void Add(IdempotencyRecord record) => db.IdempotencyRecords.Add(record);
    public void Add(OutboxMessage message) => db.OutboxMessages.Add(message);
}
