namespace DubboTester.Services;

public sealed class ClientEnvironment(bool isDesktop)
{
    public bool IsDesktop { get; } = isDesktop;

    public string SettingsStorageDescription => IsDesktop
        ? "设置保存在本机，仅对当前应用生效。"
        : "设置保存在当前浏览器中。";

    public string StorageLocation => IsDesktop ? "本机应用中" : "当前浏览器中";
}
