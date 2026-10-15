namespace DubboTester.Services;

/// <summary>
/// Keeps newest response blocks first and drops complete older blocks when the
/// configured character budget is exceeded.
/// </summary>
public sealed class ResponseResultHistoryBuffer
{
    public const int DefaultMaxCharacters = 200_000;
    private const string EntrySeparator = "\n\n";
    private const string TruncatedMarker = "\n\n[响应内容过长，已截断]";

    private readonly int _maxCharacters;
    private readonly List<string> _entries = [];

    public ResponseResultHistoryBuffer(int maxCharacters = DefaultMaxCharacters)
    {
        if (maxCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        _maxCharacters = maxCharacters;
    }

    public string Text { get; private set; } = string.Empty;

    public void Prepend(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Insert(0, TruncateSingleEntry(entry));
        while (_entries.Count > 1 && CombinedLength() > _maxCharacters)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }

        Text = string.Join(EntrySeparator, _entries);
    }

    public void Clear()
    {
        _entries.Clear();
        Text = string.Empty;
    }

    private string TruncateSingleEntry(string entry)
    {
        if (entry.Length <= _maxCharacters) return entry;
        if (_maxCharacters <= TruncatedMarker.Length)
        {
            return entry[.._maxCharacters];
        }

        return entry[..(_maxCharacters - TruncatedMarker.Length)] + TruncatedMarker;
    }

    private int CombinedLength() =>
        _entries.Sum(entry => entry.Length) + (EntrySeparator.Length * Math.Max(0, _entries.Count - 1));
}
