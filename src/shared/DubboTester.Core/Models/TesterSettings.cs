using DubboNet.Clients;

namespace DubboTester.Models;

public sealed class TesterSettings
{
    public string ZooKeeperAddress { get; set; } = string.Empty;
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

    public TesterSettings Clone() => (TesterSettings)MemberwiseClone();
}
