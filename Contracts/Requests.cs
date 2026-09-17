using FluentValidation;

namespace NovaWallet.API.Contracts;

public sealed record CreateWalletRequest(string CustomerId);
public sealed record CreditRequest(long AmountKobo);
public sealed record TransferRequest(Guid SourceWalletId, Guid DestinationWalletId, long AmountKobo);
public sealed record StatementRequest(int Page = 1, int PageSize = 20);
public sealed record IdempotencyKeyRequest(string? Key);
public sealed record WalletResponse(Guid Id, string CustomerId, long BalanceKobo, string Currency);

public sealed class CreateWalletValidator : AbstractValidator<CreateWalletRequest>
{
    public CreateWalletValidator() => RuleFor(x => x.CustomerId).NotEmpty().MaximumLength(100);
}
public sealed class CreditValidator : AbstractValidator<CreditRequest>
{
    public CreditValidator() => RuleFor(x => x.AmountKobo).GreaterThan(0);
}
public sealed class TransferValidator : AbstractValidator<TransferRequest>
{
    public TransferValidator()
    {
        RuleFor(x => x.SourceWalletId).NotEmpty();
        RuleFor(x => x.DestinationWalletId).NotEmpty().NotEqual(x => x.SourceWalletId);
        RuleFor(x => x.AmountKobo).GreaterThan(0);
    }
}
public sealed class StatementValidator : AbstractValidator<StatementRequest>
{
    public StatementValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x).Must(x => ((long)x.Page - 1) * x.PageSize <= int.MaxValue)
            .WithMessage("Requested page is out of range.");
    }
}
public sealed class IdempotencyKeyValidator : AbstractValidator<IdempotencyKeyRequest>
{
    public IdempotencyKeyValidator() => RuleFor(x => x.Key).NotEmpty().MaximumLength(128);
}
