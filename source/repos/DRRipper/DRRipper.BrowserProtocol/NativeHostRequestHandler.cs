using System.Text.Json;

namespace DRRipper.BrowserBridge
{
    /// <summary>
    /// Desktop-side outcome of handling one validated enqueue message.
    /// Implemented by the named-pipe bridge (in-process) and reused by the
    /// native host through <see cref="IDesktopForwarder"/>.
    /// </summary>
    public interface IDesktopForwarder
    {
        Task<BrowserEnqueueResponse> ForwardAsync(BrowserEnqueueRequest request, CancellationToken ct);
    }

    /// <summary>
    /// Validates one raw Native Messaging payload and forwards it (§4/§9).
    /// Shared by the native-host exe and unit tests (via <see cref="IDesktopForwarder"/>).
    /// Never throws: every failure maps to a categorized response (§26).
    /// </summary>
    public sealed class NativeHostRequestHandler
    {
        private readonly IDesktopForwarder _forwarder;

        public NativeHostRequestHandler(IDesktopForwarder forwarder)
        {
            _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        }

        public async Task<BrowserEnqueueResponse> HandlePayloadAsync(byte[] payload, CancellationToken ct)
        {
            BrowserEnqueueRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<BrowserEnqueueRequest>(
                    payload, NativeMessageFraming.JsonOptions);
            }
            catch (JsonException)
            {
                return Fail(string.Empty, BrowserErrorCodes.MalformedMessage, "Malformed JSON payload.");
            }
            catch (Exception ex)
            {
                return Fail(string.Empty, BrowserErrorCodes.MalformedMessage, "Unreadable payload: " + Trim(ex.Message));
            }
            if (request == null)
                return Fail(string.Empty, BrowserErrorCodes.MalformedMessage, "Empty message.");

            return await HandleRequestAsync(request, ct).ConfigureAwait(false);
        }

        public async Task<BrowserEnqueueResponse> HandleRequestAsync(BrowserEnqueueRequest request, CancellationToken ct)
        {
            var requestId = request.RequestId ?? string.Empty;
            if (!BrowserValidation.IsValidRequestId(requestId))
                return Fail(string.Empty, BrowserErrorCodes.MalformedMessage, "Missing requestId.");
            if (request.Version != BrowserBridgeProtocol.CurrentVersion)
                return Fail(requestId, BrowserErrorCodes.UnsupportedVersion,
                    $"Unsupported protocol version {request.Version}; host speaks {BrowserBridgeProtocol.CurrentVersion}.");
            if (!string.Equals(request.Type, BrowserBridgeProtocol.MessageTypeEnqueue, StringComparison.Ordinal))
                return Fail(requestId, BrowserErrorCodes.MalformedMessage,
                    "Unexpected message type: " + (request.Type ?? "(null)") + ".");

            if (!BrowserValidation.TryNormalizeUrl(request.Url, out var url, out var urlError))
                return Fail(requestId, urlError, "URL rejected: " + urlError + ".");
            request.Url = url;

            if (!BrowserValidation.TryNormalizeSource(request.Source, out var source))
                return Fail(requestId, BrowserErrorCodes.MalformedMessage, "Unknown source label.");
            request.Source = source;

            if (!BrowserValidation.TryNormalizeReferrer(request.Referrer, out _, out var refError))
                return Fail(requestId, refError, "Referrer rejected.");

            if (!BrowserValidation.TryFilterHeaders(request.Headers, out var allowed, out _, out var hdrError))
                return Fail(requestId, hdrError, "Headers rejected.");
            request.Headers = allowed;

            if (!BrowserValidation.TryValidateCookies(request.Cookies, out _, out var cookieError))
                return Fail(requestId, cookieError, "Cookies rejected.");

            if (request.SuggestedFileName != null &&
                request.SuggestedFileName.Length > BrowserBridgeProtocol.MaxRawFileNameLength)
                return Fail(requestId, BrowserErrorCodes.InvalidFileName, "Suggested filename too long.");

            BrowserEnqueueResponse response;
            try
            {
                response = await _forwarder.ForwardAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fail(requestId, BrowserErrorCodes.DesktopUnavailable,
                    "Desktop bridge unavailable: " + Trim(ex.Message));
            }
            if (response == null)
                return Fail(requestId, BrowserErrorCodes.InternalError, "Empty desktop response.");
            response.RequestId = requestId; // preserve correlation even if the far end rewrote it (§14)
            response.Version = BrowserBridgeProtocol.CurrentVersion;
            return response;
        }

        private static BrowserEnqueueResponse Fail(string requestId, string code, string message)
            => new() { RequestId = requestId, Accepted = false, ErrorCode = code, Message = message };

        private static string Trim(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "unknown error";
            // Never propagate secret material: cap length, strip newlines.
            message = message.Replace('\r', ' ').Replace('\n', ' ');
            return message.Length > 200 ? message.Substring(0, 200) : message;
        }
    }
}
