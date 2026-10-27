namespace DubboTester.Models;

public sealed class HomeRequestDraft
{
    public InvocationInput Invocation { get; set; } = new();
    public List<MethodSignatureItem> Signatures { get; set; } = [];
    public int SelectedSignatureIndex { get; set; }
    public bool SignatureReadFailed { get; set; }
}
