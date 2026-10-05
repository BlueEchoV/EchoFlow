using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace EchoFlow;

/// After a paste, watches that text box for a minute. If you fix a word inside what was just dictated
/// (e.g. "Clod" → "Claude"), reports the fix so it can be added to your words.
/// Only small, similar-looking fixes count; rewording a sentence is ignored. Nothing leaves your PC.
sealed partial class CorrectionWatcher : IDisposable
{
    static readonly TimeSpan WatchFor = TimeSpan.FromSeconds(60);
    static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(800);

    readonly CancellationTokenSource stop = new();

    /// Called on the UI thread with a fix: (heard, wanted).
    public event Action<string, string>? Corrected;

    public static CorrectionWatcher Start(string pasted)
    {
        var watcher = new CorrectionWatcher();
        var ui = SynchronizationContext.Current;
        _ = Task.Run(() => watcher.Watch(pasted.Trim(), (heard, wanted) =>
        {
            if (ui is null) watcher.Corrected?.Invoke(heard, wanted);
            else ui.Post(_ => watcher.Corrected?.Invoke(heard, wanted), null);
        }));
        return watcher;
    }

    async Task Watch(string pasted, Action<string, string> report)
    {
        try
        {
            await Task.Delay(400, stop.Token); // let the paste land
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.IsPassword) return;

            string? settled = ReadText(element);
            if (settled is null) return;
            int start = settled.LastIndexOf(pasted, StringComparison.Ordinal);
            if (start < 0) return;
            int end = start + pasted.Length; // where the dictated text sits in the box

            string last = settled;
            var until = DateTime.UtcNow + WatchFor;
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(PollEvery, stop.Token);
                string? now = ReadText(element);
                if (now is null) return; // box closed or unreadable
                if (now != last) { last = now; continue; } // still typing; wait until it holds still
                if (now == settled) continue;

                // Find the changed span: everything between the common start and the common end.
                int prefix = 0;
                while (prefix < settled.Length && prefix < now.Length && settled[prefix] == now[prefix]) prefix++;
                int suffix = 0;
                while (suffix < settled.Length - prefix && suffix < now.Length - prefix
                       && settled[^(suffix + 1)] == now[^(suffix + 1)]) suffix++;

                int oldFrom = WordStart(settled, prefix), oldTo = WordEnd(settled, settled.Length - suffix);
                int newFrom = WordStart(now, prefix), newTo = WordEnd(now, now.Length - suffix);
                string heard = settled[oldFrom..oldTo].Trim(), wanted = now[newFrom..newTo].Trim();

                if (oldFrom >= start && oldTo <= end && LooksLikeAFix(heard, wanted)) report(heard, wanted);

                // Keep tracking where the dictated text is as the box changes.
                int shift = now.Length - settled.Length;
                if (prefix < start) start += shift;
                if (prefix < end) end += shift;
                settled = now;
            }
        }
        catch (OperationCanceledException) { }
        catch (ElementNotAvailableException) { }
        catch (Exception ex)
        {
            Log.Write($"Correction watcher stopped: {ex.Message}");
        }
    }

    static string? ReadText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                return ((ValuePattern)value).Current.Value.ReplaceLineEndings("\n");
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
                return ((TextPattern)text).DocumentRange.GetText(100_000).ReplaceLineEndings("\n");
        }
        catch (ElementNotAvailableException) { }
        return null;
    }

    static int WordStart(string s, int i)
    {
        while (i > 0 && char.IsLetterOrDigit(s[i - 1])) i--;
        return i;
    }

    static int WordEnd(string s, int i)
    {
        while (i < s.Length && char.IsLetterOrDigit(s[i])) i++;
        return i;
    }

    /// One to three words swapped for something spelled similarly: a fix, not a rewrite.
    static bool LooksLikeAFix(string heard, string wanted)
    {
        if (heard.Length == 0 || wanted.Length == 0 || heard == wanted) return false;
        if (WordCount(heard) > 3 || WordCount(wanted) > 3) return false;
        string a = Letters().Replace(heard.ToLowerInvariant(), ""), b = Letters().Replace(wanted.ToLowerInvariant(), "");
        if (a.Length == 0 || b.Length < 2) return false;
        if (a != b && (b.StartsWith(a) || a.StartsWith(b))) return false; // still typing, or just "word" → "words"
        return Distance(a, b) <= Math.Max(1, Math.Max(a.Length, b.Length) / 2);
    }

    static int WordCount(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]")] private static partial Regex Letters();

    public void Dispose() => stop.Cancel();
}
