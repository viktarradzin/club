namespace Common.RateLimiting;

public sealed record RateLimitDecision(bool IsAllowed, int RetryAfterSeconds);
