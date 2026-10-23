using DubboTester.Models;

namespace DubboTester.Services;

public sealed class PressureTestDraftStore
{
    private TesterSettings? _settings;
    private InvocationInput? _invocation;

    public void Capture(TesterSettings settings, InvocationInput invocation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(invocation);
        _settings = settings.Clone();
        _invocation = invocation.Clone();
    }

    public bool TryGet(out TesterSettings settings, out InvocationInput invocation)
    {
        settings = _settings?.Clone() ?? new TesterSettings();
        invocation = _invocation?.Clone() ?? new InvocationInput();
        return _settings is not null && _invocation is not null;
    }
}
