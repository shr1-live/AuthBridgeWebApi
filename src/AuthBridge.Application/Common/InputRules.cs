using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AuthBridge.Domain;

namespace AuthBridge.Application.Common;

public static partial class InputRules
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]*$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Za-z0-9._:-]+$")]
    private static partial Regex IdempotencyKeyPattern();

    /// <summary>Returns an error for a missing, oversized or malformed identifier-like code.</summary>
    public static AppError? Code(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Errors.Invalid($"{name} is required.");
        if (value.Length > maxLength)
            return Errors.Invalid($"{name} must be at most {maxLength} characters.");
        if (!CodePattern().IsMatch(value))
            return Errors.Invalid($"{name} may contain only letters, digits and hyphens.");
        return null;
    }

    public static AppError? IdempotencyKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Errors.Invalid("idempotencyKey is required.");
        if (value.Length > FieldLimits.IdempotencyKey)
            return Errors.Invalid($"idempotencyKey must be at most {FieldLimits.IdempotencyKey} characters.");
        if (!IdempotencyKeyPattern().IsMatch(value))
            return Errors.Invalid("idempotencyKey may contain only letters, digits, '.', '_', ':' and '-'.");
        return null;
    }

    public static AppError? Paging(int page, int pageSize)
    {
        if (page < 1)
            return Errors.Invalid("page must be 1 or greater.");
        if (pageSize is < 1 or > FieldLimits.MaxPageSize)
            return Errors.Invalid($"pageSize must be between 1 and {FieldLimits.MaxPageSize}.");
        return null;
    }

    public static AppError? RequiredGuid(Guid value, string name) =>
        value == Guid.Empty ? Errors.Invalid($"{name} is required.") : null;

    public static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
