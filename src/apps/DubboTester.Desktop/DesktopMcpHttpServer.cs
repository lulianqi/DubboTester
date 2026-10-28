using System.Net;
using System.Text.Json;
using DubboTester.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DubboTester.Desktop;

/// <summary>
/// Hosts the official MCP SDK's streamable HTTP transport inside the desktop
/// process without taking a dependency on the ASP.NET Core shared runtime,
/// which is not available for Mac Catalyst applications.
/// </summary>
internal sealed class DesktopMcpHttpServer : IDesktopMcpServer
{
    private readonly HttpListener _listener = new();
    private readonly McpGatewayService _gateway;
    private readonly HttpGatewayService _httpGateway;
    private readonly DesktopMcpRequestContextAccessor _requestContextAccessor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DesktopMcpHttpServer> _logger;
    private readonly IServiceProvider _services;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private bool _disposed;

    public DesktopMcpHttpServer(
        McpGatewayService gateway,
        HttpGatewayService httpGateway,
        DesktopMcpRequestContextAccessor requestContextAccessor,
        ILoggerFactory loggerFactory,
        IServiceProvider services)
    {
        _gateway = gateway;
        _httpGateway = httpGateway;
        _requestContextAccessor = requestContextAccessor;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DesktopMcpHttpServer>();
        _services = services;

        foreach (string prefix in DesktopMcpEndpoint.ListenerPrefixes)
        {
            _listener.Prefixes.Add(prefix);
        }
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener.IsListening) return Task.CompletedTask;

        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        _logger.LogInformation(
            "Desktop MCP endpoint started at {Endpoint}",
            DesktopMcpEndpoint.Address);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                HttpListenerContext context = await _listener
                    .GetContextAsync()
                    .WaitAsync(cancellationToken);
                _ = ProcessRequestSafelyAsync(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Desktop MCP listener failed while accepting a request.");
            }
        }
    }

    private async Task ProcessRequestSafelyAsync(
        HttpListenerContext context,
        CancellationToken shutdownToken)
    {
        try
        {
            await ProcessRequestAsync(context, shutdownToken);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            context.Response.Abort();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Desktop MCP request contained invalid JSON.");
            await WriteJsonAsync(context.Response, 400, new { error = "MCP 请求 JSON 无效。" });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Desktop MCP request failed.");
            await WriteJsonAsync(context.Response, 500, new { error = "MCP 请求处理失败。" });
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task ProcessRequestAsync(
        HttpListenerContext context,
        CancellationToken shutdownToken)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        const string httpPrefix = "/api/dubbo/";
        if (request.Url?.AbsolutePath.StartsWith(httpPrefix, StringComparison.Ordinal) == true)
        {
            var result = await _httpGateway.HandleAsync(request.RawUrl ?? request.Url.PathAndQuery,
                request.HttpMethod, request.Headers["Authorization"], request.ContentType,
                request.InputStream, request.RemoteEndPoint?.Address.ToString() ?? "", shutdownToken);
            response.StatusCode = result.StatusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            if (result.StatusCode == 401) response.Headers["WWW-Authenticate"] = "Bearer";
            if (result.StatusCode == 405) response.Headers["Allow"] = "POST";
            await JsonSerializer.SerializeAsync(response.OutputStream, result, HttpGatewayService.JsonOptions, shutdownToken);
            return;
        }

        if (!string.Equals(request.Url?.AbsolutePath.TrimEnd('/'), "/mcp", StringComparison.Ordinal))
        {
            await WriteJsonAsync(response, 404, new { error = "Not found." });
            return;
        }

        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Allow"] = "POST";
            await WriteJsonAsync(response, 405, new { error = "仅支持 POST MCP 请求。" });
            return;
        }

        if (!_gateway.IsEnabled)
        {
            await WriteJsonAsync(response, 503, new { error = "MCP 服务未启用。" });
            return;
        }

        if (_gateway.IsAuthenticationEnabled
            && !_gateway.IsAuthorized(
                request.Headers["Authorization"],
                request.Headers["X-MCP-Token"]))
        {
            response.Headers["WWW-Authenticate"] = "Bearer";
            await WriteJsonAsync(response, 401, new { error = "MCP 访问令牌无效。" });
            return;
        }

        JsonRpcMessage message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(
            request.InputStream,
            McpJsonUtilities.DefaultOptions,
            shutdownToken) ?? throw new JsonException("MCP 请求正文为空。");

        using IDisposable requestAddress = _requestContextAccessor.Push(
            request.RemoteEndPoint?.Address.ToString());
        await using var transport = new StreamableHttpServerTransport(_loggerFactory)
        {
            Stateless = true,
            FlowExecutionContextFromRequests = true
        };
        McpServer server = McpServer.Create(
            transport,
            CreateServerOptions(),
            _loggerFactory,
            _services);

        Task serverTask = server.RunAsync(shutdownToken);
        try
        {
            await using var responseBody = new MemoryStream();
            bool hasResponse = await transport.HandlePostRequestAsync(
                message,
                responseBody,
                shutdownToken);

            if (!hasResponse)
            {
                response.StatusCode = 202;
            }
            else
            {
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                response.Headers["Cache-Control"] = "no-cache, no-store";
                response.ContentLength64 = responseBody.Length;
                responseBody.Position = 0;
                await responseBody.CopyToAsync(response.OutputStream, shutdownToken);
            }
        }
        finally
        {
            await server.DisposeAsync();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
            }
        }
    }

    private McpServerOptions CreateServerOptions() => new()
    {
        ServerInfo = new Implementation
        {
            Name = "Dubbo Tester",
            Version = "2.1.2"
        },
        ServerInstructions = "仅提供在 Dubbo Tester 的 MCP 管理页面中明确启用的 Dubbo 方法。",
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [.. _gateway.GetTools()],
                TimeToLive = TimeSpan.Zero,
                CacheScope = CacheScope.Private
            }),
            CallToolHandler = (context, cancellationToken) =>
                new ValueTask<CallToolResult>(
                    _gateway.CallToolAsync(context.Params!, cancellationToken))
        }
    };

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        object value)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync();
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }
}
