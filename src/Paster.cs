using System.Collections.Specialized;

namespace EchoFlow;

/// Types text into whatever app has focus: puts it on the clipboard, presses Ctrl+V,
/// then puts back whatever was on your clipboard before.
static class Paster
{
    const string SkipClipboardHistory = "ExcludeClipboardContentFromMonitorProcessing";

    record Saved(string? Text, string? Rtf, string? Html, Image? Image, StringCollection? Files);

    public static async Task PasteAsync(string text)
    {
        // Wait until the hotkey is fully let go, or Ctrl+V would arrive as Ctrl+Alt+V.
        for (int i = 0; i < 100 && Native.AnyModifierDown(); i++) await Task.Delay(20);

        var saved = Save();
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData(SkipClipboardHistory, new MemoryStream(new byte[4])); // keep Win+V history clean
        Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 50);

        Native.SendCtrlV();
        await Task.Delay(300); // let the app read the clipboard before we restore it

        if (saved is not null) Restore(saved);
    }

    static Saved? Save()
    {
        try
        {
            var saved = new Saved(
                Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null,
                Clipboard.ContainsText(TextDataFormat.Rtf) ? Clipboard.GetText(TextDataFormat.Rtf) : null,
                Clipboard.ContainsText(TextDataFormat.Html) ? Clipboard.GetText(TextDataFormat.Html) : null,
                Clipboard.ContainsImage() ? Clipboard.GetImage() : null,
                Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList() : null);
            return saved is { Text: null, Image: null, Files: null } ? null : saved;
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't read clipboard: {ex.Message}");
            return null;
        }
    }

    static void Restore(Saved saved)
    {
        var data = new DataObject();
        if (saved.Text is not null) data.SetText(saved.Text, TextDataFormat.UnicodeText);
        if (saved.Rtf is not null) data.SetText(saved.Rtf, TextDataFormat.Rtf);
        if (saved.Html is not null) data.SetText(saved.Html, TextDataFormat.Html);
        if (saved.Image is not null) data.SetImage(saved.Image);
        if (saved.Files is not null) data.SetFileDropList(saved.Files);
        data.SetData(SkipClipboardHistory, new MemoryStream(new byte[4]));
        try { Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 50); }
        catch (Exception ex) { Log.Write($"Couldn't restore clipboard: {ex.Message}"); }
    }
}
