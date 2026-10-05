using System.Text.RegularExpressions;

namespace EchoFlow;

/// Your custom words, from words.txt. Reloaded automatically whenever the file changes.
///   Anthropic             always spell it exactly like this
///   chat gpt -> ChatGPT   when Whisper writes the left side, replace it with the right
sealed class WordList
{
    const string Template = """
        # Your custom words, one per line. Saved changes apply on the next dictation.
        #
        # A word on its own is always spelled exactly as written here:
        #   Anthropic
        #
        # "heard -> wanted" replaces something that keeps coming out wrong:
        #   chat gpt -> ChatGPT

        """;

    readonly string path;
    DateTime loadedAt;
    List<string> words = [];
    List<(Regex Heard, string Wanted)> replacements = [];

    public WordList(string path)
    {
        this.path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, Template);
    }

    public string FilePath => path;

    void Refresh()
    {
        var modified = File.GetLastWriteTimeUtc(path);
        if (modified == loadedAt) return;
        loadedAt = modified;
        words = [];
        replacements = [];
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) { words.Add(line); continue; }
            string heard = line[..arrow].Trim(), wanted = line[(arrow + 2)..].Trim();
            if (heard.Length > 0) replacements.Add((WholeWord(heard), wanted));
            if (wanted.Length > 0) words.Add(wanted);
        }
    }

    static Regex WholeWord(string phrase) =>
        new($@"(?<!\w){Regex.Escape(phrase)}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// Hint passed to Whisper so it leans toward your spellings. Whisper only reads ~200 words of it.
    public string Prompt()
    {
        Refresh();
        var prompt = string.Join(", ", words.Distinct());
        return prompt.Length > 800 ? prompt[..800] : prompt;
    }

    /// Fixes capitalization of your words and applies your replacements.
    public string Apply(string text)
    {
        Refresh();
        foreach (var (heard, wanted) in replacements) text = heard.Replace(text, wanted);
        foreach (var word in words) text = WholeWord(word).Replace(text, word);
        return text;
    }

    /// Adds a line ("Word" or "heard -> wanted") unless it's already there. Returns false if it was.
    public bool Add(string line)
    {
        var existing = File.ReadAllLines(path).Select(l => l.Trim());
        if (existing.Contains(line, StringComparer.OrdinalIgnoreCase)) return false;
        var text = File.ReadAllText(path);
        File.AppendAllText(path, (text.Length == 0 || text.EndsWith('\n') ? "" : Environment.NewLine) + line + Environment.NewLine);
        return true;
    }
}
