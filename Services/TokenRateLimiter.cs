namespace SemanticCachingGateway.Services;

using System.Collections.Concurrent;
using SemanticCachingGateway.Models;

public interface ITokenRateLimiter
{
    Task<RateReservationResult> ReserveAsync(string tenantId, int estimatedTokens);
    Task RefundAsync(string tenantId, int refundTokens);
    Task ExhaustTpmQuotaAsync(string tenantId);
    Task ExhaustRpmQuotaAsync(string tenantId);
    (int RpmTokens, int TpmTokens) GetBucketState(string tenantId);
}

public class TokenRateLimiter : ITokenRateLimiter
{
    private class TenantBucket
    {
        public int MaxRpm { get; set; } = 60;
        public int MaxTpm { get; set; } = 40000;
        public double RpmTokens { get; set; } = 60;
        public double TpmTokens { get; set; } = 40000;
        public DateTime LastRefillUtc { get; set; } = DateTime.UtcNow;
        public object Lock { get; } = new();
    }

    private readonly ConcurrentDictionary<string, TenantBucket> _buckets = new();
    private readonly double _rpmRefillPerMs = 60.0 / 60000.0;     // 1 req / sec
    private readonly double _tpmRefillPerMs = 40000.0 / 60000.0;  // ~0.667 tokens / ms

    private TenantBucket GetOrCreateBucket(string tenantId) =>
        _buckets.GetOrAdd(tenantId, _ => new TenantBucket());

    public (int RpmTokens, int TpmTokens) GetBucketState(string tenantId)
    {
        var b = GetOrCreateBucket(tenantId);
        lock (b.Lock)
        {
            return ((int)Math.Floor(b.RpmTokens), (int)Math.Floor(b.TpmTokens));
        }
    }

    public Task ExhaustTpmQuotaAsync(string tenantId)
    {
        var b = GetOrCreateBucket(tenantId);
        lock (b.Lock) { b.TpmTokens = 0; }
        return Task.CompletedTask;
    }

    public Task ExhaustRpmQuotaAsync(string tenantId)
    {
        var b = GetOrCreateBucket(tenantId);
        lock (b.Lock) { b.RpmTokens = 0; }
        return Task.CompletedTask;
    }

    public Task<RateReservationResult> ReserveAsync(string tenantId, int estimatedTokens)
    {
        var b = GetOrCreateBucket(tenantId);
        DateTime now = DateTime.UtcNow;

        lock (b.Lock)
        {
            double elapsedMs = Math.Max(0, (now - b.LastRefillUtc).TotalMilliseconds);
            b.RpmTokens = Math.Min(b.MaxRpm, b.RpmTokens + (elapsedMs * _rpmRefillPerMs));
            b.TpmTokens = Math.Min(b.MaxTpm, b.TpmTokens + (elapsedMs * _tpmRefillPerMs));
            b.LastRefillUtc = now;

            // 1. Check Request Rate (RPM)
            if (b.RpmTokens < 1.0)
            {
                double neededRpm = 1.0 - b.RpmTokens;
                int retrySec = Math.Max(1, (int)Math.Ceiling(neededRpm / (_rpmRefillPerMs * 1000.0)));
                return Task.FromResult(new RateReservationResult(
                    Allowed: false,
                    RetryAfterSeconds: retrySec,
                    RemainingTokens: (int)b.TpmTokens,
                    RejectionReason: "RPM limit reached"));
            }

            // 2. Check Token Budget (TPM)
            if (b.TpmTokens < estimatedTokens)
            {
                double neededTpm = estimatedTokens - b.TpmTokens;
                int retrySec = Math.Max(1, (int)Math.Ceiling(neededTpm / (_tpmRefillPerMs * 1000.0)));
                return Task.FromResult(new RateReservationResult(
                    Allowed: false,
                    RetryAfterSeconds: retrySec,
                    RemainingTokens: (int)b.TpmTokens,
                    RejectionReason: "TPM budget exhausted"));
            }

            // 3. Atomically Reserve Both
            b.RpmTokens -= 1.0;
            b.TpmTokens -= estimatedTokens;

            return Task.FromResult(new RateReservationResult(
                Allowed: true,
                RetryAfterSeconds: 0,
                RemainingTokens: (int)Math.Floor(b.TpmTokens)));
        }
    }

    public Task RefundAsync(string tenantId, int refundTokens)
    {
        if (refundTokens <= 0) return Task.CompletedTask;
        var b = GetOrCreateBucket(tenantId);

        lock (b.Lock)
        {
            b.TpmTokens = Math.Min(b.MaxTpm, b.TpmTokens + refundTokens);
        }

        return Task.CompletedTask;
    }
}
