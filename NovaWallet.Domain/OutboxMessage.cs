namespace NovaWallet.Domain;

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransferReference { get; set; }
    public string EventType { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public string Payload { get; set; } = "";
    public string? CorrelationId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
}

public sealed record TransferCompleted(Guid EventId, Guid Reference, Guid SourceWalletId,
    Guid DestinationWalletId, long AmountKobo, string Currency, DateTime OccurredAtUtc, string? CorrelationId);

// HTTP-independent request context; a fresh instance is scoped to each request.
public sealed class OperationContext
{
    public string? CorrelationId { get; set; }
}
