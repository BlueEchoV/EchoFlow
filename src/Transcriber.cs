using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace EchoFlow;

/// Turns recorded audio into text with a local Whisper model.
/// Runs on any graphics chip through Vulkan (NVIDIA, AMD, Intel), or on the processor if there is none.
sealed class Transcriber : IDisposable
{
    const int SampleRate = 16000;

    readonly string language;
    WhisperFactory? factory;

    public string ModelName { get; }
    public string Backend => RuntimeOptions.LoadedLibrary?.ToString() ?? "not loaded";

    public Transcriber(Settings settings)
    {
        language = settings.Language;
        ModelName = settings.Model == "auto" ? PickModel() : settings.Model;
        LogProvider.AddLogger((level, message) =>
        {
            if (level <= WhisperLogLevel.Info && !string.IsNullOrWhiteSpace(message)) Log.Write("whisper: " + message.TrimEnd());
        });
    }

    /// With a graphics chip the big, accurate model is fast. On the processor alone, a smaller one keeps it quick.
    static string PickModel() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll")) ? "large-v3-turbo" : "small";

    public async Task LoadAsync(Action<string> status)
    {
        string path = await DownloadModelAsync(status);
        status("Loading speech model…");
        factory = await Task.Run(() => WhisperFactory.FromPath(path));
        await TranscribeAsync(new float[SampleRate], ""); // warm-up, so the first real dictation is fast
        Log.Write($"Model {ModelName} ready on {Backend}");
    }

    async Task<string> DownloadModelAsync(Action<string> status)
    {
        string dir = Path.Combine(Settings.LocalDir, "models");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"ggml-{ModelName}.bin");
        if (File.Exists(file)) return file;

        string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{ModelName}.bin";
        Log.Write($"Downloading {url}");
        status("Downloading speech model…");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        string partial = file + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read, lastPercent = -1;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : -1;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    status($"Downloading speech model… {percent}%");
                }
            }
        }
        File.Move(partial, file, overwrite: true);
        return file;
    }

    public Task<string> TranscribeAsync(float[] audio, string prompt) => Task.Run(async () =>
    {
        if (factory is null) throw new InvalidOperationException("Speech model isn't loaded yet.");

        // Whisper needs at least a second of audio; pad short clips with silence.
        if (audio.Length < SampleRate * 1.1) Array.Resize(ref audio, (int)(SampleRate * 1.1));

        var builder = factory.CreateBuilder();
        builder = language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (prompt.Length > 0) builder = builder.WithPrompt(prompt);

        await using var processor = builder.Build();
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audio)) text.Append(segment.Text);
        return text.ToString();
    });

    public void Dispose() => factory?.Dispose();
}
