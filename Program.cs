using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using NovaWallet.API.Contracts;
using NovaWallet.API.Infrastructure;
using NovaWallet.Domain;
using NovaWallet.Repositories;
using NovaWallet.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
    options.InvalidModelStateResponseFactory = context => {
        var problem = new ValidationProblemDetails(context.ModelState)
            { Status = 400, Title = "Invalid request.", Extensions = { ["code"] = "validation" } };
        ProblemDetailsMetadata.Enrich(context.HttpContext, problem);
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    });
builder.Services.AddValidatorsFromAssemblyContaining<CreateWalletValidator>();
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch {
        401 => "unauthorized", 403 => "forbidden", 404 => "not_found", _ => "http_error" });
    ProblemDetailsMetadata.Enrich(context.HttpContext, context.ProblemDetails);
});
builder.Services.AddScoped<OperationContext>();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddHealthChecks().AddCheck<LedgerHealthCheck>("sqlserver", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
builder.Services.AddOptions<TransferRateLimitOptions>().BindConfiguration("TransferRateLimit")
    .Validate(options => options.PermitLimit > 0 && options.WindowSeconds is > 0 and <= 86400,
        "Transfer rate limit requires positive permits and a window of 1 to 86400 seconds.").ValidateOnStart();
builder.Services.AddRateLimiter(options => options.AddPolicy<string, TransferRateLimitPolicy>(TransferRateLimitPolicy.Name));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<NovaWalletDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("NovaWallet") ?? throw new InvalidOperationException("ConnectionStrings:NovaWallet is required.")));
builder.Services.AddScoped<ILedgerRepository, LedgerRepository>();
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddOptions<WalletOptions>().BindConfiguration("WalletOptions")
    .Validate(options => options.DailyOutboundLimitKobo > 0, "Daily outbound limit must be positive.").ValidateOnStart();

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience) || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
    throw new InvalidOperationException("Configure Jwt:Issuer, Jwt:Audience and a signing key of at least 32 UTF-8 bytes.");
builder.Services.AddSingleton(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30), ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };
    options.Events = new JwtBearerEvents { OnTokenValidated = context => {
        var subject = context.Principal?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200) context.Fail("A subject of at most 200 characters is required.");
        return Task.CompletedTask;
    } };
});
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "NovaWallet Ledger API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>() });
});
var app = builder.Build();
await app.ApplyMigrationsAsync();
app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSwagger();
app.UseSwaggerUI();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapDevelopmentToken();
LedgerHealthCheck.MapEndpoints(app);
app.Run();

public partial class Program;
