using NovaWallet.API.Contracts;

namespace NovaWallet.UnitTests;

public class ValidationTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(-1, false)] [InlineData(1, true)] [InlineData(long.MaxValue, true)]
    public void CreditRequiresPositiveInteger(long amount, bool valid) =>
        Assert.Equal(valid, new CreditValidator().Validate(new CreditRequest(amount)).IsValid);

    [Fact]
    public void TransferRejectsEmptyAndIdenticalWallets()
    {
        var id = Guid.NewGuid();
        var validator = new TransferValidator();
        Assert.False(validator.Validate(new TransferRequest(id, id, 1)).IsValid);
        Assert.False(validator.Validate(new TransferRequest(Guid.Empty, id, 1)).IsValid);
        Assert.True(validator.Validate(new TransferRequest(id, Guid.NewGuid(), 1)).IsValid);
    }

    [Theory]
    [InlineData(0, 20)] [InlineData(1, 101)] [InlineData(1, 0)] [InlineData(int.MaxValue, 100)]
    public void PaginationRejectsInvalidAndOverflowingOffsets(int page, int size) =>
        Assert.False(new StatementValidator().Validate(new StatementRequest(page, size)).IsValid);

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    public void RequiredFieldsRejectBlankValues(string? value)
    {
        Assert.False(new CreateWalletValidator().Validate(new CreateWalletRequest(value!)).IsValid);
        Assert.False(new IdempotencyKeyValidator().Validate(new IdempotencyKeyRequest(value)).IsValid);
    }

    [Fact]
    public void TextLengthsAreBounded()
    {
        Assert.False(new CreateWalletValidator().Validate(new CreateWalletRequest(new string('a', 101))).IsValid);
        Assert.False(new IdempotencyKeyValidator().Validate(new IdempotencyKeyRequest(new string('a', 129))).IsValid);
    }
}

