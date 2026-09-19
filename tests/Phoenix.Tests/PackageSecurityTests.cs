using System.IO.Compression;
using System.Text;
using Phoenix.Core.Errors;
using Phoenix.Infrastructure.Packaging;
using Phoenix.Infrastructure.Security;
using Phoenix.Tests.TestSupport;

namespace Phoenix.Tests;

public class ZipPackageExtractorTests
{
    private static string CreateArchive(string directory, Action<ZipArchive> build)
    {
        var path = Path.Combine(directory, $"package-{Guid.NewGuid():N}.zip");

        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            build(archive);
        }

        return path;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static ZipPackageExtractor CreateExtractor(string root, Action<Phoenix.Core.Configuration.PhoenixOptions>? configure = null)
    {
        var options = TestFactory.CreateOptions(root, configure);
        return new ZipPackageExtractor(
            TestFactory.Wrap(options),
            TestFactory.CreateFaultInjector(options),
            TestFactory.Logger<ZipPackageExtractor>());
    }

    [Fact]
    public async Task Extracts_a_normal_archive()
    {
        using var temp = new TempDirectory("extract-ok");
        var archive = CreateArchive(temp.Path, a =>
        {
            AddEntry(a, "Phoenix.DemoApp.exe", "binary");
            AddEntry(a, "config/appsettings.json", "{}");
        });

        var destination = temp.Combine("out");
        var result = await CreateExtractor(temp.Path).ExtractAsync(archive, destination, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.EntryCount);
        Assert.True(File.Exists(Path.Combine(destination, "Phoenix.DemoApp.exe")));
        Assert.True(File.Exists(Path.Combine(destination, "config", "appsettings.json")));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("../../Windows/System32/evil.dll")]
    public async Task Refuses_entries_that_climb_out_of_the_destination(string entryName)
    {
        using var temp = new TempDirectory("extract-traversal");
        var archive = CreateArchive(temp.Path, a => AddEntry(a, entryName, "owned"));

        var destination = temp.Combine("out");
        var result = await CreateExtractor(temp.Path).ExtractAsync(archive, destination, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Security, result.Error.Category);
        Assert.Equal("PX-EXTRACT-TRAVERSAL", result.Error.Code);

        // And nothing landed next to the destination.
        Assert.False(File.Exists(temp.Combine("escaped.txt")));
        Assert.False(File.Exists(temp.Combine("evil.dll")));
    }

