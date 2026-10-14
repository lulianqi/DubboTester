using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DubboTester.Models;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace DubboTester.Services;

public sealed class McpGatewayService : IDisposable
{
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _configurationPath;
    private readonly DubboSession _runtimeSession;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private McpGatewayConfiguration _configuration;
    private readonly List<McpCallRecord> _history = [];
    private bool _disposed;

    public McpGatewayService(
        IWebHostEnvironment environment,
        DubboCatalogCache catalogCache,
        IHttpContextAccessor httpContextAccessor)
    {
        _configurationPath = Path.Combine(environment.ContentRootPath, "App_Data", "mcp-config.json");
        _httpContextAccessor = httpContextAccessor;
        _runtimeSession = new DubboSession(catalogCache);
        _configuration = LoadConfiguration();
    }

    public event Action? Changed;

    public string ConfigurationPath => _configurationPath;

    public McpGatewayConfiguration GetConfiguration()
    {
        lock (_sync) return _configuration.Clone();
    }

    public IReadOnlyList<McpCallRecord> GetHistory()
    {
        lock (_sync) return _history.ToArray();
    }

    public Task<DubboCatalogLoadResult> LoadServicesAsync(
        TesterSettings settings,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        _runtimeSession.LoadServicesAsync(settings, forceRefresh, cancellationToken);

    public Task LoadServiceDetailsAsync(
        TesterSettings settings,
        DubboServiceItem service,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        _runtimeSession.LoadServiceDetailsAsync(settings, service, forceRefresh, cancellationToken);

    public Task<IReadOnlyList<MethodSignatureItem>> LoadMethodSignaturesAsync(
        TesterSettings settings,
        string serviceName,
        string methodName,
        string? registryRootPath = null,
        CancellationToken cancellationToken = default) =>
        _runtimeSession.LoadMethodSignaturesAsync(
            settings,
            serviceName,
            methodName,
            registryRootPath,
            cancellationToken);

    public Task<InvocationOutput> DebugInvokeAsync(
        McpDubboMethodConfiguration method,
        string requestJson,
        string javaParameterTypes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InvocationInput input = BuildDebugInvocation(method, requestJson, javaParameterTypes);
        McpGatewayConfiguration configuration = GetConfiguration();
        return _runtimeSession.InvokeAsync(
            configuration.ToTesterSettings(),
            input,
            cancellationToken);
    }

    public static InvocationInput BuildDebugInvocation(
        McpDubboMethodConfiguration method,
        string requestJson,
        string javaParameterTypes)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (string.IsNullOrWhiteSpace(method.ServiceName)
            || string.IsNullOrWhiteSpace(method.MethodName))
        {
            throw new ArgumentException("请先选择需要调试的 MCP 配置方法。", nameof(method));
        }

        return new InvocationInput
        {
            Route = InvocationRoute.Registry,
            RegistryRootPath = method.RegistryRootPath,
            ServiceName = method.ServiceName,
            MethodName = method.MethodName,
            RequestJson = requestJson,
            JavaParameterTypes = javaParameterTypes.Trim(),
            Version = method.Version,
            Group = method.Group
        };
    }

    public bool IsEnabled
    {
        get { lock (_sync) return _configuration.Enabled; }
    }

    public bool IsAuthenticationEnabled
    {
        get { lock (_sync) return _configuration.AuthenticationEnabled; }
    }

    public bool IsAuthorized(string? authorizationHeader, string? explicitToken = null)
    {
        string expected;
        lock (_sync) expected = _configuration.AccessToken;
        if (string.IsNullOrWhiteSpace(expected)) return false;

        string supplied = explicitToken?.Trim() ?? string.Empty;
        const string bearerPrefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(supplied)
            && authorizationHeader?.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            supplied = authorizationHeader[bearerPrefix.Length..].Trim();
        }

        if (supplied.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(expected));
    }

    public async Task SaveConfigurationAsync(
        McpGatewayConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);

