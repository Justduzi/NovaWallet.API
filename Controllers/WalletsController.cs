using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.API.Contracts;
using NovaWallet.Domain;
using NovaWallet.Service;

namespace NovaWallet.API.Controllers;

[ApiController, Authorize, Route("api/wallets")]
public sealed class WalletsController(WalletService service) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<WalletResponse>> Create(CreateWalletRequest request,
        [FromServices] IValidator<CreateWalletRequest> validator, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var wallet = await service.CreateAsync(request.CustomerId, ct);
        return CreatedAtAction(nameof(Balance), new { walletId = wallet.Id }, ToResponse(wallet));
    }

    [HttpGet("{walletId:guid}/balance")]
    public async Task<ActionResult<WalletResponse>> Balance(Guid walletId, CancellationToken ct) =>
        Ok(ToResponse(await service.BalanceAsync(walletId, ct)));

    [HttpPost("{walletId:guid}/credits")]
    public async Task<ActionResult<WalletTransaction>> Credit(Guid walletId, CreditRequest request,
        [FromServices] IValidator<CreditRequest> validator, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        return Ok(await service.CreditAsync(walletId, request.AmountKobo, User.FindFirst("sub")?.Value, ct));
    }

    [HttpGet("{walletId:guid}/statement")]
    public async Task<ActionResult<StatementPage>> Statement(Guid walletId,
        [FromServices] IValidator<StatementRequest> validator, CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        await validator.ValidateAndThrowAsync(new(page, pageSize), ct);
        return Ok(await service.StatementAsync(walletId, page, pageSize, ct));
    }

    private static WalletResponse ToResponse(Wallet wallet) => new(wallet.Id, wallet.CustomerId, wallet.BalanceKobo, wallet.Currency);
}