    [Theory]
    [InlineData("/etc/passwd", "PX-EXTRACT-ABSOLUTE")]
    [InlineData("\\windows\\system32\\evil.dll", "PX-EXTRACT-ABSOLUTE")]
    [InlineData("C:\\Windows\\evil.dll", "PX-EXTRACT-DRIVE")]
    [InlineData("file.txt:stream", "PX-EXTRACT-DRIVE")]
    public async Task Refuses_absolute_and_drive_qualified_entries(string entryName, string expectedCode)
    {
        using var temp = new TempDirectory("extract-absolute");
        var archive = CreateArchive(temp.Path, a => AddEntry(a, entryName, "owned"));

        var result = await CreateExtractor(temp.Path)
            .ExtractAsync(archive, temp.Combine("out"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    [Fact]
    public async Task Refuses_an_archive_with_too_many_entries()
    {
        using var temp = new TempDirectory("extract-many");
        var archive = CreateArchive(temp.Path, a =>
        {
            for (var i = 0; i < 10; i++)
            {
                AddEntry(a, $"file-{i}.txt", "x");
            }
        });

        var extractor = CreateExtractor(temp.Path, o => o.Security.MaxArchiveEntries = 5);
        var result = await extractor.ExtractAsync(archive, temp.Combine("out"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-EXTRACT-TOO-MANY", result.Error.Code);
    }

    [Fact]
    public async Task Refuses_an_archive_that_expands_beyond_the_limit()
    {
        using var temp = new TempDirectory("extract-big");
        var archive = CreateArchive(temp.Path, a => AddEntry(a, "big.txt", new string('x', 4096)));

        var extractor = CreateExtractor(temp.Path, o =>
        {
            o.Security.MaxPackageSizeBytes = 512;
            o.Security.MaxExtractedSizeBytes = 512;
        });

        var result = await extractor.ExtractAsync(archive, temp.Combine("out"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-EXTRACT-TOO-BIG", result.Error.Code);
    }

    [Fact]
    public async Task Reports_a_corrupt_archive_without_throwing()
    {
        using var temp = new TempDirectory("extract-corrupt");
        var path = temp.WriteFile("broken.zip", "this is definitely not a zip file");

        var result = await CreateExtractor(temp.Path)
            .ExtractAsync(path, temp.Combine("out"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-EXTRACT-CORRUPT", result.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_entry_names(string name) =>
        Assert.True(ZipPackageExtractor.ValidateEntryName(name).IsFailure);
}

public class PackageVerifierTests
{
    private static PackageVerifier CreateVerifier(string root, Action<Phoenix.Core.Configuration.PhoenixOptions>? configure = null)
    {
        var options = TestFactory.CreateOptions(root, configure);
        return new PackageVerifier(
            TestFactory.Wrap(options),
            TestFactory.CreateFaultInjector(options),
            TestFactory.Logger<PackageVerifier>());
    }

    [Fact]
    public async Task Accepts_a_file_whose_hash_matches()
    {
        using var temp = new TempDirectory("verify-ok");
        var file = temp.WriteFile("package.zip", "phoenix");

        var verifier = CreateVerifier(temp.Path);
        var expected = await verifier.ComputeSha256Async(file, CancellationToken.None);
        var result = await verifier.VerifyChecksumAsync(file, expected.ToUpperInvariant(), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Rejects_a_file_whose_hash_does_not_match()
    {
        using var temp = new TempDirectory("verify-bad");
        var file = temp.WriteFile("package.zip", "phoenix");

        var result = await CreateVerifier(temp.Path)
            .VerifyChecksumAsync(file, new string('b', 64), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-VERIFY-MISMATCH", result.Error.Code);

        // A checksum failure is never retried: the same bytes will fail again.
        Assert.False(result.Error.IsRetryable);
        Assert.True(result.Error.AllowsRemoteFallback);
    }

    [Fact]
    public async Task Rejects_a_missing_file()
    {
        using var temp = new TempDirectory("verify-missing");

        var result = await CreateVerifier(temp.Path)
            .VerifyChecksumAsync(temp.Combine("nothing.zip"), new string('a', 64), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-VERIFY-MISSING", result.Error.Code);
    }

    [Fact]
    public async Task Refuses_to_skip_verification_when_a_checksum_is_required()
    {
        using var temp = new TempDirectory("verify-required");
        var file = temp.WriteFile("package.zip", "phoenix");

        var result = await CreateVerifier(temp.Path, o => o.Security.RequireChecksum = true)
            .VerifyChecksumAsync(file, string.Empty, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PX-VERIFY-NO-EXPECTED", result.Error.Code);
    }

    [Fact]
    public async Task Computes_the_documented_sha256()
    {
        using var temp = new TempDirectory("verify-hash");
        var file = temp.WriteFile("abc.txt", "abc");

        var hash = await CreateVerifier(temp.Path).ComputeSha256Async(file, CancellationToken.None);

        // The well-known SHA-256 of "abc".
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public void Signature_verification_is_skipped_unless_it_is_required()
    {
        using var temp = new TempDirectory("verify-signature");
        var file = temp.WriteFile("unsigned.exe", "not really an executable");

        Assert.True(CreateVerifier(temp.Path).VerifySignature(file).IsSuccess);

        var strict = CreateVerifier(temp.Path, o => o.Security.RequireSignature = true);
        Assert.True(strict.VerifySignature(file).IsFailure);
    }
}
