using Phoenix.Core.Errors;
using Phoenix.Core.Models;

namespace Phoenix.Core.Abstractions;

/// <summary>
/// Everything needed to fetch one file, expressed without any HTTP types so the
/// release source (GitHub today, something else tomorrow) stays behind one seam.
/// </summary>
public sealed record DownloadDescriptor(
    Uri Url,
    IReadOnlyDictionary<string, string> Headers,
    long? ExpectedSizeBytes = null)
{
    public static DownloadDescriptor For(Uri url, long? size = null) =>
        new(url, new Dictionary<string, string>(), size);
}

/// <summary>Source of application releases.</summary>
public interface IReleaseProvider
{
    /// <summary>Releases, newest first. Drafts are excluded unless configuration allows them.</summary>
    Task<Result<IReadOnlyList<ReleaseInfo>>> GetReleasesAsync(CancellationToken cancellationToken);

    /// <summary>Produces the request details (URL plus any auth headers) for one asset.</summary>
    DownloadDescriptor DescribeAsset(ReleaseAsset asset);
}

public interface IDownloadManager
{
    /// <summary>
    /// Downloads to a temporary file next to <paramref name="destinationPath"/> and moves it
    /// into place only once the transfer completed, so a partial file is never mistaken for a package.
    /// </summary>
    Task<Result<string>> DownloadToFileAsync(
        DownloadDescriptor descriptor,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Downloads a small text asset (a manifest) straight into memory, bounded by <paramref name="maxBytes"/>.</summary>
    Task<Result<string>> DownloadTextAsync(
        DownloadDescriptor descriptor,
        long maxBytes,
        CancellationToken cancellationToken);
}

public interface IPackageVerifier
{
    Task<Result> VerifyChecksumAsync(string filePath, string expectedSha256, CancellationToken cancellationToken);

    /// <summary>Authenticode verification. A no-op unless <c>Security:RequireSignature</c> is enabled.</summary>
    Result VerifySignature(string filePath);

    Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken);
}

public sealed record ExtractionSummary(int EntryCount, long TotalBytes);

public interface IPackageExtractor
{
    /// <summary>
    /// Extracts an archive into <paramref name="destinationDirectory"/>. Entries that would
    /// escape the destination, or that exceed the configured size limits, abort the extraction.
    /// </summary>
    Task<Result<ExtractionSummary>> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken);
}
