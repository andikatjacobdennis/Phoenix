namespace Phoenix.Infrastructure.Prerequisites.Detectors;

/// <summary>
/// Turns a configured program name into a full path: environment variables are expanded, and
/// a bare name is looked up on PATH using PATHEXT. Returns null rather than guessing.
/// </summary>
public static class ExecutableResolver
{
    public static string? Resolve(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(pathOrName.Trim());

        if (Path.IsPathRooted(expanded))
        {
            return File.Exists(expanded) ? expanded : null;
        }

        if (expanded.Contains(Path.DirectorySeparatorChar) || expanded.Contains(Path.AltDirectorySeparatorChar))
        {
            var relative = Path.GetFullPath(expanded);
            return File.Exists(relative) ? relative : null;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;

            try
            {
                candidate = Path.Combine(directory.Trim('"'), expanded);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }

            foreach (var extension in extensions)
            {
                if (extension.Length == 0)
                {
                    continue;
                }

                var withExtension = candidate + extension;
                if (File.Exists(withExtension))
                {
                    return withExtension;
                }
            }
        }

        return null;
    }
}
