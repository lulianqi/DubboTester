using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DubboTester.Models;
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
    private readonly IMcpRequestContextAccessor _requestContextAccessor;
    private McpGatewayConfiguration _configuration;
    private readonly List<McpCallRecord> _history = [];
    private bool _disposed;

    public McpGatewayService(
        IAppDataDirectoryProvider appDataDirectoryProvider,
        DubboCatalogCache catalogCache,
        IMcpRequestContextAccessor requestContextAccessor)
    {
        _configurationPath = Path.Combine(
            appDataDirectoryProvider.DirectoryPath,
            "mcp-config.json");
        _requestContextAccessor = requestContextAccessor;
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

    public static bool IsConnectionContextEquivalent(
        McpGatewayConfiguration left,
        McpGatewayConfiguration right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(
                left.ZooKeeperAddress.Trim(),
                right.ZooKeeperAddress.Trim(),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                RegistryRootPaths.Parse(left.RegistryRootPath).CacheKey,
                RegistryRootPaths.Parse(right.RegistryRootPath).CacheKey,
                StringComparison.Ordinal)
            && string.Equals(
                left.MetadataCenterAddress.Trim(),
                right.MetadataCenterAddress.Trim(),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NormalizeOptionalRoot(left.MetadataRootPath),
                NormalizeOptionalRoot(right.MetadataRootPath),
                StringComparison.Ordinal);
    }

    public async Task<McpToolValidationSummary> RevalidateConfiguredToolsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        McpGatewayConfiguration snapshot = GetConfiguration();
        if (snapshot.Methods.Count == 0)
            return new McpToolValidationSummary(0, 0, 0);
        if (string.IsNullOrWhiteSpace(snapshot.ZooKeeperAddress))
            throw new ArgumentException("重新校验 MCP 工具前必须填写 ZooKeeper 地址。");

        var results = new Dictionary<string, ToolValidationResult>(StringComparer.Ordinal);
        DateTimeOffset validatedAt = DateTimeOffset.Now;
        try
        {
            DubboCatalogLoadResult catalog = await _runtimeSession.LoadServicesAsync(
                snapshot.ToTesterSettings(),
                forceRefresh: true,
                cancellationToken);
            var detailErrors = new Dictionary<string, string?>(StringComparer.Ordinal);
            var signatureResults = new Dictionary<string, SignatureLoadResult>(StringComparer.Ordinal);

            foreach (McpDubboMethodConfiguration method in snapshot.Methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string root = RegistryRootPaths.NormalizeRoot(method.RegistryRootPath);
                DubboServiceItem? service = catalog.Services.FirstOrDefault(candidate =>
                    string.Equals(
                        RegistryRootPaths.NormalizeRoot(candidate.RegistryRootPath),
                        root,
                        StringComparison.Ordinal)
                    && string.Equals(candidate.Name, method.ServiceName, StringComparison.Ordinal));
                if (service is null)
                {
                    results[method.Id] = new ToolValidationResult(
                        McpToolValidationStatus.ServiceMissing,
                        $"当前注册中心的 {root} 中未找到服务 {method.ServiceName}。",
                        validatedAt);
                    continue;
                }

                string serviceKey = $"{root}\n{service.Name}";
                if (!detailErrors.TryGetValue(serviceKey, out string? detailError))
                {
                    try
                    {
                        await _runtimeSession.LoadServiceDetailsAsync(
                            snapshot.ToTesterSettings(),
                            service,
                            forceRefresh: true,
                            cancellationToken);
                        detailErrors[serviceKey] = null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        detailError = exception.Message;
                        detailErrors[serviceKey] = detailError;
                    }
                }

                if (!string.IsNullOrWhiteSpace(detailError))
                {
                    results[method.Id] = new ToolValidationResult(
                        McpToolValidationStatus.ValidationFailed,
                        $"读取服务 {method.ServiceName} 失败：{detailError}",
                        validatedAt);
                    continue;
                }
                if (!service.Methods.Contains(method.MethodName, StringComparer.Ordinal))
                {
                    results[method.Id] = new ToolValidationResult(
                        McpToolValidationStatus.MethodMissing,
                        $"当前服务 {method.ServiceName} 中未找到方法 {method.MethodName}。",
                        validatedAt);
                    continue;
                }

                if (method.ParameterTypesManuallyConfigured)
                {
                    results[method.Id] = new ToolValidationResult(
                        McpToolValidationStatus.Available,
                        "已确认服务与方法存在；Java 参数类型沿用手动配置。",
                        validatedAt);
                    continue;
                }

                string signatureKey = $"{serviceKey}\n{method.MethodName}";
                if (!signatureResults.TryGetValue(signatureKey, out SignatureLoadResult? loaded))
                {
                    try
                    {
                        IReadOnlyList<MethodSignatureItem> signatures =
                            await _runtimeSession.LoadMethodSignaturesAsync(
                                snapshot.ToTesterSettings(),
                                method.ServiceName,
                                method.MethodName,
                                method.RegistryRootPath,
                                cancellationToken);
                        loaded = new SignatureLoadResult(signatures, string.Empty);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        loaded = new SignatureLoadResult([], exception.Message);
                    }
                    signatureResults[signatureKey] = loaded;
                }

                if (!string.IsNullOrWhiteSpace(loaded.Error))
                {
                    results[method.Id] = new ToolValidationResult(
                        McpToolValidationStatus.ValidationFailed,
                        $"读取方法签名失败：{loaded.Error}",
                        validatedAt);
                    continue;
                }

                bool signatureMatches = loaded.Signatures.Any(signature =>
                    signature.ParameterTypes.SequenceEqual(method.ParameterTypes, StringComparer.Ordinal)
                    && string.Equals(signature.Version?.Trim() ?? string.Empty, method.Version, StringComparison.Ordinal)
                    && string.Equals(signature.Group?.Trim() ?? string.Empty, method.Group, StringComparison.Ordinal));
                results[method.Id] = signatureMatches
                    ? new ToolValidationResult(
                        McpToolValidationStatus.Available,
                        "已在当前注册中心匹配到服务、方法和精确签名。",
                        validatedAt)
                    : new ToolValidationResult(
                        McpToolValidationStatus.SignatureMismatch,
                        loaded.Signatures.Count == 0
                            ? "已找到服务和方法，但未读取到可校验的方法签名。"
                            : $"方法 {method.MethodName} 存在，但当前签名与已保存配置不一致。",
                        validatedAt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            foreach (McpDubboMethodConfiguration method in snapshot.Methods)
            {
                results[method.Id] = new ToolValidationResult(
                    McpToolValidationStatus.ValidationFailed,
                    $"连接当前注册中心并校验工具失败：{exception.Message}",
                    validatedAt);
            }
        }

        McpGatewayConfiguration latest = GetConfiguration();
        if (!IsConnectionContextEquivalent(snapshot, latest))
            throw new InvalidOperationException("校验期间连接环境再次发生变化，请重新校验。");
        foreach (McpDubboMethodConfiguration method in latest.Methods)
        {
            if (!results.TryGetValue(method.Id, out ToolValidationResult? result)) continue;
            method.ValidationStatus = result.Status;
            method.ValidationMessage = result.Message;
            method.ValidatedAt = result.ValidatedAt;
        }
        await SaveConfigurationAsync(latest, cancellationToken);

        int availableCount = results.Values.Count(result =>
            result.Status == McpToolValidationStatus.Available);
        return new McpToolValidationSummary(
            results.Count,
            availableCount,
            results.Count - availableCount);
    }

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

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            McpGatewayConfiguration current;
            lock (_sync) current = _configuration.Clone();
            if (!IsConnectionContextEquivalent(current, normalized))
            {
                MarkToolsPendingValidation(
                    normalized.Methods,
                    "连接环境已更改，等待在新的注册中心重新校验。");
            }

            Validate(normalized);
            string json = JsonSerializer.Serialize(normalized, ConfigurationJsonOptions);
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
            .Where(method => method.IsAvailableForMcp)
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
        if (method.ValidationStatus != McpToolValidationStatus.Available)
        {
            string reason = string.IsNullOrWhiteSpace(method.ValidationMessage)
                ? "尚未在当前连接环境中校验通过。"
                : method.ValidationMessage;
            return ErrorResult($"工具 {request.Name} 当前不可用：{reason}");
        }

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
                RegistryRootPath = method.RegistryRootPath,
                ServiceName = method.ServiceName,
                MethodName = method.MethodName,
                RemoteAddress = _requestContextAccessor.GetRemoteAddress(),
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
        limit = Math.Clamp(limit, 10, 5_000);
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
            ? "/"
            : RegistryRootPaths.NormalizeRoot(configuration.RegistryRootPath);
        configuration.MetadataCenterAddress = configuration.MetadataCenterAddress.Trim();
        configuration.MetadataRootPath = configuration.MetadataRootPath.Trim();
        configuration.RequestTimeoutMs = Math.Clamp(configuration.RequestTimeoutMs, 100, 600_000);
        configuration.HistoryLimit = Math.Clamp(configuration.HistoryLimit, 10, 5_000);
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
            method.ValidationMessage = method.ValidationMessage.Trim();
            if (!Enum.IsDefined(method.ValidationStatus))
            {
                method.ValidationStatus = McpToolValidationStatus.PendingValidation;
                method.ValidationMessage = "工具校验状态无效，等待重新校验。";
                method.ValidatedAt = null;
            }
        }
        return configuration;
    }

    private static void MarkToolsPendingValidation(
        IEnumerable<McpDubboMethodConfiguration> methods,
        string message)
    {
        foreach (McpDubboMethodConfiguration method in methods)
        {
            method.ValidationStatus = McpToolValidationStatus.PendingValidation;
            method.ValidationMessage = message;
            method.ValidatedAt = null;
        }
    }

    private static string NormalizeOptionalRoot(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : RegistryRootPaths.NormalizeRoot(value);

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
            ValidateMethodConfiguration(method);
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

    public static void ValidateMethodConfiguration(McpDubboMethodConfiguration method)
    {
        IReadOnlyList<string> missingFields = GetMissingRequiredMethodFields(method);
        if (missingFields.Count > 0)
        {
            throw new ArgumentException(
                $"{method.ServiceName}#{method.MethodName} 缺少必填项：{string.Join("、", missingFields)}。");
        }
        if (!IsValidToolName(method.ToolName))
            throw new ArgumentException($"MCP 工具名 {method.ToolName} 无效；仅支持 1-64 个字母、数字、下划线或连字符。");
    }

    public static IReadOnlyList<string> GetMissingRequiredMethodFields(
        McpDubboMethodConfiguration method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var missingFields = new List<string>(7);
        if (string.IsNullOrWhiteSpace(method.RegistryRootPath)) missingFields.Add("注册目录");
        if (string.IsNullOrWhiteSpace(method.ServiceName)) missingFields.Add("服务");
        if (string.IsNullOrWhiteSpace(method.MethodName)) missingFields.Add("方法");
        if (string.IsNullOrWhiteSpace(method.ToolName)) missingFields.Add("MCP 工具名");
        if (string.IsNullOrWhiteSpace(method.Description)) missingFields.Add("方法描述");
        if (string.IsNullOrWhiteSpace(method.TriggerCondition)) missingFields.Add("适用/触发条件");
        if (string.IsNullOrWhiteSpace(method.ParameterGuidance)) missingFields.Add("参数要求");
        return missingFields;
    }

    private static bool IsValidToolName(string name) =>
        name.Length is >= 1 and <= 64
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string GenerateAccessToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private sealed record ToolValidationResult(
        McpToolValidationStatus Status,
        string Message,
        DateTimeOffset ValidatedAt);

    private sealed record SignatureLoadResult(
        IReadOnlyList<MethodSignatureItem> Signatures,
        string Error);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtimeSession.Dispose();
        _saveGate.Dispose();
    }
}
