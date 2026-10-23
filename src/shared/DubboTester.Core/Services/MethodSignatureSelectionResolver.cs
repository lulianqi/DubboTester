using DubboTester.Models;

namespace DubboTester.Services;

public static class MethodSignatureSelectionResolver
{
    public static int FindBestMatchIndex(
        IReadOnlyList<MethodSignatureItem> signatures,
        IReadOnlyList<string> preferredParameterTypes,
        string? preferredVersion,
        string? preferredGroup)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(preferredParameterTypes);
        if (signatures.Count == 0) return -1;

        string[] normalizedPreferredTypes = NormalizeTypes(preferredParameterTypes);
        int firstParameterMatch = -1;
        for (int index = 0; index < signatures.Count; index++)
        {
            MethodSignatureItem candidate = signatures[index];
            if (!NormalizeTypes(candidate.ParameterTypes)
                    .SequenceEqual(normalizedPreferredTypes, StringComparer.Ordinal))
            {
                continue;
            }

            if (firstParameterMatch < 0) firstParameterMatch = index;
            if (OptionalValueMatches(candidate.Version, preferredVersion)
                && OptionalValueMatches(candidate.Group, preferredGroup))
            {
                return index;
            }
        }

        return firstParameterMatch >= 0 ? firstParameterMatch : 0;
    }

    private static string[] NormalizeTypes(IEnumerable<string> parameterTypes) => parameterTypes
        .Select(type => type.Trim())
        .ToArray();

    private static bool OptionalValueMatches(string? candidate, string? preferred) =>
        string.IsNullOrWhiteSpace(preferred)
        || string.Equals(candidate?.Trim(), preferred.Trim(), StringComparison.Ordinal);
}
