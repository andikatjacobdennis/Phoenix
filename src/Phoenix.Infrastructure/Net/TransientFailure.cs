using System.Net;
using System.Net.Sockets;

namespace Phoenix.Infrastructure.Net;

/// <summary>
/// Decides what is worth retrying. Phoenix retries things that might be different in a
/// second - a dropped connection, a 503 - and never retries things that will not be:
/// a bad checksum, a 401, a malformed manifest.
/// </summary>
public static class TransientFailure
{
    public static bool IsTransient(Exception exception) => exception switch
    {
        TaskCanceledException or TimeoutException => true,
        SocketException socket => IsTransient(socket.SocketErrorCode),
        HttpRequestException http => IsTransientHttp(http),
        IOException => true,
        _ => false,
    };

    private static bool IsTransientHttp(HttpRequestException exception)
    {
        if (exception.InnerException is SocketException socket)
        {
            return IsTransient(socket.SocketErrorCode);
        }

        return exception.StatusCode is null || IsTransient(exception.StatusCode.Value);
    }

    private static bool IsTransient(SocketError error) => error switch
    {
        // A name that does not resolve is a configuration problem, not a blip.
        SocketError.HostNotFound or SocketError.NoData => false,
        _ => true,
    };

    public static bool IsTransient(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.InternalServerError => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => true,
        _ => false,
    };

    /// <summary>Exponential backoff with a small jitter, bounded at 30 seconds.</summary>
    public static TimeSpan BackoffFor(int attempt, int baseDelayMilliseconds)
    {
        var exponent = Math.Min(attempt, 6);
        var delay = baseDelayMilliseconds * Math.Pow(2, exponent);
        var jitter = Random.Shared.Next(0, baseDelayMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Min(delay + jitter, 30_000));
    }
}
