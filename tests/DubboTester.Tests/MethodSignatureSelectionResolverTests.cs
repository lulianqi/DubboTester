using DubboTester.Models;
using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class MethodSignatureSelectionResolverTests
{
    [Fact]
    public void FindBestMatchIndex_PreservesSelectedOverloadAfterRefresh()
    {
        MethodSignatureItem[] signatures =
        [
            Signature("java.lang.String"),
            Signature("int"),
            Signature("long")
        ];

        int index = MethodSignatureSelectionResolver.FindBestMatchIndex(
            signatures,
            ["int"],
            preferredVersion: null,
            preferredGroup: null);

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindBestMatchIndex_UsesVersionAndGroupForOtherwiseIdenticalSignatures()
    {
        MethodSignatureItem[] signatures =
        [
            Signature("int", version: "1.0", group: "blue"),
            Signature("int", version: "2.0", group: "green")
        ];

        int index = MethodSignatureSelectionResolver.FindBestMatchIndex(
            signatures,
            ["int"],
            preferredVersion: "2.0",
            preferredGroup: "green");

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindBestMatchIndex_CanRestoreZeroParameterHistorySelection()
    {
        MethodSignatureItem[] signatures =
        [
            Signature("java.lang.String"),
            Signature()
        ];

        int index = MethodSignatureSelectionResolver.FindBestMatchIndex(
            signatures,
            [],
            preferredVersion: null,
            preferredGroup: null);

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindBestMatchIndex_FallsBackToFirstSignatureWhenPreviousOneDisappeared()
    {
        MethodSignatureItem[] signatures =
        [
            Signature("java.lang.String"),
            Signature("long")
        ];

        int index = MethodSignatureSelectionResolver.FindBestMatchIndex(
            signatures,
            ["int"],
            preferredVersion: null,
            preferredGroup: null);

        Assert.Equal(0, index);
    }

    private static MethodSignatureItem Signature(
        string? parameterType = null,
        string? version = null,
        string? group = null) => new()
    {
        ServiceName = "com.foo.OverloadService",
        MethodName = "overload",
        ParameterTypes = parameterType is null ? [] : [parameterType],
        Version = version,
        Group = group
    };
}
