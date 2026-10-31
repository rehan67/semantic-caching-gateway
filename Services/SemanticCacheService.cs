namespace SemanticCachingGateway.Services;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SemanticCachingGateway.Models;

public interface ISemanticCacheService
{
    float ComputeCosineDistance(float[] u, float[] v);
    float[] GenerateNormalizedEmbedding(string text);
    Task<SemanticMatchResult?> FindMatchAsync(string tenantId, string prompt, float[] vector, float threshold = 0.04f);
    Task StoreAsync(string tenantId, string prompt, float[] vector, string responsePayload, TimeSpan? ttl = null);
    void ValidateIndexSchema(int dimensions, string metric);
}

public class SemanticCacheService : ISemanticCacheService
{
    private record CacheEntry(
        string TenantId,
        string Prompt,
        float[] Vector,
        string ResponsePayload,
        DateTime ExpiresAtUtc,
        string ExactHash
    );

    private readonly ConcurrentDictionary<string, CacheEntry> _storage = new();
    public const int DefaultDimensions = 1536;
    public const string DefaultMetric = "COSINE";

    public void ValidateIndexSchema(int dimensions, string metric)
    {
        if (dimensions != DefaultDimensions)
            throw new ArgumentException($"Invalid vector dimensions: expected {DefaultDimensions}, received {dimensions}");
        if (!string.Equals(metric, DefaultMetric, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid distance metric: expected {DefaultMetric}, received {metric}");
    }

    public float ComputeCosineDistance(float[] u, float[] v)
    {
        if (u.Length != v.Length)
            throw new ArgumentException("Vectors must have identical dimensions");

        float dot = 0f;
        float normU = 0f;
        float normV = 0f;

        for (int i = 0; i < u.Length; i++)
        {
            dot += u[i] * v[i];
            normU += u[i] * u[i];
            normV += v[i] * v[i];
        }

        if (normU <= 0f || normV <= 0f) return 1.0f;

        float similarity = dot / (MathF.Sqrt(normU) * MathF.Sqrt(normV));
        // Clamp to [-1, 1] for floating point stability
        similarity = Math.Clamp(similarity, -1.0f, 1.0f);
        return 1.0f - similarity;
    }

    public float[] GenerateNormalizedEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Prompt text cannot be empty or whitespace.", nameof(text));

        // Deterministic high-dimensional projection for testing and verification
        var vector = new float[DefaultDimensions];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant()));

        for (int i = 0; i < DefaultDimensions; i++)
        {
            byte b = hash[i % hash.Length];
            vector[i] = (b / 128.0f) - 1.0f + (MathF.Sin(i * 0.1f) * 0.05f);
        }

        // L2 Unit Normalization: ||u||_2 = 1.0
        float sumSq = 0f;
        for (int i = 0; i < vector.Length; i++) sumSq += vector[i] * vector[i];
        float norm = MathF.Sqrt(sumSq);

        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }

    public Task<SemanticMatchResult?> FindMatchAsync(string tenantId, string prompt, float[] vector, float threshold = 0.04f)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new UnauthorizedAccessException("Missing required tenant_id claim.");

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt cannot be empty.");

        string promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt.Trim())));
        DateTime now = DateTime.UtcNow;

        // 1. Exact String Hash Shortcut (D = 0.00)
        foreach (var entry in _storage.Values)
        {
            // CRITICAL: RediSearch Tag Pre-Filtering (@tenant_id:{tenantId})
            // Constrain search strictly to matching tenant partition
            if (entry.TenantId != tenantId) continue;
            if (entry.ExpiresAtUtc <= now) continue;

            if (entry.ExactHash == promptHash)
            {
                return Task.FromResult<SemanticMatchResult?>(new SemanticMatchResult(entry.Prompt, entry.ResponsePayload, 0.00f, tenantId));
            }
        }

        // 2. High-Dimensional Vector Distance Search (KNN 1 Pre-Filtered by @tenant_id)
        SemanticMatchResult? bestMatch = null;
        float minDistance = float.MaxValue;

        foreach (var entry in _storage.Values)
        {
            // Enforce tenant boundary before evaluating vector distance
            if (entry.TenantId != tenantId) continue;
            if (entry.ExpiresAtUtc <= now) continue;

            float dist = ComputeCosineDistance(vector, entry.Vector);
            if (dist < minDistance && dist <= threshold)
            {
                minDistance = dist;
                bestMatch = new SemanticMatchResult(entry.Prompt, entry.ResponsePayload, dist, tenantId);
            }
        }

        return Task.FromResult(bestMatch);
    }

    public Task StoreAsync(string tenantId, string prompt, float[] vector, string responsePayload, TimeSpan? ttl = null)
    {
        string promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt.Trim())));
        string key = $"{tenantId}:{promptHash}";
        DateTime expiry = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromHours(4));

        _storage[key] = new CacheEntry(tenantId, prompt, vector, responsePayload, expiry, promptHash);
        return Task.CompletedTask;
    }
}
