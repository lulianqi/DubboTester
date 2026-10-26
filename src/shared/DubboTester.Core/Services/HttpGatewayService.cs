using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DubboTester.Models;

namespace DubboTester.Services;

/// <summary>Independent, opt-in HTTP exposure of discovered, exact Dubbo signatures.</summary>
public sealed class HttpGatewayService : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // JSON responses and Razor-encoded text, never raw HTML: retain readable Chinese.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public const int MaximumBodyBytes = 1024 * 1024;
    public const int MaximumHistoryRecords = 2_000;
    public const int HistoryPageSize = 50;
    private const int RetainedTextLength = 16_384;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly DubboCatalogCache _cache;
    private readonly string _path;
    private HttpGatewayConfiguration _configuration;
    private Runtime? _runtime;
    private readonly List<HttpCallRecord> _history = [];
    private bool _disposed;

    public HttpGatewayService(IAppDataDirectoryProvider directory, DubboCatalogCache cache)
    {
        _cache = cache;
        _path = Path.Combine(directory.DirectoryPath, "http-config.json");
        // Do not silently overwrite an unreadable configuration with empty settings.
        _configuration = File.Exists(_path)
            ? JsonSerializer.Deserialize<HttpGatewayConfiguration>(File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidDataException("HTTP 服务配置文件为空。")
            : new();
        _configuration.HistoryLimit = Math.Clamp(_configuration.HistoryLimit, 10, MaximumHistoryRecords);
    }

    public event Action? Changed;
    public HttpGatewayConfiguration GetConfiguration() { lock (_sync) return _configuration.Clone(); }
    public IReadOnlyList<HttpGatewayMethod> GetMethods() { lock (_sync) return _runtime?.Methods ?? []; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            if (_runtime is not null || !_configuration.Enabled || string.IsNullOrWhiteSpace(_configuration.Settings.RegistryAddress)) return;
            Runtime runtime = await DiscoverAsync(_configuration.Settings, cancellationToken);
            lock (_sync) _runtime = runtime;
        }
        finally { _saveGate.Release(); }
    }

    public async Task SaveConfigurationAsync(HttpGatewayConfiguration configuration, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HttpGatewayConfiguration candidate = configuration.Clone();
        candidate.Settings.RegistryAddress = candidate.Settings.RegistryAddress.Trim();
        candidate.Settings.RegistryRootPath = string.IsNullOrWhiteSpace(candidate.Settings.RegistryRootPath) ? "/" : candidate.Settings.RegistryRootPath.Trim();
        candidate.Settings.OnlyRefreshServiceNames = false;
        candidate.Settings.RequestTimeoutMs = Math.Clamp(candidate.Settings.RequestTimeoutMs, 100, 300_000);
        candidate.Settings.MetadataFallbackTimeoutMs = Math.Clamp(candidate.Settings.MetadataFallbackTimeoutMs, 100, 60_000);
        candidate.HistoryLimit = Math.Clamp(candidate.HistoryLimit, 10, MaximumHistoryRecords);
        if (candidate.Enabled && string.IsNullOrWhiteSpace(candidate.Settings.RegistryAddress))
            throw new ArgumentException("请先填写注册中心地址。");
        if (candidate.AuthenticationEnabled && string.IsNullOrWhiteSpace(candidate.AccessToken))
            throw new ArgumentException("开启鉴权时访问令牌不能为空。");

        await _saveGate.WaitAsync(cancellationToken);
        Runtime? replacement = null;
        try
        {
            bool changed = SettingsKey(candidate.Settings) != SettingsKey(_configuration.Settings);
            bool rebuild = candidate.Enabled && (changed || refresh || !_configuration.Enabled || _runtime is null);
            if (rebuild && !string.IsNullOrWhiteSpace(candidate.Settings.RegistryAddress))
                replacement = await DiscoverAsync(candidate.Settings, cancellationToken);
            await PersistAsync(candidate, cancellationToken);
            Runtime? previous = null;
            lock (_sync)
            {
                if (rebuild)
                {
                    previous = _runtime;
                    _runtime = replacement;
                    replacement = null;
                }
                _configuration = candidate;
                TrimHistory();
            }
            previous?.Release();
        }
        finally { replacement?.Release(); _saveGate.Release(); }
        Changed?.Invoke();
    }

    public async Task SetMethodEnabledAsync(string id, bool enabled)
    {
        await _saveGate.WaitAsync();
        try
        {
            HttpGatewayConfiguration candidate = GetConfiguration();
            var methods = GetMethods();
            if (!methods.Any(method => method.Id == id && method.Available))
                throw new InvalidOperationException("方法签名不可用，请刷新列表后重试。");
            if (candidate.EnableAllMethods)
            {
                candidate.EnableAllMethods = false;
                candidate.EnabledMethodIds = methods.Where(method => method.Available).Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
            }
            if (enabled) candidate.EnabledMethodIds.Add(id);
            else candidate.EnabledMethodIds.Remove(id);
            await PersistAsync(candidate, CancellationToken.None);
            lock (_sync) _configuration = candidate;
        }
        finally { _saveGate.Release(); }
        Changed?.Invoke();
    }

    private async Task PersistAsync(HttpGatewayConfiguration configuration, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(configuration, JsonOptions), cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<Runtime> DiscoverAsync(TesterSettings settings, CancellationToken cancellationToken)
    {
        using var session = new DubboSession(_cache);
        TesterSettings snapshot = settings.Clone();
        snapshot.OnlyRefreshServiceNames = false;
        var catalog = await session.LoadServicesAsync(snapshot, forceRefresh: true, cancellationToken);
        var methods = new Dictionary<string, HttpGatewayMethod>(StringComparer.Ordinal);
        foreach (DubboServiceItem service in catalog.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await session.LoadServiceDetailsAsync(snapshot, service, forceRefresh: true, cancellationToken);
                foreach (string name in service.Methods.Order(StringComparer.Ordinal))
                {
                    try
                    {
                        var signatures = await session.LoadMethodSignaturesAsync(snapshot, service.Name, name, service.RegistryRootPath, cancellationToken);
                        if (signatures.Count == 0) throw new InvalidOperationException("未读取到精确方法签名，不能安全开放 HTTP 接口。");
                        foreach (var signature in signatures)
                        {
                            var variants = service.Providers.Where(provider => !provider.Disabled
                                && (string.IsNullOrEmpty(signature.Version) || provider.Version == signature.Version)
                                && (string.IsNullOrEmpty(signature.Group) || provider.Group == signature.Group))
                                .Select(provider => (Version: provider.Version ?? "", Group: provider.Group ?? "")).Distinct().ToArray();
                            if (variants.Length == 0) throw new InvalidOperationException("方法没有匹配的可用 Provider。");
                            var parameters = DubboTypeStructureBuilder.Build(signature);
                            string example = DubboTypeStructureBuilder.GenerateExampleJson(parameters);
                            if (parameters.Count == 1 && parameters[0].Structure.Kind != JavaTypeKind.Collection) example = $"[{example}]";
                            foreach (var variant in variants)
                            {
                                string id = MethodId(snapshot, service, name, signature.ParameterTypes, variant.Version, variant.Group);
                                methods[id] = new(id, service.RegistryRootPath, service.Name, name, signature.ParameterTypes,
                                    variant.Version, variant.Group, signature.Signature, example);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { AddUnavailable(name, exception.Message); }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { AddUnavailable("（方法加载失败）", exception.Message); }

            void AddUnavailable(string name, string error)
            {
                string id = MethodId(snapshot, service, name, [], "", "") + "-unavailable";
                methods[id] = new(id, service.RegistryRootPath, service.Name, name, [], "", "", name, "[]", error);
            }
        }
        return new Runtime(snapshot, methods.Values.OrderBy(method => method.RegistryRootPath, StringComparer.Ordinal)
            .ThenBy(method => method.ServiceName, StringComparer.Ordinal).ThenBy(method => method.Signature, StringComparer.Ordinal).ToArray(), _cache);
    }

    private static string MethodId(TesterSettings settings, DubboServiceItem service, string method, string[] types, string version, string group) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { settings.RegistryCacheAddress, service.RegistryRootPath, service.Name, method, types, version, group }))));

    private static string SettingsKey(TesterSettings settings) => JsonSerializer.Serialize(settings);

    public async Task<HttpGatewayResponse> HandleAsync(string requestTarget, string httpMethod, string? authorization,
        string? contentType, Stream body, string remoteAddress, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string requestText = string.Empty;
        string registryRootPath = string.Empty;
        string serviceName = string.Empty;
        string methodName = requestTarget.Split('?', 2)[0];
        Runtime? lease = null;
        HttpGatewayResponse result;
        try { result = await ProcessAsync(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { result = new(499, false, null, "请求已取消。", stopwatch.ElapsedMilliseconds); }
        catch (JsonException) { result = new(400, false, null, "请求必须是有效的 JSON 对象。", stopwatch.ElapsedMilliseconds); }
        catch (ArgumentException exception) { result = new(400, false, null, exception.Message, stopwatch.ElapsedMilliseconds); }
        catch (Exception) { result = new(502, false, null, "Dubbo 调用或注册中心连接失败，请检查服务配置及可用性。", stopwatch.ElapsedMilliseconds); }
        finally { lease?.Release(); }
        result = result with { ElapsedMs = stopwatch.ElapsedMilliseconds };
        lock (_sync)
        {
            _history.Insert(0, new(DateTimeOffset.Now, registryRootPath, serviceName, Limit(methodName),
                Limit(requestTarget), result.StatusCode, result.ElapsedMs, remoteAddress, Limit(requestText),
                Limit(JsonSerializer.Serialize(result, JsonOptions))));
            TrimHistory();
        }
        Changed?.Invoke();
        return result;

        async Task<HttpGatewayResponse> ProcessAsync()
        {
            // Capture once, including requests rejected before invocation (disabled/auth/etc.).
            // Keep the read bounded and preserve any captured prefix when reading fails.
            bool bodyWithinLimit = await CaptureRequestAsync();
            HttpGatewayRoute? route = HttpGatewayRoute.Parse(requestTarget);
            if (route is null) return new(404, false, null, "接口地址无效，请使用 /api/dubbo/注册目录/服务名称/方法名称。");
            registryRootPath = route.RegistryRootPath;
            serviceName = route.ServiceName;
            methodName = route.MethodName;
            if (!string.Equals(httpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return new(405, false, null, "仅支持 POST 请求。");
            HttpGatewayConfiguration config = GetConfiguration();
            if (!config.Enabled) return new(503, false, null, "HTTP 服务未启用。");
            if (!Authorized(config, authorization)) return new(401, false, null, "HTTP 访问令牌无效。");
            if (!string.Equals(contentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                return new(415, false, null, "请使用 Content-Type: application/json。");
            await InitializeAsync(cancellationToken);
            HttpGatewayMethod? method;
            lock (_sync)
            {
                // Recheck after asynchronous discovery: a user may have disabled the gateway meanwhile.
                config = _configuration;
                if (!config.Enabled) return new(503, false, null, "HTTP 服务未启用。");
                if (!Authorized(config, authorization)) return new(401, false, null, "HTTP 访问令牌无效。");
                // Resolve against all available variants before checking switches: turning one off
                // must never silently redirect an ambiguous request to a different overload/provider.
                var matches = _runtime?.Methods.Where(candidate => candidate.Available && route.Matches(candidate)).Take(2).ToArray() ?? [];
                if (matches.Length == 0)
                    return new(404, false, null, "接口不存在或尚未开启。");
                if (matches.Length > 1)
                    return new(409, false, null, "匹配到多个版本、分组或重载，请通过 version、group、parameterTypes 查询参数明确指定。可在“调用方式”中复制完整地址；无参数重载使用 parameterTypes=。");
                method = matches[0];
                if (!config.EnableAllMethods && !config.EnabledMethodIds.Contains(method.Id))
                    return new(404, false, null, "接口不存在或尚未开启。");
                lease = _runtime!;
                lease.Retain();
            }
            registryRootPath = method.RegistryRootPath;
            serviceName = method.ServiceName;
            methodName = method.MethodName;
            if (!bodyWithinLimit) return new(413, false, null, "请求正文不能超过 1 MB。");
            using var document = JsonDocument.Parse(requestText);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Array) return new(400, false, null, "请提供 arguments 参数数组；无参数方法使用 []。");
            if (root.EnumerateObject().Any(property => property.Name is not ("arguments" or "attachments")))
                return new(400, false, null, "仅支持 arguments 和 attachments，服务、版本、分组及签名由接口配置决定。");
            if (arguments.GetArrayLength() != method.ParameterTypes.Length)
                return new(400, false, null, $"参数数量不匹配，需要 {method.ParameterTypes.Length} 个参数。");
            string attachmentsJson = "";
            if (root.TryGetProperty("attachments", out var attachments))
            {
                if (attachments.ValueKind != JsonValueKind.Object || attachments.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String))
                    return new(400, false, null, "attachments 必须是字符串键值对象。");
                // These attachments could override the identity fixed by the published endpoint.
                if (attachments.EnumerateObject().Any(property => property.Name is "path" or "interface" or "group" or "version" or "method" or "generic"))
                    return new(400, false, null, "attachments 不能覆盖服务、版本、分组或调用方式。");
                attachmentsJson = attachments.GetRawText();
            }
            var output = await lease.Session(method.RegistryRootPath).InvokeAsync(lease.Settings, new InvocationInput
            {
                RegistryRootPath = method.RegistryRootPath, ServiceName = method.ServiceName, MethodName = method.MethodName,
                JavaParameterTypes = string.Join(", ", method.ParameterTypes), Version = method.Version, Group = method.Group,
                RequestJson = arguments.GetRawText(), AttachmentsJson = attachmentsJson
            }, cancellationToken);
            object? data = output.Content;
            if (output.Success && !string.IsNullOrWhiteSpace(output.Content))
            {
                try { data = JsonSerializer.Deserialize<JsonElement>(output.Content); }
                catch (JsonException) { /* Some providers return plain text. */ }
            }
            return new(output.Success ? 200 : 502, output.Success, output.Success ? data : null,
                output.Success ? null : output.Error ?? "Dubbo 调用失败。");
        }

        async Task<bool> CaptureRequestAsync()
        {
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            try
            {
                while (buffer.Length <= MaximumBodyBytes)
                {
                    int remaining = MaximumBodyBytes + 1 - (int)buffer.Length;
                    int count = await body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken);
                    if (count == 0) break;
                    buffer.Write(chunk, 0, count);
                }
                return buffer.Length <= MaximumBodyBytes;
            }
            finally
            {
                requestText = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }
    }

    private static bool Authorized(HttpGatewayConfiguration config, string? authorization)
    {
        if (!config.AuthenticationEnabled) return true;
        if (string.IsNullOrWhiteSpace(config.AccessToken)) return false;
        if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(config.AccessToken), Encoding.UTF8.GetBytes(authorization[7..].Trim()));
    }

    public HttpCallRecordPage GetHistoryPage(int page, string method = "", string request = "", string response = "")
    {
        lock (_sync)
        {
            var matches = _history.Where(record => $"{record.RegistryRootPath} {record.Method}".Contains(method, StringComparison.OrdinalIgnoreCase)
                && record.Request.Contains(request, StringComparison.OrdinalIgnoreCase)
                && record.Response.Contains(response, StringComparison.OrdinalIgnoreCase)).ToArray();
            int pageCount = Math.Max(1, (matches.Length + HistoryPageSize - 1) / HistoryPageSize);
            int pageNumber = Math.Clamp(page, 1, pageCount);
            return new(matches.Skip((pageNumber - 1) * HistoryPageSize).Take(HistoryPageSize).ToArray(), matches.Length, _history.Count);
        }
    }

    public void ClearHistory() { lock (_sync) _history.Clear(); Changed?.Invoke(); }
    private void TrimHistory() { if (_history.Count > _configuration.HistoryLimit) _history.RemoveRange(_configuration.HistoryLimit, _history.Count - _configuration.HistoryLimit); }
    private static string Limit(string value) => value.Length <= RetainedTextLength ? value : value[..RetainedTextLength] + "\n（记录内容过长，已截断；不影响实际调用响应）";
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _runtime?.Release();
            _runtime = null;
        }
    }

    // A refresh can replace a runtime while requests are in flight. Each request holds a lease;
    // retired clients are disposed only after their final invocation has completed.
    private sealed class Runtime(TesterSettings settings, HttpGatewayMethod[] methods, DubboCatalogCache cache)
    {
        private int _references = 1;
        private readonly ConcurrentDictionary<string, Lazy<DubboSession>> _sessions = new(StringComparer.Ordinal);
        public TesterSettings Settings { get; } = settings;
        public HttpGatewayMethod[] Methods { get; } = methods;
        public DubboSession Session(string root) => _sessions.GetOrAdd(root, _ => new(() => new DubboSession(cache))).Value;
        public void Retain() => Interlocked.Increment(ref _references);
        public void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            foreach (var session in _sessions.Values) if (session.IsValueCreated) session.Value.Dispose();
        }
    }
}
