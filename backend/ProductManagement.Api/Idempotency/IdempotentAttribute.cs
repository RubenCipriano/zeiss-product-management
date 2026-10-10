using Microsoft.AspNetCore.Mvc;

namespace ProductManagement.Api.Idempotency;

/// <summary>
/// Makes a non-idempotent endpoint safe to retry: requests carrying an <c>Idempotency-Key</c> header are executed at
/// most once per key and consumer; repeats get the original response. See <see cref="IdempotencyFilter"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class IdempotentAttribute() : TypeFilterAttribute(typeof(IdempotencyFilter));
