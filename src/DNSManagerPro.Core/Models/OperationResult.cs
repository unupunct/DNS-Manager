namespace DNSManagerPro.Core.Models;

/// <summary>Outcome of an operation that can fail without being exceptional.</summary>
public sealed record OperationResult(bool Success, string Message, bool RequiresElevation = false)
{
    public static OperationResult Ok(string message = "") => new(true, message);

    public static OperationResult Fail(string message, bool requiresElevation = false) => new(false, message, requiresElevation);
}
