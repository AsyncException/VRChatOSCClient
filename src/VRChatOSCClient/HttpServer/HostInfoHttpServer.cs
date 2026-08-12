using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace VRChatOSCClient.HttpServer;

/// <summary>
/// Provides an HTTP Server that provides host information through a REST api call
/// </summary>
/// <param name="logger"></param>
internal sealed class HostInfoHttpServer(ILogger<HostInfoHttpServer> logger) : IAsyncDisposable
{
    private RunningState? _state;

    private Task _serverTask = Task.CompletedTask;

    /// <summary>
    /// Start a HttpListener on the specified binding and port, and use the provided response provider to generate responses for incoming requests.
    /// </summary>
    /// <param name="binding"></param>
    /// <param name="port"></param>
    /// <param name="responseProvider"></param>
    /// <param name="token"></param>
    public void Start(string binding, ushort port, Func<bool, string> responseProvider, CancellationToken token) {
        logger.LogHostStarting();
        
        var state = new RunningState(binding, port, responseProvider);
        state.HttpListener.Start();
        _state = state;

        _serverTask = ListenLoopAsync();
    }

    /// <summary>
    /// Stops the HTTP server and cleans up resources
    /// </summary>
    /// <returns></returns>
    public async Task StopAsync() {
        var state = _state;
        if(state is null) {
            return;
        }

        state.HttpListener.Close();
        
        await state.Cts.CancelAsync();
        await _serverTask;
        
        state.Dispose();
        _state = null;
    }

    /// <summary>
    /// Loop that listens for requests
    /// </summary>
    /// <returns></returns>
    private async Task ListenLoopAsync() {
        try {
            var state = _state;
            if(state is null) return;

            while (!state.Cts.IsCancellationRequested) {
                try {
                    if (state.HttpListener is null) {
                        throw new InvalidOperationException("Listener is not initialized");
                    }

                    var ctx = await state.HttpListener.GetContextAsync().WaitAsync(state.Cts.Token);
                    await HandleContextAsync(ctx);
                }
                catch (TaskCanceledException) { }
                catch (OperationCanceledException) { }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995) { } // Ignore abort because of thread exit. This is basically the same as OperationCancelledException
            }
        }
        catch (Exception ex) {
            logger.LogListeningRequestError(ex);
        }
    }

    /// <summary>
    /// Handle the incoming request.
    /// </summary>
    /// <param name="ctx"></param>
    /// <returns></returns>
    private async Task HandleContextAsync(HttpListenerContext ctx) {
        var req = ctx.Request;
        var res = ctx.Response;

        var state = _state;
        if(state is null) {
            res.StatusCode = (int)HttpStatusCode.InternalServerError;
            res.Close();
            return;
        }

        try {
            // Check if the request is valid.
            if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) || req.Url == null || !string.Equals(req.Url.AbsolutePath, "/", StringComparison.Ordinal)) {

                res.StatusCode = (int)HttpStatusCode.NotFound;
                res.Close();

                return;
            }

            // check if the parameters contain 'HOST_INFO'
            var hasHostInfo = !string.IsNullOrEmpty(req.Url.Query) && req.Url.Query.Contains("HOST_INFO", StringComparison.OrdinalIgnoreCase);

            logger.LogAnsweringRequest(ctx.Request.RawUrl);

            var responseString = state.ResponseProvider(hasHostInfo);

            var buffer = Encoding.UTF8.GetBytes(responseString);
            res.StatusCode = (int)HttpStatusCode.OK;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = buffer.Length;

            // Write body
            await res.OutputStream.WriteAsync(buffer, state.Cts.Token);
            res.Close();
        }
        catch (Exception ex) {
            try {
                logger.LogUnableToRespond(ex);

                res.StatusCode = (int)HttpStatusCode.InternalServerError;
                res.Close();
            }
            catch
            {
                // ignored
            }
        }
    }

    #region IDisposable Support
    private bool _disposedValue;
    public async ValueTask DisposeAsync()
    {
        if (_disposedValue) return;
        await StopAsync();

        _disposedValue = true;
    }
    #endregion

    private class RunningState : IDisposable
    {
        public HttpListener HttpListener { get; }
        public Func<bool, string> ResponseProvider { get; }
        public CancellationTokenSource Cts { get; }

        public RunningState(string binding, ushort port, Func<bool, string> responseProvider) {
            HttpListener = new HttpListener();
            HttpListener.Prefixes.Add($"http://{binding}:{port}/");
            ResponseProvider = responseProvider;
            Cts = new CancellationTokenSource();
        }

        public void Dispose() {
            Cts.Dispose();
        }
    }
}

static partial class HostInfoHttpServerLogger {
    [LoggerMessage(Level = LogLevel.Information, Message = "HostInfoHttpServer starting")]
    public static partial void LogHostStarting(this ILogger<HostInfoHttpServer> logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Answering request {rawUrl}")]
    public static partial void LogAnsweringRequest(this ILogger<HostInfoHttpServer> logger, string? rawUrl);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unable to respond to request")]
    public static partial void LogUnableToRespond(this ILogger<HostInfoHttpServer> logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Encountered error while listening for HOST_INFO requests")]
    public static partial void LogListeningRequestError(this ILogger<HostInfoHttpServer> logger, Exception exception);
}