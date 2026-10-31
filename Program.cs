using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SemanticCachingGateway.Filters;
using SemanticCachingGateway.Models;
using SemanticCachingGateway.Services;
using SemanticCachingGateway.Tests;

var builder = WebApplication.CreateBuilder(args);

// Check if running in self-test CLI mode
if (args.Contains("--self-test"))
{
    await GatewaySelfTests.RunAsync();
    return;
}

// Register AI Gateway Services
builder.Services.AddSingleton<ISemanticCacheService, SemanticCacheService>();
builder.Services.AddSingleton<ITokenRateLimiter, TokenRateLimiter>();
builder.Services.AddSingleton<ISingleflightService, SingleflightService>();
builder.Services.AddSingleton<IXFetchService, XFetchService>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<AiGatewayEndpointFilter>();

// Authentication & JWT Middleware
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = false,
            RequireSignedTokens = false
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Primary Chat Completions Endpoint protected by the AiGatewayEndpointFilter
app.MapPost("/v1/chat/completions", (ChatCompletionRequest request) =>
{
    return Results.Ok(new ChatCompletionResponse(
        Id: "chatcmpl-" + Guid.NewGuid().ToString("N")[..8],
        Text: $"Synthesized response for: {request.Prompt}",
        PromptTokens: Math.Max(1, request.Prompt.Length / 4),
        CompletionTokens: 50,
        TotalTokens: Math.Max(1, request.Prompt.Length / 4) + 50
    ));
})
.AddEndpointFilter<AiGatewayEndpointFilter>();

// Health & Metrics Endpoint
app.MapGet("/health", (GatewayMetrics metrics) =>
{
    return Results.Ok(new
    {
        status = "healthy",
        cacheHitRatio = $"{metrics.HitRatio * 100:F1}%",
        tokensSaved = metrics.TokensSaved,
        rateLimit429Count = metrics.Rejections429
    });
});

Console.WriteLine("AI Gateway listening on http://localhost:5000");
app.Run();
