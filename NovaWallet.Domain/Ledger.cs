namespace NovaWallet.Domain;

public enum WalletTransactionType { Credit = 1, TransferDebit = 2, TransferCredit = 3 }

public sealed class Wallet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CustomerId { get; set; } = "";
    public long BalanceKobo { get; set; }
    public string Currency { get; set; } = "NGN";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WalletTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WalletId { get; set; }
    public Guid Reference { get; set; }
    public WalletTransactionType Type { get; set; }
    public long AmountKobo { get; set; }
    public long BalanceBeforeKobo { get; set; }
    public long BalanceAfterKobo { get; set; }
    public Guid? CounterpartyWalletId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WalletId { get; set; }
    public Guid WalletTransactionId { get; set; }
    public string MutationType { get; set; } = "";
    public long DeltaKobo { get; set; }
    public long BalanceBeforeKobo { get; set; }
    public long BalanceAfterKobo { get; set; }
    public string? ActorSubject { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int ResponseStatusCode { get; set; }
    public string ResponseBody { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class LedgerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record StatementPage(int Page, int PageSize, long TotalCount, IReadOnlyList<WalletTransaction> Items);

// A scoped repository owns one unit of work; business decisions remain in Service.
public interface ILedgerRepository
{
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
    Task LockIdempotencyAsync(string key, CancellationToken ct);
    Task<IdempotencyRecord?> FindIdempotencyAsync(string key, CancellationToken ct);
    Task<Wallet?> GetWalletAsync(Guid id, bool forUpdate, CancellationToken ct);
    Task<long> GetOutboundAsync(Guid walletId, DateTime startUtc, DateTime endUtc, CancellationToken ct);
    Task<StatementPage> GetStatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct);
    void Add(Wallet wallet);
    void Add(WalletTransaction transaction);
    void Add(AuditLog audit);
    void Add(IdempotencyRecord record);
}
