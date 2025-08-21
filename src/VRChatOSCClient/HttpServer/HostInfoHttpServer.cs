using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace VRChatOSCClient.HttpServer;

internal class HostInfoHttpServer(ILogger<HostInfoHttpServer> logger) : IDisposable
{
    private readonly ILogger<HostInfoHttpServer> _logger = logger;

    private HttpListener _listener = null!;
    private Func<bool, string> _responseProvider = null!;
    private CancellationTokenSource _cts = null!;
    private Task? _serverTask;

    public void Start(string binding, ushort port, Func<bool, string> responseProvider) {
        _logger.LogInformation("HostInfoHttpServer starting");
        string prefix = $"http://{binding}:{port}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _responseProvider = responseProvider ?? throw new ArgumentNullException(nameof(responseProvider));

        if (_cts is null ||  _cts.IsCancellationRequested) {
            _cts = new();
        }

        _listener.Start();
        _serverTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public async Task StopAsync(CancellationToken token = default) {
        _cts.Cancel();
        _listener.Stop();
        if (_serverTask != null) {
            await Task.Run(async () => await _serverTask.ConfigureAwait(false), token);
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
                HttpListenerContext ctx;
                try {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch(Exception ex) when (ex is HttpListenerException or ObjectDisposedException && ct.IsCancellationRequested) {
                    break;
                }

                _ = Task.Run(() => HandleContextAsync(ctx), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogError(ex, "Encountered error while listening for HOST_INFO requests");
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

        try {
            // Check if the request is valid.
            if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) || req.Url == null || !string.Equals(req.Url.AbsolutePath, "/", StringComparison.Ordinal)) {

                res.StatusCode = (int)HttpStatusCode.NotFound;
                res.Close();

                return;
            }

            // check if the parameters contain 'HOST_INFO'
            bool hasHostInfo = !string.IsNullOrEmpty(req.Url.Query) && req.Url.Query.Contains("HOST_INFO", StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation("Answering request {rawUrl}", ctx.Request.RawUrl);

            string responseString = _responseProvider(hasHostInfo) ?? string.Empty;

            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            res.StatusCode = (int)HttpStatusCode.OK;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = buffer.Length;

            // Write body
            await res.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
            res.Close();
        }
        catch (Exception ex) {
            try {
                _logger.LogError(ex, "Unable to respond to request");

                res.StatusCode = (int)HttpStatusCode.InternalServerError;
                res.Close();
            }
            catch { }
        }
    }

    public void Dispose() {
        GC.SuppressFinalize(this);

        _cts.Cancel();
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }
}