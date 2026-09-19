using System.Security.Cryptography;

namespace Phoenix.Core.Diagnostics;

/// <summary>
/// Short correlation identifier shown to users ("Reference: PX-7F2A91") and attached to
/// every log entry of a Phoenix run, so support can find the matching diagnostics.
/// </summary>
public static class OperationId
{
    public const string LogPropertyName = "OperationId";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[3];
        RandomNumberGenerator.Fill(bytes);
        return $"PX-{Convert.ToHexString(bytes)}";
    }
}
