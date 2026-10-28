using DubboNet.Clients;
using DubboTester.Services;
using System.Text.Json.Serialization;

namespace DubboTester.Models;

public enum RegistryCenterType
{
    ZooKeeper = 0,
    Nacos = 1
}

public sealed class TesterSettings
{
    public RegistryCenterType RegistryType { get; set; } = RegistryCenterType.ZooKeeper;

    // The persisted property name is retained so existing browser settings migrate
    // without losing the configured ZooKeeper address.
    public string ZooKeeperAddress { get; set; } = string.Empty;

    [JsonIgnore]
    public string RegistryAddress
    {
        get => ZooKeeperAddress;
        set => ZooKeeperAddress = value;
    }

    [JsonIgnore]
    public string RegistryConnectionString =>
        RegistryConnectionSettings.BuildConnectionString(RegistryType, RegistryAddress);

    [JsonIgnore]
    public string RegistryCacheAddress => $"{RegistryType}\n{RegistryConnectionString}";

    [JsonIgnore]
    public string RegistryDisplayName => RegistryConnectionSettings.DisplayName(RegistryType);

    public string RegistryRootPath { get; set; } = "/";
    public string MetadataCenterAddress { get; set; } = string.Empty;
    public string MetadataRootPath { get; set; } = string.Empty;
    public int RequestTimeoutMs { get; set; } = 10_000;
    public int MetadataFallbackTimeoutMs { get; set; } = 10_000;
    public int HistoryLimit { get; set; } = 5_000;
    public DubboClient.LoadBalanceMode LoadBalanceMode { get; set; } =
        DubboClient.LoadBalanceMode.Random;
    public bool SaveResponseInHistory { get; set; } = true;
    public bool HideUnmatched { get; set; }
    public bool SearchMethodsOnly { get; set; }
    public bool CaseSensitiveSearch { get; set; }
    public bool OnlyRefreshServiceNames { get; set; }
    public bool AutoFillRequestExample { get; set; } = true;

    public TesterSettings Clone() => (TesterSettings)MemberwiseClone();
}
