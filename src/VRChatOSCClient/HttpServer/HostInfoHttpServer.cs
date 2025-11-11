using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace VRChatOSCClient.HttpServer;

internal class HostInfoHttpServer(ILogger<HostInfoHttpServer> logger) : IDisposable
{
    private readonly ILogger<HostInfoHttpServer> _logger = logger;

    private HttpListener? _listener = null!;
    private Func<bool, string> _responseProvider = null!;
    private Task? _serverTask;

    public void Start(string binding, ushort port, Func<bool, string> responseProvider, CancellationToken token) {
        _logger.LogHostStarting();
        string prefix = $"http://{binding}:{port}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _responseProvider = responseProvider ?? throw new ArgumentNullException(nameof(responseProvider));

        _listener.Start();
        _serverTask = ListenLoopAsync(token);
    }

    public async Task StopAsync(CancellationToken token = default) {
        _listener?.Stop();
        if (_serverTask != null) {
            await _serverTask.WaitAsync(token).ConfigureAwait(false);
        }

        _listener = null!;
        _responseProvider = null!;
        _serverTask = null;
    }

    /// <summary>
    /// Loop that listens for requests
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    private async Task ListenLoopAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                HttpListenerContext? ctx = null;
                try {
                    if(_listener is not null) {
                        ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                }
                catch(Exception ex) when (ex is HttpListenerException or ObjectDisposedException && ct.IsCancellationRequested) {
                    break;
                }

                if(ctx is not null) {
                    _ = HandleContextAsync(ctx, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogListeningRequestError(ex);
        }
    }

    /// <summary>
    /// Handle the incoming request.
    /// </summary>
    /// <param name="ctx"></param>
    /// <returns></returns>
    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken token) {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;

        try {
            // Check if the request is valid.
            if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) || req.Url == null || !string.Equals(req.Url.AbsolutePath, "/", StringComparison.Ordinal)) {

                res.StatusCode = (int)HttpStatusCode.NotFound;
                res.Close();

                return;
            }

            // check if the parameters contain 'HOST_INFO'
            bool hasHostInfo = !string.IsNullOrEmpty(req.Url.Query) && req.Url.Query.Contains("HOST_INFO", StringComparison.OrdinalIgnoreCase);

            _logger.LogAnsweringRequest(ctx.Request.RawUrl);

            string responseString = _responseProvider(hasHostInfo) ?? string.Empty;

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            res.StatusCode = (int)HttpStatusCode.OK;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = buffer.Length;

            // Write body
            await res.OutputStream.WriteAsync(buffer, token).ConfigureAwait(false);
            res.Close();
        }
        catch (Exception ex) {
            try {
                _logger.LogUnableToRespond(ex);

                res.StatusCode = (int)HttpStatusCode.InternalServerError;
                res.Close();
            }
            catch { }
        }
    }

    public void Dispose() {
        GC.SuppressFinalize(this);
        try { _listener?.Close(); } catch { }
    }
}

static partial class HostInfoHttpServerLogger {
    [LoggerMessage(Level = LogLevel.Information, Message = "HostInfoHttpServer starting")]
    public static partial void LogHostStarting(this ILogger<HostInfoHttpServer> logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Answering request {rawUrl}")]
    public static partial void LogAnsweringRequest(this ILogger<HostInfoHttpServer> logger, string? rawUrl);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unable to respond to request")]
    public static partial void LogUnableToRespond(this ILogger<HostInfoHttpServer> logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Encountered error while listening for HOST_INFO requests")]
    public static partial void LogListeningRequestError(this ILogger<HostInfoHttpServer> logger, Exception exception);

}