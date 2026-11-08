namespace WebApi.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class UserRateLimitAttribute : Attribute;
