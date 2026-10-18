using DubboTester.Services;
using Xunit;

namespace DubboTester.Tests;

public sealed class ResponseResultHistoryBufferTests
{
    [Fact]
    public void Prepend_KeepsNewestEntryFirst()
    {
        ResponseResultHistoryBuffer buffer = new(100);

        buffer.Prepend("older");
        buffer.Prepend("newer");

        Assert.Equal("newer\n\nolder", buffer.Text);
    }

    [Fact]
    public void Prepend_DropsCompleteOldEntriesWhenLimitIsExceeded()
    {
        ResponseResultHistoryBuffer buffer = new(12);

        buffer.Prepend("oldest");
        buffer.Prepend("middle");
        buffer.Prepend("newest");

        Assert.Equal("newest", buffer.Text);
    }

    [Fact]
    public void Clear_RemovesAllRetainedEntries()
    {
        ResponseResultHistoryBuffer buffer = new(100);
        buffer.Prepend("response");

        buffer.Clear();

        Assert.Empty(buffer.Text);
    }
}