        McpGatewayConfiguration normalized = Normalize(configuration.Clone());
        Validate(normalized);
        string json = JsonSerializer.Serialize(normalized, ConfigurationJsonOptions);

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            string? directory = Path.GetDirectoryName(_configurationPath);
            Directory.CreateDirectory(directory!);
            string temporaryPath = _configurationPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, _configurationPath, overwrite: true);

            lock (_sync)
            {
                _configuration = normalized;
                TrimHistoryCore(normalized.HistoryLimit);
            }
        }
        finally
        {
            _saveGate.Release();
        }

        Changed?.Invoke();
    }

    public async Task RegenerateAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        McpGatewayConfiguration configuration = GetConfiguration();
        configuration.AccessToken = GenerateAccessToken();
        await SaveConfigurationAsync(configuration, cancellationToken);
    }

    public void ClearHistory()
    {
        lock (_sync) _history.Clear();
        Changed?.Invoke();
    }

    public IReadOnlyList<Tool> GetTools()
    {
        McpGatewayConfiguration configuration = GetConfiguration();
        if (!configuration.Enabled) return [];

        return configuration.Methods
            .Where(method => method.Enabled)
            .OrderBy(method => method.ToolName, StringComparer.Ordinal)
            .Select(method => new Tool
            {
                Name = method.ToolName,
                Title = $"{method.ServiceName}.{method.MethodName}",
                Description = McpToolSchemaBuilder.BuildDescription(method),
                InputSchema = ParseSchema(method.InputSchemaJson)
            })
            .ToArray();
    }

    public async Task<CallToolResult> CallToolAsync(
        CallToolRequestParams request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        McpGatewayConfiguration configuration = GetConfiguration();
        if (!configuration.Enabled)
            return ErrorResult("MCP 服务全局开关未启用。");

        McpDubboMethodConfiguration? method = configuration.Methods.FirstOrDefault(item =>
            item.Enabled && string.Equals(item.ToolName, request.Name, StringComparison.Ordinal));
        if (method is null)
            return ErrorResult($"工具 {request.Name} 不存在或未启用。");

        var stopwatch = Stopwatch.StartNew();
        string requestJson = "[]";
        string response = string.Empty;
        string error = string.Empty;
        bool success = false;
        try
        {
            requestJson = BuildRequestJson(request.Arguments, method.ParameterTypes.Length);
            string attachmentsJson = BuildAttachmentsJson(request.Arguments);
            var input = new InvocationInput
            {
                Route = InvocationRoute.Registry,
                RegistryRootPath = method.RegistryRootPath,
                ServiceName = method.ServiceName,
                MethodName = method.MethodName,
                RequestJson = requestJson,
                JavaParameterTypes = string.Join(",", method.ParameterTypes),
                Version = method.Version,
                Group = method.Group,
                AttachmentsJson = attachmentsJson
            };
            InvocationOutput output = await _runtimeSession.InvokeAsync(
                configuration.ToTesterSettings(),
                input,
                cancellationToken);
            success = output.Success;
            response = output.Success ? output.Content : output.Error ?? "Dubbo 调用失败。";
            error = output.Success ? string.Empty : response;
            return output.Success ? SuccessResult(response) : ErrorResult(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error = "MCP 工具调用已取消。";
            response = error;
            throw;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            response = error;
            return ErrorResult(error);
        }
        finally
        {
            stopwatch.Stop();
            AddHistory(new McpCallRecord
            {
                StartedAt = DateTimeOffset.Now - stopwatch.Elapsed,
                ToolName = method.ToolName,
                ServiceName = method.ServiceName,
                MethodName = method.MethodName,
                RemoteAddress = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                RequestJson = requestJson,
                Response = response,
                Success = success,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Error = error
            });
        }
    }

    public static string BuildRequestJson(
        IDictionary<string, JsonElement>? arguments,
        int expectedParameterCount)
    {
        if (expectedParameterCount == 0)
        {
            if (arguments is not null
                && arguments.TryGetValue("arguments", out JsonElement unexpected)
                && unexpected.ValueKind == JsonValueKind.Array
                && unexpected.GetArrayLength() > 0)
            {
                throw new ArgumentException("该方法没有参数，请勿传入 arguments。", nameof(arguments));
            }
            return "[]";
        }

        if (arguments is null || !arguments.TryGetValue("arguments", out JsonElement values))
            throw new ArgumentException("缺少 arguments 参数数组。", nameof(arguments));
        if (values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("arguments 必须是 JSON 数组。", nameof(arguments));
        if (values.GetArrayLength() != expectedParameterCount)
            throw new ArgumentException(
                $"arguments 数量（{values.GetArrayLength()}）与 Java 参数数量（{expectedParameterCount}）不一致。",
                nameof(arguments));
        return values.GetRawText();
    }

    private static string BuildAttachmentsJson(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("attachments", out JsonElement attachments))
            return string.Empty;
        if (attachments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("attachments 必须是 JSON 对象。", nameof(arguments));
        return attachments.GetRawText();
    }

    private static JsonElement ParseSchema(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using JsonDocument document = JsonDocument.Parse("{\"type\":\"object\"}");
            return document.RootElement.Clone();
        }
    }

    private static CallToolResult SuccessResult(string content) => new()
    {
        Content = [new TextContentBlock { Text = content }],
        IsError = false
    };

    private static CallToolResult ErrorResult(string error) => new()
    {
        Content = [new TextContentBlock { Text = error }],
        IsError = true
    };

    private void AddHistory(McpCallRecord record)
    {
        lock (_sync)
        {
            _history.Insert(0, record);
            TrimHistoryCore(_configuration.HistoryLimit);
        }
        Changed?.Invoke();
    }

    private void TrimHistoryCore(int limit)
    {
        limit = Math.Clamp(limit, 10, 2_000);
        if (_history.Count > limit) _history.RemoveRange(limit, _history.Count - limit);
    }

    private McpGatewayConfiguration LoadConfiguration()
    {
        McpGatewayConfiguration configuration;
        try
        {
            configuration = File.Exists(_configurationPath)
                ? JsonSerializer.Deserialize<McpGatewayConfiguration>(
                    File.ReadAllText(_configurationPath),
                    ConfigurationJsonOptions) ?? new McpGatewayConfiguration()
                : new McpGatewayConfiguration();
        }
        catch (JsonException)
        {
            configuration = new McpGatewayConfiguration();
        }

        configuration = Normalize(configuration);
        return configuration;
    }

    private static McpGatewayConfiguration Normalize(McpGatewayConfiguration configuration)
    {
        configuration.ZooKeeperAddress = configuration.ZooKeeperAddress.Trim();
        configuration.RegistryRootPath = string.IsNullOrWhiteSpace(configuration.RegistryRootPath)
            ? "/dubbo"
            : RegistryRootPaths.NormalizeRoot(configuration.RegistryRootPath);
        configuration.MetadataCenterAddress = configuration.MetadataCenterAddress.Trim();
        configuration.MetadataRootPath = configuration.MetadataRootPath.Trim();
        configuration.RequestTimeoutMs = Math.Clamp(configuration.RequestTimeoutMs, 100, 600_000);
        configuration.HistoryLimit = Math.Clamp(configuration.HistoryLimit, 10, 2_000);
        if (configuration.AuthenticationEnabled
            && string.IsNullOrWhiteSpace(configuration.AccessToken))
            configuration.AccessToken = GenerateAccessToken();
        configuration.Methods ??= [];
        foreach (McpDubboMethodConfiguration method in configuration.Methods)
        {
            method.Id = string.IsNullOrWhiteSpace(method.Id) ? Guid.NewGuid().ToString("N") : method.Id.Trim();
            method.ToolName = method.ToolName.Trim();
            method.RegistryRootPath = string.IsNullOrWhiteSpace(method.RegistryRootPath)
                ? configuration.RegistryRootPath
                : RegistryRootPaths.NormalizeRoot(method.RegistryRootPath);
            method.ServiceName = method.ServiceName.Trim();
            method.MethodName = method.MethodName.Trim();
            method.ParameterTypes ??= [];
            method.ParameterTypes = method.ParameterTypes.Select(type => type.Trim()).ToArray();
            method.ReturnType = method.ReturnType.Trim();
            method.Version = method.Version.Trim();
            method.Group = method.Group.Trim();
            method.Description = method.Description.Trim();
            method.TriggerCondition = method.TriggerCondition.Trim();
            method.ParameterGuidance = method.ParameterGuidance.Trim();
        }
        return configuration;
    }

    public static void Validate(McpGatewayConfiguration configuration)
    {
        if (configuration.Enabled && string.IsNullOrWhiteSpace(configuration.ZooKeeperAddress))
            throw new ArgumentException("启用 MCP 服务前必须填写 ZooKeeper 地址。");
        if (configuration.Enabled
            && configuration.AuthenticationEnabled
            && string.IsNullOrWhiteSpace(configuration.AccessToken))
            throw new ArgumentException("启用 MCP 服务前必须配置访问令牌。");

        var enabledToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (McpDubboMethodConfiguration method in configuration.Methods)
        {
            if (!method.Enabled) continue;
            if (string.IsNullOrWhiteSpace(method.Description)
                || string.IsNullOrWhiteSpace(method.TriggerCondition)
                || string.IsNullOrWhiteSpace(method.ParameterGuidance))
            {
                throw new ArgumentException(
                    $"启用 {method.ServiceName}#{method.MethodName} 前，请完整填写方法描述、触发条件和参数要求。");
            }
            if (!IsValidToolName(method.ToolName))
                throw new ArgumentException($"MCP 工具名 {method.ToolName} 无效；仅支持 1-64 个字母、数字、下划线或连字符。");
            if (!enabledToolNames.Add(method.ToolName))
                throw new ArgumentException($"MCP 工具名 {method.ToolName} 重复。");
            try
            {
                using JsonDocument document = JsonDocument.Parse(method.InputSchemaJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ArgumentException($"{method.ToolName} 的输入 Schema 不是有效的 JSON 对象。");
            }
        }
    }

    private static bool IsValidToolName(string name) =>
        name.Length is >= 1 and <= 64
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string GenerateAccessToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtimeSession.Dispose();
        _saveGate.Dispose();
    }
}
