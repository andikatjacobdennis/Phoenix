using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phoenix.Core.Abstractions;
using Phoenix.Core.Configuration;
using Phoenix.Core.Errors;
using Phoenix.Infrastructure.Testing;

namespace Phoenix.Infrastructure.Security;

/// <summary>
/// Proves that a downloaded file is the file the manifest described, before anything is
/// extracted or executed. A checksum failure is never retried: the same bytes will fail again,
/// and a mismatch is either corruption or tampering.
/// </summary>
public sealed class PackageVerifier : IPackageVerifier
{
    private readonly PhoenixOptions _options;
    private readonly FaultInjector _faults;
    private readonly ILogger<PackageVerifier> _logger;

    public PackageVerifier(IOptions<PhoenixOptions> options, FaultInjector faults, ILogger<PackageVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _faults = faults;
        _logger = logger;
    }

    public async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<Result> VerifyChecksumAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return PhoenixError.Verification("PX-VERIFY-MISSING", $"'{filePath}' does not exist.");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (_options.Security.RequireChecksum)
            {
                return PhoenixError.Security(
                    "PX-VERIFY-NO-EXPECTED",
                    $"No SHA-256 was supplied for '{Path.GetFileName(filePath)}' and Security:RequireChecksum is on.");
            }

            _logger.LogWarning(
                "No checksum supplied for {File}; accepting it because Security:RequireChecksum is off.",
                Path.GetFileName(filePath));
            return Result.Success();
        }

        var actual = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);

        if (_faults.ShouldFailChecksum)
        {
            _logger.LogWarning("Injecting a checksum mismatch for {File}.", Path.GetFileName(filePath));
            actual = new string('0', 64);
        }

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return PhoenixError.Verification(
                "PX-VERIFY-MISMATCH",
                $"SHA-256 mismatch for '{Path.GetFileName(filePath)}': expected {expectedSha256.ToLowerInvariant()}, " +
                $"computed {actual}.");
        }

        _logger.LogInformation(
            "Verified {File} ({Bytes} bytes, sha256 {Hash}).",
            Path.GetFileName(filePath),
            new FileInfo(filePath).Length.ToString(CultureInfo.InvariantCulture),
            actual);

        return Result.Success();
    }

    public Result VerifySignature(string filePath)
    {
        if (!_options.Security.RequireSignature)
        {
            return Result.Success();
        }

        if (!OperatingSystem.IsWindows())
        {
            return PhoenixError.Security(
                "PX-SIGN-UNSUPPORTED",
                "Security:RequireSignature is enabled but Authenticode verification needs Windows.");
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(filePath);
            using var chainCertificate = new X509Certificate2(certificate);

            using var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.Online,
                    RevocationFlag = X509RevocationFlag.ExcludeRoot,
                    VerificationFlags = X509VerificationFlags.NoFlag,
                },
            };

            if (!chain.Build(chainCertificate))
            {
                var statuses = string.Join(
                    ", ",
                    chain.ChainStatus.Select(s => s.Status.ToString()));

                return PhoenixError.Security(
                    "PX-SIGN-CHAIN",
                    $"The signing certificate of '{Path.GetFileName(filePath)}' did not validate: {statuses}.");
            }

            var allowed = _options.Security.AllowedCertificateThumbprints;
            if (allowed.Length > 0 &&
                !allowed.Contains(chainCertificate.Thumbprint, StringComparer.OrdinalIgnoreCase))
            {
                return PhoenixError.Security(
                    "PX-SIGN-THUMBPRINT",
                    $"'{Path.GetFileName(filePath)}' is signed by {chainCertificate.Thumbprint}, which is not in " +
                    "Security:AllowedCertificateThumbprints.");
            }

            _logger.LogInformation(
                "Signature verified for {File} (subject {Subject}).",
                Path.GetFileName(filePath),
                chainCertificate.Subject);

            return Result.Success();
        }
        catch (CryptographicException ex)
        {
            return PhoenixError.Security(
                "PX-SIGN-MISSING",
                $"'{Path.GetFileName(filePath)}' is not signed, or the signature could not be read: {ex.Message}",
                ex);
        }
    }
}
