using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Domain;

namespace NovaWallet.API.Infrastructure;

public sealed class LedgerExceptionHandler(ILogger<LedgerExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var code = exception switch { LedgerException e => e.Code, ValidationException => "validation", _ => "internal_error" };
        var status = code switch {
            "validation" => 400, "wallet_not_found" => 404,
            "duplicate_customer" or "idempotency_conflict" => 409,
            "insufficient_funds" or "daily_limit_exceeded" or "balance_overflow" => 422, _ => 500
        };
        if (status == 500) logger.LogError(exception, "Unhandled ledger request failure {TraceId}", context.TraceIdentifier);
        var problem = new ProblemDetails { Status = status, Title = status == 500 ? "An unexpected error occurred." : "Request could not be completed.",
            Detail = exception is LedgerException ? exception.Message : null, Instance = context.Request.Path };
        problem.Extensions["code"] = code;
        ProblemDetailsMetadata.Enrich(context, problem);
        if (exception is ValidationException validation)
            problem.Extensions["errors"] = validation.Errors.GroupBy(x => x.PropertyName)
                .ToDictionary(x => x.Key, x => x.Select(e => e.ErrorMessage).ToArray());
        await Results.Problem(problem).ExecuteAsync(context);
        return true;
    }
}
