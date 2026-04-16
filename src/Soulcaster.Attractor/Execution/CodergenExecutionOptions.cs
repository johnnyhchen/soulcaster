namespace Soulcaster.Attractor.Execution;

using System.Globalization;
using System.Text.RegularExpressions;

public sealed record CodergenExecutionOptions(
    string? StageClass = null,
    int? MaxProviderResponseMs = null,
    bool RequireEdits = false,
    bool RequireVerification = false,
    bool AllowContractFallback = true,
    string? CodergenVersion = null,
    RuntimeValidationPolicy? Validation = null)
{
    public RuntimeValidationPolicy EffectiveValidation => Validation ?? RuntimeValidationPolicy.None;
}

public static partial class RuntimeDurationParser
{
    [GeneratedRegex(@"^(?<value>\d+(?:\.\d+)?)\s*(?<unit>ms|s|m|h)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationWithUnitPattern();

    public static bool TryParseTimeout(string? raw, out TimeSpan timeout)
    {
        timeout = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (TryParseMilliseconds(raw, out var milliseconds))
        {
            timeout = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out timeout) ||
               TimeSpan.TryParse(raw, out timeout);
    }

    public static bool TryParseMilliseconds(string? raw, out int milliseconds)
    {
        milliseconds = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawMilliseconds))
        {
            if (rawMilliseconds < 0)
                return false;

            milliseconds = rawMilliseconds;
            return true;
        }

        var match = DurationWithUnitPattern().Match(trimmed);
        if (!match.Success)
            return false;

        if (!decimal.TryParse(match.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ||
            value < 0)
        {
            return false;
        }

        var multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "ms" => 1m,
            "s" => 1000m,
            "m" => 60_000m,
            "h" => 3_600_000m,
            _ => 0m
        };

        if (multiplier <= 0)
            return false;

        var computed = decimal.Round(value * multiplier, 0, MidpointRounding.AwayFromZero);
        if (computed > int.MaxValue)
            return false;

        milliseconds = (int)computed;
        return true;
    }
}
