namespace SemanticCachingGateway.Models;

public record ChatCompletionRequest(
    string Prompt,
    int? MaxTokens = 1000,
    double Temperature = 0.7,
    bool Stream = false
);

public record ChatCompletionResponse(
    string Id,
    string Text,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string Model = "gpt-4o",
    bool Cached = false
);

public record SemanticMatchResult(
    string CachedPrompt,
    string ResponsePayload,
    float Distance,
    string TenantId
);

public record RateReservationResult(
    bool Allowed,
    int RetryAfterSeconds = 0,
    int RemainingTokens = 0,
    string RejectionReason = ""
);
