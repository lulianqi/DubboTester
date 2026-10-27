namespace DubboTester.Services;

public sealed class LocalGatewayListenerStatus
{
    public bool IsUnavailable { get; private set; }
    public bool IsPortOccupied { get; private set; }
    public int Port { get; private set; }
    public string Headline => IsPortOccupied
        ? $"工作端口 {Port} 已被占用"
        : $"工作端口 {Port} 监听失败";
    public string Guidance => IsPortOccupied
        ? $"请释放占用 {Port} 端口的程序，然后重启 Dubbo Tester。"
        : $"请检查 {Port} 端口占用和系统监听权限，解决后重启 Dubbo Tester。";

    public event Action? Changed;

    public void ReportFailure(int port, bool isPortOccupied)
    {
        Port = port;
        IsPortOccupied = isPortOccupied;
        IsUnavailable = true;
        Changed?.Invoke();
    }
}
