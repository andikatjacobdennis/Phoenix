using System.Diagnostics.CodeAnalysis;

namespace Phoenix.Core.Versioning;

/// <summary>
/// Version constraint used by prerequisite detection: "at least 9.0", "exactly 3.12.4",
/// or interval notation such as <c>[9.0,11.0)</c>.
/// </summary>
public sealed class VersionRange
{
    private VersionRange(
        SemanticVersion? minimum,
        bool minimumInclusive,
        SemanticVersion? maximum,
        bool maximumInclusive,
        SemanticVersion? exact)
    {
        Minimum = minimum;
        MinimumInclusive = minimumInclusive;
        Maximum = maximum;
        MaximumInclusive = maximumInclusive;
        Exact = exact;
    }

    public SemanticVersion? Minimum { get; }

    public bool MinimumInclusive { get; }

    public SemanticVersion? Maximum { get; }

    public bool MaximumInclusive { get; }

    /// <summary>When set, only this exact version satisfies the range.</summary>
    public SemanticVersion? Exact { get; }

    /// <summary>A range that accepts any version.</summary>
    public static VersionRange Any { get; } = new(null, true, null, true, null);

    public static VersionRange AtLeast(SemanticVersion minimum) => new(minimum, true, null, true, null);

    public static VersionRange ExactlyEqual(SemanticVersion version) => new(null, true, null, true, version);

    /// <summary>
    /// Builds a range from the individual configuration fields. <paramref name="range"/>
    /// (interval notation) wins; otherwise required / minimum / maximum are combined.
    /// </summary>
    public static bool TryCreate(
        string? required,
        string? minimum,
        string? maximum,
        string? range,
        [NotNullWhen(true)] out VersionRange? result)
    {
        result = null;

        if (!string.IsNullOrWhiteSpace(range))
        {
            return TryParseInterval(range, out result);
        }

        if (!string.IsNullOrWhiteSpace(required))
        {
            if (!SemanticVersion.TryParse(required, out var exact))
            {
                return false;
            }

            result = ExactlyEqual(exact);
            return true;
        }

        SemanticVersion? min = null;
        SemanticVersion? max = null;

        if (!string.IsNullOrWhiteSpace(minimum) && !SemanticVersion.TryParse(minimum, out min))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(maximum) && !SemanticVersion.TryParse(maximum, out max))
        {
            return false;
        }

        if (min is not null && max is not null && min > max)
        {
            return false;
        }

        result = new VersionRange(min, true, max, true, null);
        return true;
    }

    /// <summary>Parses interval notation: <c>[1.0,2.0)</c>, <c>(1.0,]</c>, <c>[1.2.3]</c>.</summary>
    public static bool TryParseInterval(string value, [NotNullWhen(true)] out VersionRange? result)
    {
        result = null;
        var text = value.Trim();

        if (text.Length < 3)
        {
            return false;
        }

        var minInclusive = text[0] switch { '[' => true, '(' => false, _ => (bool?)null };
        var maxInclusive = text[^1] switch { ']' => true, ')' => false, _ => (bool?)null };
        if (minInclusive is null || maxInclusive is null)
        {
            return false;
        }

        var inner = text[1..^1];
        var comma = inner.IndexOf(',', StringComparison.Ordinal);

        if (comma < 0)
        {
            // "[1.2.3]" means exactly 1.2.3
            if (minInclusive != true || maxInclusive != true || !SemanticVersion.TryParse(inner, out var exact))
            {
                return false;
            }

            result = ExactlyEqual(exact);
            return true;
        }

        var lower = inner[..comma].Trim();
        var upper = inner[(comma + 1)..].Trim();

        SemanticVersion? min = null;
        SemanticVersion? max = null;

        if (lower.Length > 0 && !SemanticVersion.TryParse(lower, out min))
        {
            return false;
        }

        if (upper.Length > 0 && !SemanticVersion.TryParse(upper, out max))
        {
            return false;
        }

        if (min is not null && max is not null && min > max)
        {
            return false;
        }

        result = new VersionRange(min, minInclusive.Value, max, maxInclusive.Value, null);
        return true;
    }

    public bool Satisfies(SemanticVersion? version)
    {
        if (version is null)
        {
            return false;
        }

        if (Exact is not null)
        {
            return version == Exact;
        }

        if (Minimum is not null)
        {
            var comparison = version.CompareTo(Minimum);
            if (comparison < 0 || (comparison == 0 && !MinimumInclusive))
            {
                return false;
            }
        }

        if (Maximum is not null)
        {
            var comparison = version.CompareTo(Maximum);
            if (comparison > 0 || (comparison == 0 && !MaximumInclusive))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString()
    {
        if (Exact is not null)
        {
            return Exact.ToString();
        }

        if (Minimum is null && Maximum is null)
        {
            return "any";
        }

        var open = MinimumInclusive ? '[' : '(';
        var close = MaximumInclusive ? ']' : ')';
        return $"{open}{Minimum?.ToString() ?? string.Empty},{Maximum?.ToString() ?? string.Empty}{close}";
    }
}
