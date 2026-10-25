using Microsoft.Extensions.Options;

namespace RateLimiter.Middleware;

/// <summary>
/// Runs at startup (ValidateOnStart), so a bad config fails the deploy immediately
/// instead of silently not rate limiting -- or crashing on the first request.
/// </summary>
public sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApiKeyHeader))
        {
            errors.Add("RateLimiting:ApiKeyHeader must not be empty.");
        }

        var duplicateNames = options.Policies
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var duplicate in duplicateNames)
        {
            errors.Add($"Policy name '{duplicate}' is used more than once; names must be unique.");
        }

        foreach (var policy in options.Policies)
        {
            var label = string.IsNullOrWhiteSpace(policy.Name) ? "(unnamed)" : policy.Name;

            if (string.IsNullOrWhiteSpace(policy.Name))
                errors.Add("Every policy needs a Name.");
            if (!policy.PathPrefix.StartsWith('/'))
                errors.Add($"Policy '{label}': PathPrefix must start with '/'.");
            if (policy.PermitLimit <= 0)
                errors.Add($"Policy '{label}': PermitLimit must be greater than 0.");
            if (policy.Window <= TimeSpan.Zero)
                errors.Add($"Policy '{label}': Window must be greater than 0.");
            if (!Enum.IsDefined(policy.Algorithm))
                errors.Add($"Policy '{label}': unknown Algorithm '{policy.Algorithm}'.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
