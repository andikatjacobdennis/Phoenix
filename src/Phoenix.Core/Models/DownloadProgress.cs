namespace Phoenix.Core.Models;

/// <summary>
/// Progress of a download. <see cref="TotalBytes"/> is null when the server did not send a
/// content length, in which case the UI shows a spinner rather than inventing a percentage.
/// </summary>
public readonly record struct DownloadProgress(long BytesTransferred, long? TotalBytes)
{
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes.Value, 0d, 1d) : null;
}
