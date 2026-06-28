using System;
using Datafinder.Platform.Models;

namespace Datafinder.Platform.AppServices;

public sealed record HealthSnapshot(string Status, DateTimeOffset UtcNow, bool HasWorkspace);

public sealed record ApiActionResult(bool Success, string? Error = null, string? Message = null)
{
    public static ApiActionResult Ok(string? message = null) => new(true, null, message);
    public static ApiActionResult Fail(string error, string? message = null) => new(false, error, message);
}

public sealed record ApiActionResult<T>(bool Success, T? Data = default, string? Error = null, string? Message = null)
{
    public static ApiActionResult<T> Ok(T? data, string? message = null) => new(true, data, null, message);
    public static ApiActionResult<T> Fail(string error, string? message = null) => new(false, default, error, message);
}
