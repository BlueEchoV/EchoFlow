using NAudio.Dsp;
using NAudio.Wave;

namespace EchoFlow;

/// Records a microphone as 16 kHz mono, the format Whisper expects.
sealed class Recorder : IDisposable
{
    readonly List<float> samples = [];
    WaveIn? wave;

    /// Microphone name from Devices(), or empty for the Windows default.
    public string Microphone { get; set; } = "";

    /// Current loudness from 0 to 1, for the indicator's bars.
    public float Level { get; private set; }

    public bool IsRecording => wave is not null;

    public static List<string> Devices()
    {
        var names = new List<string>();
        for (int i = 0; i < WaveIn.DeviceCount; i++) names.Add(WaveIn.GetCapabilities(i).ProductName);
        return names;
    }

    public void Start()
    {
        lock (samples) samples.Clear();
        Level = 0;
        int device = Microphone.Length > 0 ? Devices().IndexOf(Microphone) : -1; // -1 = Windows default
        wave = new WaveIn { DeviceNumber = device, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 30 };
        wave.DataAvailable += OnData;
        wave.StartRecording();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        if (count == 0) return;
        float sumSquares = 0;
        lock (samples)
        {
            for (int i = 0; i < count; i++)
            {
                float v = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                samples.Add(v);
                sumSquares += v * v;
            }
        }
        float rms = MathF.Sqrt(sumSquares / count);
        Level = Math.Clamp(MathF.Sqrt(rms) * 2.2f - 0.1f, 0f, 1f);
    }

    const int FftSize = 512;
    readonly FftProcessor fft = new(FftSize, FftWindowType.Hann);
    readonly float[] window = new float[FftSize];
    readonly Complex[] spectrum = new Complex[FftSize / 2 + 1];

    /// Loudness of the last ~30 ms in voice frequency bands (low to high), each 0 to 1. Drives the bouncing bars.
    public float[] Bands(int count)
    {
        var result = new float[count];
        lock (samples)
        {
            if (samples.Count < FftSize) return result;
            samples.CopyTo(samples.Count - FftSize, window, 0, FftSize);
        }
        fft.RealForward(window, spectrum);

        // Log-spaced bands from 120 Hz to 5 kHz, where speech lives. 31.25 Hz per bin at 16 kHz.
        for (int b = 0; b < count; b++)
        {
            int from = (int)(120 * Math.Pow(5000 / 120.0, b / (double)count) / 31.25);
            int to = Math.Max(from + 1, (int)(120 * Math.Pow(5000 / 120.0, (b + 1) / (double)count) / 31.25));
            float energy = 0;
            for (int k = from; k < to && k < spectrum.Length; k++)
                energy = MathF.Max(energy, MathF.Sqrt(spectrum[k].X * spectrum[k].X + spectrum[k].Y * spectrum[k].Y));
            float db = 20 * MathF.Log10(energy + 1e-9f);
            result[b] = Math.Clamp((db + 72) / 48, 0, 1); // about -72 dB (quiet) to -24 dB (loud)
        }
        return result;
    }

    public float[] Stop()
    {
        if (wave is null) return [];
        wave.StopRecording();
        wave.Dispose();
        wave = null;
        Level = 0;
        lock (samples) return samples.ToArray();
    }

    public void Dispose() => wave?.Dispose();
}
