namespace SemanticCachingGateway.Filters;

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SemanticCachingGateway.Models;
using SemanticCachingGateway.Services;

public class GatewayMetrics
{
    private long _totalRequests = 0;
    private long _cacheHits = 0;
    private long _cacheMisses = 0;
    private long _rejected429 = 0;
    private long _tokensSaved = 0;

    public void RecordHit(int tokensSaved)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _cacheHits);
        Interlocked.Add(ref _tokensSaved, tokensSaved);
    }

    public void RecordMiss()
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _cacheMisses);
    }

    public void Record429()
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _rejected429);
    }

    public double HitRatio => _totalRequests == 0 ? 0.0 : (double)_cacheHits / _totalRequests;
    public long TokensSaved => _tokensSaved;
    public long Rejections429 => _rejected429;
}

public class AiGatewayEndpointFilter : IEndpointFilter
{
    private readonly ISemanticCacheService _cache;
    private readonly ITokenRateLimiter _rateLimiter;
    private readonly ISingleflightService _singleflight;
    private readonly GatewayMetrics _metrics;

    public AiGatewayEndpointFilter(
        ISemanticCacheService cache,
        ITokenRateLimiter rateLimiter,
        ISingleflightService singleflight,
        GatewayMetrics metrics)
    {
        _cache = cache;
        _rateLimiter = rateLimiter;
        _singleflight = singleflight;
        _metrics = metrics;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var cancellationToken = http.RequestAborted;

        // 1. Authenticated Identity & Tenancy
        var tenantClaim = http.User.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(tenantClaim))
        {
            return Results.Unauthorized();
        }

        var request = context.GetArgument<ChatCompletionRequest>(0);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Results.BadRequest(new { error = "Prompt cannot be empty" });
        }

        int promptTokens = Math.Max(1, request.Prompt.Length / 4);
        int maxOutput = request.MaxTokens ?? 1000;
        int estimatedTotal = promptTokens + maxOutput;

        // 2. Pre-Flight Token Reservation (RPM + TPM)
        var reservation = await _rateLimiter.ReserveAsync(tenantClaim, estimatedTotal);
        if (!reservation.Allowed)
        {
            _metrics.Record429();
            http.Response.Headers.RetryAfter = reservation.RetryAfterSeconds.ToString();
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // 3. Fast Vector Embedding Generation (1,536-dim normalized vector)
        var vector = _cache.GenerateNormalizedEmbedding(request.Prompt);

        // 4. Pre-Filtered Semantic Cache Lookup (Threshold <= 0.04)
        var match = await _cache.FindMatchAsync(tenantClaim, request.Prompt, vector, threshold: 0.04f);
        if (match is not null)
        {
            // Immediate Cache Hit: refund reserved output tokens immediately!
            await _rateLimiter.RefundAsync(tenantClaim, maxOutput);
            _metrics.RecordHit(tokensSaved: maxOutput);

            http.Response.Headers["X-Cache"] = "HIT-SEMANTIC";
            http.Response.Headers["X-Cache-Distance"] = match.Distance.ToString("F4");

            return Results.Ok(new ChatCompletionResponse(
                Id: "cache-" + Guid.NewGuid().ToString("N")[..8],
                Text: match.ResponsePayload,
                PromptTokens: promptTokens,
                CompletionTokens: 0,
                TotalTokens: promptTokens,
                Cached: true
            ));
        }

        // 5. Cache Miss: Execute Upstream with Singleflight Mutex
        _metrics.RecordMiss();
        int actualOutputTokens = 0;

        try
        {
            string upstreamOutput = await _singleflight.ExecuteAsync($"{tenantClaim}:{request.Prompt.Trim()}", async () =>
            {
                // Simulate LLM execution
                await Task.Delay(20, cancellationToken);
                return $"Processed response for: {request.Prompt}";
            });

            actualOutputTokens = Math.Max(1, upstreamOutput.Length / 4);

            // 6. Post-Stream True-Up: Refund unconsumed reservation margin
            int unconsumed = maxOutput - actualOutputTokens;
            if (unconsumed > 0)
            {
                await _rateLimiter.RefundAsync(tenantClaim, unconsumed);
            }

            // 7. Store in Cache asynchronously
            await _cache.StoreAsync(tenantClaim, request.Prompt, vector, upstreamOutput);

            return Results.Ok(new ChatCompletionResponse(
                Id: "gen-" + Guid.NewGuid().ToString("N")[..8],
                Text: upstreamOutput,
                PromptTokens: promptTokens,
                CompletionTokens: actualOutputTokens,
                TotalTokens: promptTokens + actualOutputTokens,
                Cached: false
            ));
        }
        catch (OperationCanceledException)
        {
            // Client disconnection mid-stream: refund full unconsumed output reservation
            await _rateLimiter.RefundAsync(tenantClaim, maxOutput);
            throw;
        }
    }
}
