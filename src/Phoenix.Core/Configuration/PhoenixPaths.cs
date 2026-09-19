using System.Text;
using Phoenix.Core.Versioning;

namespace Phoenix.Core.Configuration;

/// <summary>
/// The concrete directory layout Phoenix owns, resolved once at startup.
///
/// <code>
/// {Root}/
///   versions/{version}/   extracted installations (the active one is named in state)
///   backup/{version}/     copies of known-good installations kept for recovery
///   staging/{operation}/  downloads and extraction in progress
///   cache/                verified installers and packages
///   logs/                 Serilog rolling files
///   state/phoenix-state.json
/// </code>
/// </summary>
public sealed class PhoenixPaths
{
    private PhoenixPaths(
        string root,
        string versions,
        string staging,
        string backup,
        string cache,
        string logs,
        string state)
    {
        Root = root;
        Versions = versions;
        Staging = staging;
        Backup = backup;
        Cache = cache;
        Logs = logs;
        StateDirectory = state;
        StateFile = Path.Combine(state, "phoenix-state.json");
        CurrentLink = Path.Combine(root, "current");
    }

    public string Root { get; }

    public string Versions { get; }

    public string Staging { get; }

    public string Backup { get; }

    public string Cache { get; }

    public string Logs { get; }

    public string StateDirectory { get; }

    public string StateFile { get; }

    /// <summary>
    /// Convenience junction pointing at the active version. Never load-bearing:
    /// activation is a state-file swap, not a directory rename.
    /// </summary>
    public string CurrentLink { get; }

    public static PhoenixPaths Create(PhoenixOptions options, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(options);

        var slug = ProductSlug(options.Product);
        var root = Path.GetFullPath(Expand(options.Directories.Root, slug, environmentName));

        string Resolve(string? configured, string defaultName) =>
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(root, defaultName)
                : Path.GetFullPath(Expand(configured, slug, environmentName));

        return new PhoenixPaths(
            root,
            Resolve(options.Directories.Versions, "versions"),
            Resolve(options.Directories.Staging, "staging"),
            Resolve(options.Directories.Backup, "backup"),
            Resolve(options.Directories.Cache, "cache"),
            Resolve(options.Directories.Logs, "logs"),
            Resolve(options.Directories.State, "state"));
    }

    /// <summary>Replaces placeholders and environment variables in a configured path.</summary>
    public static string Expand(string value, string productSlug, string environmentName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value ?? string.Empty);
        return expanded
            .Replace("{ProductSlug}", productSlug, StringComparison.OrdinalIgnoreCase)
            .Replace("{Environment}", environmentName, StringComparison.OrdinalIgnoreCase);
    }

    public static string ProductSlug(ProductOptions product)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (!string.IsNullOrWhiteSpace(product.Slug))
        {
            return Sanitize(product.Slug);
        }

        return Sanitize(string.IsNullOrWhiteSpace(product.Name) ? "Application" : product.Name);
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                builder.Append(c);
            }
            else if (c is ' ' or '.')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "Application" : slug;
    }

    public string VersionDirectory(SemanticVersion version) =>
        Path.Combine(Versions, version.ToString());

    public string VersionDirectory(string version) =>
        Path.Combine(Versions, version);

    public string BackupDirectory(string version) =>
        Path.Combine(Backup, version);

    public string StagingDirectory(string operationId) =>
        Path.Combine(Staging, operationId);

    /// <summary>Creates the directory tree. Safe to call repeatedly.</summary>
    public void EnsureCreated()
    {
        foreach (var directory in new[] { Root, Versions, Staging, Backup, Cache, Logs, StateDirectory })
        {
            Directory.CreateDirectory(directory);
        }
    }

    public override string ToString() => Root;
}
