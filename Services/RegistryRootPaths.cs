namespace DubboTester.Services;

public sealed record RegistryRootSelection(
    IReadOnlyList<string> ExplicitRoots,
    bool AutoDiscover,
    string CacheKey);

/// <summary>
/// Parses the user-facing registry directory setting. A single normalized path keeps
/// its historical cache key; multiple paths use an order-independent composite key.
/// An empty value or '/' asks the registry loader to discover usable first-level roots.
/// </summary>
public static class RegistryRootPaths
{
    private static readonly char[] Separators = [',', ';', '，', '；', '\r', '\n', '\t', ' '];

    public static RegistryRootSelection Parse(string? value)
    {
        string[] tokens = string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool autoDiscover = tokens.Length == 0;
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
            string root = NormalizeRoot(token);
            if (root == "/")
            {
                autoDiscover = true;
            }
            else
            {
                roots.Add(root);
            }
        }

        string[] orderedRoots = roots.Order(StringComparer.Ordinal).ToArray();
        string cacheKey = !autoDiscover && orderedRoots.Length == 1
            ? orderedRoots[0]
            : string.Join("\n", autoDiscover ? ["/", .. orderedRoots] : orderedRoots);
        return new RegistryRootSelection(orderedRoots, autoDiscover, cacheKey);
    }

    public static string NormalizeRoot(string? value)
    {
        string root = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        if (!root.StartsWith('/')) root = "/" + root;
        return root.Length > 1 ? root.TrimEnd('/') : root;
    }

    public static string ResolveInvocationRoot(string? configuredRoots, string? selectedRoot)
    {
        string selected = NormalizeRoot(selectedRoot);
        if (!string.IsNullOrWhiteSpace(selectedRoot) && selected != "/") return selected;

        RegistryRootSelection selection = Parse(configuredRoots);
        if (selection.ExplicitRoots.Count > 0) return selection.ExplicitRoots[0];
        throw new ArgumentException("当前设置会自动发现注册目录，请先从服务目录中选择要调用的服务。");
    }

    public static string ResolveMetadataRoot(string? configuredMetadataRoot, string registryRoot) =>
        string.IsNullOrWhiteSpace(configuredMetadataRoot)
            ? NormalizeRoot(registryRoot)
            : NormalizeRoot(configuredMetadataRoot);

    public static string DisplayName(string root)
    {
        string normalized = NormalizeRoot(root);
        return normalized == "/"
            ? "/"
            : normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
    }
}
