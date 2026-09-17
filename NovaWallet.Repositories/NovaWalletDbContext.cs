using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NovaWallet.Domain;

namespace NovaWallet.Repositories;

public sealed class NovaWalletDbContext(DbContextOptions<NovaWalletDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var wallet = model.Entity<Wallet>();
        wallet.ToTable("Wallets", t => {
            t.HasCheckConstraint("CK_Wallet_Balance", "[BalanceKobo] >= 0");
            t.HasCheckConstraint("CK_Wallet_Currency", "[Currency] = 'NGN'");
        });
        wallet.HasKey(x => x.Id);
        wallet.Property(x => x.CustomerId).HasMaxLength(100).IsRequired();
        wallet.HasIndex(x => x.CustomerId).IsUnique();
        wallet.Property(x => x.Currency).HasColumnType("char(3)").HasDefaultValue("NGN");

        var transaction = model.Entity<WalletTransaction>();
        transaction.ToTable("WalletTransactions", t => {
            t.HasCheckConstraint("CK_Transaction_Amount", "[AmountKobo] > 0");
            t.HasCheckConstraint("CK_Transaction_Before", "[BalanceBeforeKobo] >= 0");
            t.HasCheckConstraint("CK_Transaction_After", "[BalanceAfterKobo] >= 0");
        });
        transaction.HasKey(x => x.Id);
        transaction.HasOne<Wallet>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
        transaction.HasOne<Wallet>().WithMany().HasForeignKey(x => x.CounterpartyWalletId).OnDelete(DeleteBehavior.Restrict);
        transaction.HasIndex(x => new { x.WalletId, x.CreatedAtUtc, x.Id }).IsDescending(false, true, true);
        transaction.HasIndex(x => new { x.WalletId, x.Type, x.CreatedAtUtc });
        transaction.HasIndex(x => x.Reference);

        var audit = model.Entity<AuditLog>();
        // SQL Server disallows OUTPUT without INTO on tables with enabled triggers.
        audit.ToTable("AuditLogs", t => { t.HasTrigger("TR_AuditLogs_Immutable"); t.UseSqlOutputClause(false); });
        audit.HasKey(x => x.Id);
        audit.Property(x => x.MutationType).HasMaxLength(50).IsRequired();
        audit.Property(x => x.ActorSubject).HasMaxLength(200);
        audit.Property(x => x.CorrelationId).HasMaxLength(100);
        audit.HasOne<Wallet>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<WalletTransaction>().WithMany().HasForeignKey(x => x.WalletTransactionId).OnDelete(DeleteBehavior.Restrict);
        audit.HasIndex(x => x.WalletTransactionId).IsUnique();

        var idempotency = model.Entity<IdempotencyRecord>();
        idempotency.ToTable("IdempotencyRecords");
        idempotency.HasKey(x => x.Id);
        // Match SQL key equality to the case-sensitive application-lock resource.
        idempotency.Property(x => x.IdempotencyKey).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        idempotency.HasIndex(x => x.IdempotencyKey).IsUnique();
        idempotency.Property(x => x.RequestHash).HasColumnType("char(64)").IsRequired();
        idempotency.Property(x => x.ResponseBody).IsRequired();

        var outbox = model.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages", t => t.HasCheckConstraint("CK_Outbox_SchemaVersion", "[SchemaVersion] > 0"));
        outbox.HasKey(x => x.Id);
        outbox.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        outbox.Property(x => x.Payload).IsRequired();
        outbox.Property(x => x.CorrelationId).HasMaxLength(100);
        outbox.HasIndex(x => x.TransferReference).IsUnique();
        outbox.HasIndex(x => new { x.CreatedAtUtc, x.Id }).HasFilter("[PublishedAtUtc] IS NULL");

        // datetime2 stores UTC clock values but not DateTime.Kind; restore the UTC marker on reads.
        var utc = new ValueConverter<DateTime, DateTime>(value => value,
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime)))
                property.SetValueConverter(utc);
    }
}
