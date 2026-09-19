namespace Phoenix.Infrastructure.Installation;

/// <summary>Small filesystem helpers shared by the installer, backups and cleanup.</summary>
internal static class DirectoryOperations
{
    public static void Copy(string sourceDirectory, string targetDirectory)
    {
        var source = new DirectoryInfo(sourceDirectory);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory '{sourceDirectory}' does not exist.");
        }

        Directory.CreateDirectory(targetDirectory);

        foreach (var file in source.GetFiles())
        {
            file.CopyTo(Path.Combine(targetDirectory, file.Name), overwrite: true);
        }

        foreach (var directory in source.GetDirectories())
        {
            Copy(directory.FullName, Path.Combine(targetDirectory, directory.Name));
        }
    }

    /// <summary>
    /// Deletes a directory tree, clearing read-only attributes on the way. Returns false
    /// instead of throwing when something holds a file open: cleanup is never fatal.
    /// </summary>
    public static bool TryDelete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return true;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves a directory, falling back to copy-then-delete when the move crosses volumes.
    /// </summary>
    public static void Move(string sourceDirectory, string targetDirectory)
    {
        try
        {
            Directory.Move(sourceDirectory, targetDirectory);
        }
        catch (IOException)
        {
            Copy(sourceDirectory, targetDirectory);
            TryDelete(sourceDirectory);
        }
    }

    public static long MeasureSize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // A file that vanished mid-scan does not change the decision meaningfully.
            }
        }

        return total;
    }
}
