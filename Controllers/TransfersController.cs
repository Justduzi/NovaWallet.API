using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NovaWallet.API.Infrastructure;
using NovaWallet.API.Contracts;
using NovaWallet.Domain;
using NovaWallet.Service;

namespace NovaWallet.API.Controllers;

[ApiController, Authorize, Route("api/transfers")]
public sealed class TransfersController(TransferService service, ILogger<TransfersController> logger) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(TransferRateLimitPolicy.Name)]
    [ProducesResponseType(typeof(TransferResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Transfer(TransferRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromServices] IValidator<TransferRequest> validator,
        [FromServices] IValidator<IdempotencyKeyRequest> keyValidator, CancellationToken ct)
    {
        if (Request.Headers["Idempotency-Key"].Count != 1)
            throw new LedgerException("validation", "Exactly one Idempotency-Key header is required.");
        var key = idempotencyKey?.Trim();
        await keyValidator.ValidateAndThrowAsync(new IdempotencyKeyRequest(key), ct);
        await validator.ValidateAndThrowAsync(request, ct);
        var result = await service.TransferAsync(request.SourceWalletId, request.DestinationWalletId,
            request.AmountKobo, key!, User.FindFirst("sub")?.Value, ct);
        logger.LogInformation("Transfer request completed for source {WalletId}", request.SourceWalletId);
        return new ContentResult { StatusCode = result.StatusCode, ContentType = "application/json", Content = result.ResponseBody };
    }
}
