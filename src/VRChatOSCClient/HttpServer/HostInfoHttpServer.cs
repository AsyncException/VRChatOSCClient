using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using Tmds.Linux;
using VRChatOSCClient.Utilities;

namespace VRChatOSCClient.HttpServer;

/// <summary>
/// Provides an HTTP Server that provides host information through a REST api call
/// </summary>
/// <param name="logger"></param>
internal class HostInfoHttpServer(ILogger<HostInfoHttpServer> logger) : IDisposable
{
    private readonly ILogger<HostInfoHttpServer> _logger = logger;

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
        _logger.LogHostStarting();
        
        var state = new RunningState(binding, port, responseProvider);
        state.HttpListener.Start();
        _state = state;

        _serverTask = ListenLoopAsync();
    }

    /// <summary>
    /// Stops the HTTP server and cleans up resources
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public void Stop() {
        var state = _state;
        if(state is null) {
            return;
        }

        state.HttpListener.Close();
        state.CTS.Cancel();

        state.Dispose();
        _state = null;
    }

    /// <summary>
    /// Loop that listens for requests
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    private async Task ListenLoopAsync() {
        try {
            var state = _state;
            if(state is null) {
                return;
            }

            while (!state.CTS.IsCancellationRequested) {
                try {
                    if (state.HttpListener is null) {
                        throw new InvalidOperationException("Listener is not initialized");
                    }

                    HttpListenerContext? ctx = await state.HttpListener.GetContextAsync().WaitAsync(state.CTS.Token);
                    await HandleContextAsync(ctx);
                }
                catch (TaskCanceledException) { }
                
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
    private async Task HandleContextAsync(HttpListenerContext ctx) {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;

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
            bool hasHostInfo = !string.IsNullOrEmpty(req.Url.Query) && req.Url.Query.Contains("HOST_INFO", StringComparison.OrdinalIgnoreCase);

            _logger.LogAnsweringRequest(ctx.Request.RawUrl);

            string responseString = state.ResponseProvider(hasHostInfo) ?? string.Empty;

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            res.StatusCode = (int)HttpStatusCode.OK;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = buffer.Length;

            // Write body
            await res.OutputStream.WriteAsync(buffer, state.CTS.Token);
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

    #region IDisposable Support
    private bool _disposedValue;

    protected virtual void Dispose(bool disposing) {
        if(!_disposedValue) {
            if (disposing) { }
            Stop();

            _disposedValue = true;
        }
    }

    public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion

    private class RunningState : IDisposable
    {
        public HttpListener HttpListener { get; }
        public Func<bool, string> ResponseProvider { get; }
        public CancellationTokenSource CTS { get; }

        public RunningState(string binding, ushort port, Func<bool, string> responseProvider) {
            HttpListener = new HttpListener();
            HttpListener.Prefixes.Add($"http://{binding}:{port}/");
            ResponseProvider = responseProvider;
            CTS = new();
        }

        public void Dispose() {
            CTS.Dispose();
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Encountered error while listening for HOST_INFO requests")]
    public static partial void LogListeningRequestError(this ILogger<HostInfoHttpServer> logger, Exception exception);

}