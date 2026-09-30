using DubboTester.Models;

namespace DubboTester.Services;

public static class HistoryRetentionPolicy
{
    public const int DefaultLimit = 500;
    public const int MaximumLimit = 2_000;
    public const int MinimumLimit = 1;

    public static int NormalizeLimit(int configuredLimit) =>
        Math.Clamp(configuredLimit, MinimumLimit, MaximumLimit);

    /// <summary>
    /// 历史按新到旧排列。超过上限时优先淘汰最旧的未收藏记录；
    /// 只有收藏记录本身已经占满上限时，才淘汰最旧的收藏记录。
    /// </summary>
    public static bool TrimToLimit(List<InvocationHistory> history, int configuredLimit)
    {
        ArgumentNullException.ThrowIfNull(history);
        int limit = NormalizeLimit(configuredLimit);
        if (history.Count <= limit) return false;

        while (history.Count > limit)
        {
            int index = history.FindLastIndex(item => !item.Favorite);
            if (index < 0) index = history.Count - 1;
            history.RemoveAt(index);
        }

        return true;
    }
}
