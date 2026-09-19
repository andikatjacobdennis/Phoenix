using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Phoenix.Core.Versioning;

/// <summary>
/// Semantic Version 2.0.0 value with the comparison rules Phoenix relies on when it
/// decides which release is newer and which channel a release belongs to.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public SemanticVersion(int major, int minor, int patch, string? preRelease = null, string? buildMetadata = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);

        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = string.IsNullOrWhiteSpace(preRelease) ? null : preRelease;
        BuildMetadata = string.IsNullOrWhiteSpace(buildMetadata) ? null : buildMetadata;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The part after '-', for example <c>qa.3</c>. Null for a stable release.</summary>
    public string? PreRelease { get; }

    /// <summary>The part after '+'. Ignored for precedence, per the specification.</summary>
    public string? BuildMetadata { get; }

    public bool IsPreRelease => PreRelease is not null;

    /// <summary>
    /// The first dot-separated identifier of the prerelease label, lower-cased:
    /// <c>1.2.0-qa.3</c> yields <c>qa</c>. This is what channel resolution keys off.
    /// </summary>
    public string? PreReleaseLabel =>
        PreRelease?.Split('.', 2)[0].ToLowerInvariant();

    public static bool TryParse(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // Release tags are conventionally prefixed, e.g. "v1.4.0".
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        string? build = null;
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            build = text[(plus + 1)..];
            text = text[..plus];
            if (build.Length == 0 || !IsValidDotSeparated(build))
            {
                return false;
            }
        }

        string? pre = null;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
            if (pre.Length == 0 || !IsValidDotSeparated(pre))
            {
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        Span<int> numbers = stackalloc int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length)
            {
                numbers[i] = 0;
                continue;
            }

            var part = parts[i];
            if (part.Length == 0 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            numbers[i] = number;
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], pre, build);
        return true;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid semantic version.");

    private static bool IsValidDotSeparated(string value)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            foreach (var c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        // A version without a prerelease label outranks one that has it.
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var shared = Math.Min(leftParts.Length, rightParts.Length);

        for (var i = 0; i < shared; i++)
        {
            var leftIsNumeric = int.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumeric = int.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            int comparison;
            if (leftIsNumeric && rightIsNumeric)
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (leftIsNumeric)
            {
                comparison = -1; // numeric identifiers are lower than alphanumeric ones
            }
            else if (rightIsNumeric)
            {
                comparison = 1;
            }
            else
            {
                comparison = string.CompareOrdinal(leftParts[i], rightParts[i]);
            }

            if (comparison != 0)
            {
                return Math.Sign(comparison);
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        if (PreRelease is not null)
        {
            core += $"-{PreRelease}";
        }

        return BuildMetadata is not null ? $"{core}+{BuildMetadata}" : core;
    }

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);

    public static bool operator <(SemanticVersion? left, SemanticVersion? right) =>
        Comparer<SemanticVersion>.Default.Compare(left, right) < 0;

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) =>
        Comparer<SemanticVersion>.Default.Compare(left, right) > 0;

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) =>
        Comparer<SemanticVersion>.Default.Compare(left, right) <= 0;

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) =>
        Comparer<SemanticVersion>.Default.Compare(left, right) >= 0;
}
