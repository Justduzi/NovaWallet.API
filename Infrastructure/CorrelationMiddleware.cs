using System.Diagnostics;
using NovaWallet.Domain;

namespace NovaWallet.API.Infrastructure;

public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext http, OperationContext operation)
    {
        var values = http.Request.Headers[HeaderName];
        var supplied = values.Count == 1 ? values[0] : null;
        var correlationId = IsValid(supplied) ? supplied! : Guid.NewGuid().ToString("N");
        operation.CorrelationId = correlationId;
        http.Response.OnStarting(() => {
            http.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object?> {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier
        });
        var started = Stopwatch.GetTimestamp();
        try { await next(http); }
        finally
        {
            logger.LogInformation("HTTP {Method} {Path} completed with {StatusCode} in {ElapsedMilliseconds} ms",
                http.Request.Method, http.Request.Path, http.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    // Bounded printable identifiers keep caller-controlled values safe for logs and SQL columns.
    public static bool IsValid(string? value) => value is { Length: > 0 and <= 100 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
