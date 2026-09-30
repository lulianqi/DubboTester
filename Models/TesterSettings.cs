using DubboNet.Clients;

namespace DubboTester.Models;

public sealed class TesterSettings
{
    public string ZooKeeperAddress { get; set; } = string.Empty;
    public string RegistryRootPath { get; set; } = "/dubbo";
    public string MetadataCenterAddress { get; set; } = string.Empty;
    public string MetadataRootPath { get; set; } = "/dubbo";
    public int RequestTimeoutMs { get; set; } = 60_000;
    public int MetadataFallbackTimeoutMs { get; set; } = 5_000;
    public int HistoryLimit { get; set; } = 500;
    public DubboClient.LoadBalanceMode LoadBalanceMode { get; set; } =
        DubboClient.LoadBalanceMode.Random;
    public bool SaveResponseInHistory { get; set; }
    public bool HideUnmatched { get; set; }
    public bool SearchMethodsOnly { get; set; }
    public bool CaseSensitiveSearch { get; set; }
    public bool PreloadAllServiceMethods { get; set; }

    public TesterSettings Clone() => (TesterSettings)MemberwiseClone();
}
