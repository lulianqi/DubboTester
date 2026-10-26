using DubboTester.Models;

namespace DubboTester.Services;

public static class RegistryConnectionSettings
{
    private const string NacosPrefix = "nacos://";

    public static string DisplayName(RegistryCenterType type) =>
        type == RegistryCenterType.Nacos ? "Nacos" : "ZooKeeper";

    public static string BuildConnectionString(RegistryCenterType type, string? address)
    {
        string normalized = address?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return string.Empty;
        if (type != RegistryCenterType.Nacos)
        {
            if (normalized.StartsWith(NacosPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("ZooKeeper 连接地址不能使用 nacos:// 前缀。", nameof(address));
            }
            return normalized;
        }

        if (normalized.StartsWith(NacosPrefix, StringComparison.OrdinalIgnoreCase)) return normalized;

        return NacosPrefix + normalized;
    }

    public static string? BuildMetadataConnectionString(TesterSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.MetadataCenterAddress)) return null;
        string address = settings.MetadataCenterAddress.Trim();
        return settings.RegistryType == RegistryCenterType.Nacos
            && !address.Contains("://", StringComparison.Ordinal)
                ? NacosPrefix + address
                : address;
    }

    public static string ResolveMetadataRoot(
        TesterSettings settings,
        string registryRoot,
        string? metadataConnectionString)
    {
        bool metadataUsesNacos = metadataConnectionString is null
            ? settings.RegistryType == RegistryCenterType.Nacos
            : metadataConnectionString.StartsWith(NacosPrefix, StringComparison.OrdinalIgnoreCase);
        bool followsSelectedRegistryRoot = string.IsNullOrWhiteSpace(settings.MetadataRootPath)
            || RegistryRootPaths.NormalizeRoot(settings.MetadataRootPath) == "/";
        if (followsSelectedRegistryRoot)
        {
            return metadataUsesNacos
                ? NacosGroup(registryRoot)
                : RegistryRootPaths.NormalizeRoot(registryRoot);
        }

        return metadataUsesNacos
            ? NacosGroup(settings.MetadataRootPath)
            : RegistryRootPaths.NormalizeRoot(settings.MetadataRootPath);
    }

    public static string NacosGroup(string? root) =>
        string.IsNullOrWhiteSpace(root) || RegistryRootPaths.NormalizeRoot(root) == "/"
            ? "DEFAULT_GROUP"
            : root.Trim().Trim('/');

    public static string RegistryRoot(RegistryCenterType type, string? root) =>
        type == RegistryCenterType.Nacos
            ? RegistryRootPaths.NormalizeRoot(NacosGroup(root))
            : RegistryRootPaths.NormalizeRoot(root);
}
