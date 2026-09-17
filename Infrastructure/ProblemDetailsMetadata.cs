using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Domain;

namespace NovaWallet.API.Infrastructure;

public static class ProblemDetailsMetadata
{
    public static void Enrich(HttpContext context, ProblemDetails problem)
    {
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        problem.Extensions["correlationId"] = context.RequestServices.GetRequiredService<OperationContext>().CorrelationId;
    }
}
