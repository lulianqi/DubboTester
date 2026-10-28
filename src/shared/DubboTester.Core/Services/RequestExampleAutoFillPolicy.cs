using DubboTester.Models;

namespace DubboTester.Services;

public static class RequestExampleAutoFillPolicy
{
    public static bool TryFill(
        InvocationInput input,
        MethodSignatureItem? signature,
        bool enabled,
        bool signatureReadFailed)
    {
        if (!enabled || signatureReadFailed || !string.IsNullOrWhiteSpace(input.RequestJson)
            || signature is null || signature.ParameterTypes.Length == 0
            || !string.Equals(signature.Source, "MetadataCenter", StringComparison.Ordinal)
            || !string.Equals(signature.ServiceName, input.ServiceName.Trim(), StringComparison.Ordinal)
            || !string.Equals(signature.MethodName, input.MethodName.Trim(), StringComparison.Ordinal)
            // Compare JVM invocation types on both sides; keep the full generic
            // metadata below when building collection elements and object fields.
            || !JsonArgumentParser.ParseJavaTypes(string.Join(",", signature.ParameterTypes)).SequenceEqual(
                JsonArgumentParser.ParseJavaTypes(input.JavaParameterTypes), StringComparer.Ordinal))
            return false;

        input.RequestJson = DubboTypeStructureBuilder.GenerateExampleJson(
            DubboTypeStructureBuilder.Build(signature));
        return true;
    }
}
