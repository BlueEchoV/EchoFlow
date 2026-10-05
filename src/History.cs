using System.Text.Json;

namespace EchoFlow;

/// Every dictation, saved to %APPDATA%\EchoFlow\history.jsonl so nothing is lost if it didn't paste where you wanted.
sealed class History
{
    /// Text is what was pasted; Raw is what Whisper heard before AI cleanup.
    public record Entry(DateTime Time, string Text, string? Raw = null, long InputTokens = 0, long OutputTokens = 0);

    public record Summary(int Dictations, int Words, long InputTokens, long OutputTokens)
    {
        /// Claude Haiku 4.5 pricing: $1 per million tokens in, $5 per million out.
        public decimal EstimatedCost => InputTokens / 1_000_000m * 1m + OutputTokens / 1_000_000m * 5m;
    }

    const int Keep = 5000;
    readonly string path = Path.Combine(Settings.AppDataDir, "history.jsonl");

    public History()
    {
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path);
        if (lines.Length > Keep * 1.2) File.WriteAllLines(path, lines[^Keep..]);
    }

    public void Add(string text, string raw, long inputTokens, long outputTokens) =>
        File.AppendAllText(path, JsonSerializer.Serialize(
            new Entry(DateTime.Now, text, raw == text ? null : raw, inputTokens, outputTokens)) + "\n");

    IEnumerable<Entry> All()
    {
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
        {
            Entry? entry = null;
            try { entry = JsonSerializer.Deserialize<Entry>(line); }
            catch (JsonException) { }
            if (entry is not null) yield return entry;
        }
    }

    /// Newest first.
    public List<Entry> Recent(int max = 500) => All().Reverse().Take(max).ToList();

    public string? Last() => Recent(1).FirstOrDefault()?.Text;

    public Summary ThisMonth()
    {
        var start = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        int count = 0, wordCount = 0;
        long input = 0, output = 0;
        foreach (var e in All().Where(e => e.Time >= start))
        {
            count++;
            wordCount += e.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            input += e.InputTokens;
            output += e.OutputTokens;
        }
        return new(count, wordCount, input, output);
    }
}
