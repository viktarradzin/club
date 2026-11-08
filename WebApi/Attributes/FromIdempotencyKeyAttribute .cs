using Microsoft.AspNetCore.Mvc;
using WebApi.Constants;

namespace WebApi.Attributes;

public sealed class FromIdempotencyKeyAttribute : FromHeaderAttribute
{
    public FromIdempotencyKeyAttribute()
    {
        Name = ApiHeaders.IdempotencyKey;
    }
}
