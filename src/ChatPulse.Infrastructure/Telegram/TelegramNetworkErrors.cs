using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>Classifies explicit transport failures without confusing local file IO with network IO.</summary>
internal static class TelegramNetworkErrors
{
    internal static bool IsTransportFailure(Exception exception)
    {
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException or HttpRequestException)
                return true;
            if (current is WebException webException && webException.Status is
                WebExceptionStatus.ConnectFailure or WebExceptionStatus.ConnectionClosed or
                WebExceptionStatus.NameResolutionFailure or WebExceptionStatus.Timeout or
                WebExceptionStatus.ReceiveFailure or WebExceptionStatus.SendFailure)
                return true;
        }
        return false;
    }
}
