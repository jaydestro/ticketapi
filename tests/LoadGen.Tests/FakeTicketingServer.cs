using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LoadGen.Tests;

internal sealed record ReceivedRequest(
    string Method,
    string Path,
    string? Authorization,
    string? IdempotencyKey,
    string Body);

internal sealed class FakeTicketingServer : IAsyncDisposable
{
    private const string FullOpenApi = """
        {"openapi":"3.0.1","paths":{
          "/api/events/{id}":{"get":{}},
          "/api/events/upcoming":{"get":{}},
          "/api/events/city/{city}":{"get":{}},
          "/api/events":{"post":{}},
          "/api/orders":{"post":{}},
          "/api/orders/customer/{customerId}":{"get":{}},
          "/api/orders/event/{eventId}":{"get":{}}
        }}
        """;

    private const string ComparisonOpenApi = """
        {"openapi":"3.0.1","paths":{
          "/api/events/{id}":{"get":{}},
          "/api/events/upcoming":{"get":{}},
          "/api/events/city/{city}":{"get":{}},
          "/api/orders/customer/{customerId}":{"get":{}},
          "/api/orders/event/{eventId}":{"get":{}}
        }}
        """;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _listenerTask;
    private readonly string _openApi;
    private readonly int _openApiStatus;
    private readonly int _apiStatus;
    private readonly Func<string, int>? _apiStatusResolver;
    private readonly Func<string, string?> _queryScopeResolver;

    public FakeTicketingServer(
        bool includeWriteRoutes = true,
        int openApiStatus = StatusCodes.Ok,
        int apiStatus = StatusCodes.Ok,
        Func<string, int>? apiStatusResolver = null,
        Func<string, string?>? queryScopeResolver = null)
    {
        _openApi = includeWriteRoutes ? FullOpenApi : ComparisonOpenApi;
        _openApiStatus = openApiStatus;
        _apiStatus = apiStatus;
        _apiStatusResolver = apiStatusResolver;
        _queryScopeResolver = queryScopeResolver ?? GetQueryScope;
        var port = GetAvailablePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _listenerTask = ListenAsync();
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

    public async Task WaitForTrafficAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!Requests.Any(request => request.Path != "/openapi/v1.json"))
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("LoadGen did not send traffic before the timeout.");
            }

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        try
        {
            await _listenerTask;
        }
        catch (HttpListenerException) when (_stopping.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Close();
            _stopping.Dispose();
        }
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested)
            {
                return;
            }

            _ = RespondAsync(context);
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            Requests.Enqueue(new ReceivedRequest(
                context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath ?? "/",
                context.Request.Headers["Authorization"],
                context.Request.Headers["Idempotency-Key"],
                body));

            var isOpenApi = context.Request.Url?.AbsolutePath == "/openapi/v1.json";
            var path = context.Request.Url?.AbsolutePath ?? "/";
            context.Response.StatusCode = isOpenApi
                ? _openApiStatus
                : _apiStatusResolver?.Invoke(path) ?? _apiStatus;
            var payload = isOpenApi ? _openApi : "[]";
            if (!isOpenApi)
            {
                context.Response.Headers["x-ms-request-charge"] = "2.5";
                var queryScope = _queryScopeResolver(path);
                if (queryScope is not null)
                {
                    context.Response.Headers["x-cosmos-query-scope"] = queryScope;
                }
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetQueryScope(string path)
    {
        if (path.StartsWith("/api/events/event-", StringComparison.Ordinal))
        {
            return "point-read";
        }

        if (path is "/api/events" or "/api/orders")
        {
            return "not-applicable";
        }

        return "cross-partition";
    }

    internal static class StatusCodes
    {
        public const int Ok = 200;
        public const int Unauthorized = 401;
        public const int TooManyRequests = 429;
        public const int InternalServerError = 500;
    }
}