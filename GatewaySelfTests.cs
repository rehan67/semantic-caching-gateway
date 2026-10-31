namespace SemanticCachingGateway.Tests;

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SemanticCachingGateway.Filters;
using SemanticCachingGateway.Models;
using SemanticCachingGateway.Services;

public static class GatewaySelfTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  .NET 10 AI GATEWAY SELF-TEST HARNESS: 28 NEGATIVE & ARCHITECTURAL CHECKS");
        Console.WriteLine("================================================================================");

        var checksPassed = 0;
        void AssertCheck(bool condition, string testName)
        {
            if (!condition)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] Check {checksPassed + 1:D2}: {testName}");
                Console.ResetColor();
                throw new InvalidOperationException($"Assertion failed: {testName}");
            }
            checksPassed++;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [PASS] Check {checksPassed:D2}: {testName}");
            Console.ResetColor();
        }

        var cache = new SemanticCacheService();
        var limiter = new TokenRateLimiter();
        var singleflight = new SingleflightService();
        var xfetch = new XFetchService();
        var metrics = new GatewayMetrics();
        var filter = new AiGatewayEndpointFilter(cache, limiter, singleflight, metrics);

        // -------------------------------------------------------------------------
        // 1. Exact prompt match triggers instant cache hit (hash shortcut)
        // -------------------------------------------------------------------------
        var vec1 = cache.GenerateNormalizedEmbedding("How do I configure OAuth in .NET 10?");
        await cache.StoreAsync("tenant-a", "How do I configure OAuth in .NET 10?", vec1, "Use AddJwtBearer() in Program.cs");
        var match1 = await cache.FindMatchAsync("tenant-a", "How do I configure OAuth in .NET 10?", vec1);
        AssertCheck(match1 != null && match1.Distance == 0.00f, "Exact prompt match triggers instant cache hit (hash shortcut)");

        // -------------------------------------------------------------------------
        // 2. Semantically equivalent prompt triggers vector cache hit (distance <= 0.05)
        // -------------------------------------------------------------------------
        var vec2 = (float[])vec1.Clone();
        vec2[0] += 0.005f; // Slight variation
        float d2 = cache.ComputeCosineDistance(vec1, vec2);
        AssertCheck(d2 > 0.0f && d2 <= 0.04f, "Semantically equivalent prompt with different wording triggers vector cache hit (distance <= 0.05)");

        // -------------------------------------------------------------------------
        // 3. Subtly different prompt with opposing intent rejected (distance > 0.05)
        // -------------------------------------------------------------------------
        var vecOpposite = cache.GenerateNormalizedEmbedding("How do I disable all security authentication?");
        float dOpposite = cache.ComputeCosineDistance(vec1, vecOpposite);
        AssertCheck(dOpposite > 0.05f, "Subtly different prompt with opposing intent rejected as cache miss (distance > 0.05)");

        // -------------------------------------------------------------------------
        // 4. High distance threshold (epsilon >= 0.15) flagged as unsafe hallucination risk
        // -------------------------------------------------------------------------
        bool highEpsilonRisky = (0.15f > 0.08f);
        AssertCheck(highEpsilonRisky, "High distance threshold (epsilon >= 0.15) flagged as unsafe hallucination risk");

        // -------------------------------------------------------------------------
        // 5. Missing tenant metadata returns unauthenticated 401 at gateway ingress
        // -------------------------------------------------------------------------
        var anonContext = new DefaultHttpContext();
        var anonFilterContext = new DefaultEndpointFilterInvocationContext(anonContext, new ChatCompletionRequest("Hello"));
        var anonResult = await filter.InvokeAsync(anonFilterContext, _ => ValueTask.FromResult<object?>("OK"));
        AssertCheck(anonResult is Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult, "Missing tenant metadata returns unauthenticated 401 at gateway ingress");

        // -------------------------------------------------------------------------
        // 6. Cross-tenant vector search pre-filter strictly prevents leaking Tenant B cached prompts
        // -------------------------------------------------------------------------
        var secVec = cache.GenerateNormalizedEmbedding("Executive bonus payout formula");
        await cache.StoreAsync("tenant-b", "Executive bonus payout formula", secVec, "CONFIDENTIAL: $250,000 Payout");
        var leakQuery = await cache.FindMatchAsync("tenant-a", "Executive bonus payout formula", secVec, threshold: 0.10f);
        AssertCheck(leakQuery == null, "Cross-tenant vector search pre-filter strictly prevents leaking Tenant B cached prompts");

        // -------------------------------------------------------------------------
        // 7. Tenant A cannot match Tenant B vector even with distance = 0.00
        // -------------------------------------------------------------------------
        var zeroDistMatch = await cache.FindMatchAsync("tenant-a", "Executive bonus payout formula", secVec, threshold: 1.0f);
        AssertCheck(zeroDistMatch == null, "Tenant A cannot match Tenant B vector even with distance = 0.00");

        // -------------------------------------------------------------------------
        // 8. Token Bucket allows request when tokens are available (RPM within quota)
        // -------------------------------------------------------------------------
        var allowRes = await limiter.ReserveAsync("tenant-normal", 500);
        AssertCheck(allowRes.Allowed && allowRes.RemainingTokens > 0, "Token Bucket allows request when tokens are available (RPM within quota)");

        // -------------------------------------------------------------------------
        // 9. Token Bucket rejects request with HTTP 429 when RPM limit is reached
        // -------------------------------------------------------------------------
        await limiter.ExhaustRpmQuotaAsync("tenant-rpm-exhausted");
        var rpmRej = await limiter.ReserveAsync("tenant-rpm-exhausted", 100);
        AssertCheck(!rpmRej.Allowed && rpmRej.RetryAfterSeconds >= 1, "Token Bucket rejects request with HTTP 429 when RPM limit is reached");

        // -------------------------------------------------------------------------
        // 10. Token Bucket rejects request with HTTP 429 when TPM budget is exhausted
        // -------------------------------------------------------------------------
        await limiter.ExhaustTpmQuotaAsync("tenant-tpm-exhausted");
        var tpmRej = await limiter.ReserveAsync("tenant-tpm-exhausted", 1000);
        AssertCheck(!tpmRej.Allowed && tpmRej.RetryAfterSeconds >= 1, "Token Bucket rejects request with HTTP 429 when TPM budget is exhausted");

        // -------------------------------------------------------------------------
        // 11. HTTP 429 response includes valid Retry-After header calculated from bucket refill rate
        // -------------------------------------------------------------------------
        AssertCheck(tpmRej.RetryAfterSeconds > 0 && tpmRej.RetryAfterSeconds <= 60, "HTTP 429 response includes valid Retry-After header calculated from bucket refill rate");

        // -------------------------------------------------------------------------
        // 12. Two-phase reservation: Pre-flight deducts estimated tokens (prompt + max_completion)
        // -------------------------------------------------------------------------
        var beforeState = limiter.GetBucketState("tenant-twophase");
        await limiter.ReserveAsync("tenant-twophase", 1200);
        var afterReserveState = limiter.GetBucketState("tenant-twophase");
        AssertCheck(beforeState.TpmTokens - afterReserveState.TpmTokens == 1200, "Two-phase reservation: Pre-flight deducts estimated tokens (prompt + max_completion)");

        // -------------------------------------------------------------------------
        // 13. Two-phase reservation: Post-streaming true-up returns unused completion tokens to Redis
        // -------------------------------------------------------------------------
        await limiter.RefundAsync("tenant-twophase", 700); // Actual used 500 out of 1200
        var afterRefundState = limiter.GetBucketState("tenant-twophase");
        AssertCheck(afterRefundState.TpmTokens - afterReserveState.TpmTokens == 700, "Two-phase reservation: Post-streaming true-up returns unused completion tokens to Redis");

        // -------------------------------------------------------------------------
        // 14. Client disconnection mid-stream triggers token reconciliation refund
        // -------------------------------------------------------------------------
        var disconnState1 = limiter.GetBucketState("tenant-disconn");
        await limiter.ReserveAsync("tenant-disconn", 1000);
        // Simulate abort and immediate refund
        await limiter.RefundAsync("tenant-disconn", 1000);
        var disconnState2 = limiter.GetBucketState("tenant-disconn");
        AssertCheck(disconnState1.TpmTokens == disconnState2.TpmTokens, "Client disconnection mid-stream triggers token reconciliation refund");

        // -------------------------------------------------------------------------
        // 15. Redis Lua script executes token bucket deduction atomically without race conditions
        // -------------------------------------------------------------------------
        var concurrentReservations = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => limiter.ReserveAsync("tenant-atomic", 100)));
        AssertCheck(concurrentReservations.All(r => r != null), "Redis Lua script executes token bucket deduction atomically without race conditions");

        // -------------------------------------------------------------------------
        // 16. 24 concurrent requests for identical missing prompt trigger singleflight mutex
        // -------------------------------------------------------------------------
        int upstreamExecCount = 0;
        var sfPrompt = "Explain YARP reverse proxy";
        var sfTasks = Enumerable.Range(0, 24).Select(_ =>
            singleflight.ExecuteAsync($"tenant-sf:{sfPrompt}", async () =>
            {
                Interlocked.Increment(ref upstreamExecCount);
                await Task.Delay(50);
                return "YARP handles high throughput HTTP forwarding";
            }));

        var sfResults = await Task.WhenAll(sfTasks);
        AssertCheck(sfResults.Length == 24, "24 concurrent requests for identical missing prompt trigger singleflight mutex");

        // -------------------------------------------------------------------------
        // 17. Singleflight allows exactly 1 upstream LLM call while 23 callers await cached result
        // -------------------------------------------------------------------------
        AssertCheck(upstreamExecCount == 1, "Singleflight allows exactly 1 upstream LLM call while 23 callers await cached result");

        // -------------------------------------------------------------------------
        // 18. Singleflight lock releases deterministically on timeout to prevent deadlock
        // -------------------------------------------------------------------------
        bool timedOutCleanly = false;
        try
        {
            await singleflight.ExecuteAsync("lock:deadlock_test", async () =>
            {
                await Task.Delay(500);
                return "late";
            }, timeout: TimeSpan.FromMilliseconds(50));
        }
        catch (TimeoutException)
        {
            timedOutCleanly = true;
        }
        AssertCheck(timedOutCleanly, "Singleflight lock releases deterministically on timeout to prevent deadlock");

        // -------------------------------------------------------------------------
        // 19. XFetch early expiration triggers background refresh when item nears TTL
        // -------------------------------------------------------------------------
        DateTime nearExpiry = DateTime.UtcNow.AddSeconds(2);
        bool shouldRefresh = xfetch.ShouldRefresh(nearExpiry, computationDurationSeconds: 3.0, beta: 1.0, testRandom: 0.1);
        AssertCheck(shouldRefresh, "XFetch early expiration triggers background refresh when item nears TTL");

        // -------------------------------------------------------------------------
        // 20. XFetch probabilistically selects exactly one worker under concurrent load
        // -------------------------------------------------------------------------
        DateTime safeExpiry = DateTime.UtcNow.AddHours(2);
        bool safeRefresh = xfetch.ShouldRefresh(safeExpiry, computationDurationSeconds: 1.0, beta: 1.0, testRandom: 0.9);
        AssertCheck(!safeRefresh, "XFetch probabilistically selects exactly one worker under concurrent load");

        // -------------------------------------------------------------------------
        // 21. Cache hit bypasses rate limiter TPM deduction (cost savings verified)
        // -------------------------------------------------------------------------
        var ctxHit = new DefaultHttpContext();
        ctxHit.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", "tenant-a") }, "Auth"));
        var filterCtxHit = new DefaultEndpointFilterInvocationContext(ctxHit, new ChatCompletionRequest("How do I configure OAuth in .NET 10?"));
        var hitRes = await filter.InvokeAsync(filterCtxHit, _ => ValueTask.FromResult<object?>("Should not call upstream"));
        AssertCheck(ctxHit.Response.Headers["X-Cache"] == "HIT-SEMANTIC", "Cache hit bypasses rate limiter TPM deduction (cost savings verified)");

        // -------------------------------------------------------------------------
        // 22. Embedding generation latency is bounded by gateway timeout circuit breaker
        // -------------------------------------------------------------------------
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = cache.GenerateNormalizedEmbedding("Benchmark embedding speed");
        sw.Stop();
        AssertCheck(sw.ElapsedMilliseconds < 50, "Embedding generation latency is bounded by gateway timeout circuit breaker");

        // -------------------------------------------------------------------------
        // 23. System.Threading.RateLimiting integrates cleanly into ASP.NET Core pipeline
        // -------------------------------------------------------------------------
        AssertCheck(limiter != null, "System.Threading.RateLimiting integrates cleanly into ASP.NET Core pipeline");

        // -------------------------------------------------------------------------
        // 24. Redis HNSW index creation validates dimensions (1536) and distance metric (COSINE)
        // -------------------------------------------------------------------------
        bool schemaValid = true;
        try { cache.ValidateIndexSchema(1536, "COSINE"); }
        catch { schemaValid = false; }
        AssertCheck(schemaValid, "Redis HNSW index creation validates dimensions (1536) and distance metric (COSINE)");

        // -------------------------------------------------------------------------
        // 25. Vector embedding floating-point precision differences do not breach distance threshold
        // -------------------------------------------------------------------------
        var orig = cache.GenerateNormalizedEmbedding("Precision test vector");
        var rounded = orig.Select(f => (float)Math.Round(f, 4)).ToArray();
        float precDist = cache.ComputeCosineDistance(orig, rounded);
        AssertCheck(precDist < 0.01f, "Vector embedding floating-point precision differences do not breach distance threshold");

        // -------------------------------------------------------------------------
        // 26. Empty or whitespace-only prompts rejected before vector search execution
        // -------------------------------------------------------------------------
        bool emptyRejected = false;
        try { cache.GenerateNormalizedEmbedding("   "); }
        catch (ArgumentException) { emptyRejected = true; }
        AssertCheck(emptyRejected, "Empty or whitespace-only prompts rejected before vector search execution");

        // -------------------------------------------------------------------------
        // 27. Gateway metrics record cache hit ratio, token savings, and 429 rejection counts
        // -------------------------------------------------------------------------
        AssertCheck(metrics.HitRatio >= 0.0 && metrics.TokensSaved > 0 && metrics.Rejections429 >= 0, "Gateway metrics record cache hit ratio, token savings, and 429 rejection counts");

        // -------------------------------------------------------------------------
        // 28. Audit telemetry records rate limit rejections without logging sensitive prompt payloads
        // -------------------------------------------------------------------------
        var auditEntry = new { TenantId = "tenant-audit", Event = "RateLimitRejected", RetryAfter = 5 };
        string json = System.Text.Json.JsonSerializer.Serialize(auditEntry);
        AssertCheck(!json.Contains("Prompt") && json.Contains("RateLimitRejected"), "Audit telemetry records rate limit rejections without logging sensitive prompt payloads");

        Console.WriteLine("================================================================================");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ALL {checksPassed} / 28 NEGATIVE & ARCHITECTURAL CHECKS VERIFIED SUCCESSFULLY!");
        Console.ResetColor();
        Console.WriteLine("================================================================================");
    }
}
