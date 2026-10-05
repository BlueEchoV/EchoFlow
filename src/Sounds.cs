using System.Media;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace EchoFlow;

/// Start/stop chimes, and quieting other audio while you dictate.
static class Sounds
{
    static readonly SoundPlayer Start = Chime([660, 990]);
    static readonly SoundPlayer Stop = Chime([880, 587]);
    static bool mutedByUs;

    public static void PlayStart() => Play(Start);
    public static void PlayStop() => Play(Stop);

    static void Play(SoundPlayer player)
    {
        try { player.Play(); }
        catch (Exception ex) { Log.Write($"Sound failed: {ex.Message}"); }
    }

    static readonly List<GlobalSystemMediaTransportControlsSession> pausedByUs = [];

    /// Pauses whatever is playing (Spotify, YouTube…) and mutes anything that can't be paused (games, calls).
    public static async Task QuietOthersAsync()
    {
        MuteSpeakers(true);
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (var session in manager.GetSessions())
            {
                if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                if (await session.TryPauseAsync()) pausedByUs.Add(session);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't pause media: {ex.Message}");
        }
    }

    /// Undoes QuietOthersAsync: unmutes, and resumes only what we paused.
    public static async Task RestoreOthersAsync()
    {
        MuteSpeakers(false);
        var sessions = pausedByUs.ToList();
        pausedByUs.Clear();
        foreach (var session in sessions)
        {
            try { await session.TryPlayAsync(); }
            catch (Exception ex) { Log.Write($"Couldn't resume media: {ex.Message}"); }
        }
    }

    /// Mutes the default speakers. Only unmutes later if it was us who muted them.
    static void MuteSpeakers(bool mute)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var speakers = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = speakers.AudioEndpointVolume;
            if (mute && !volume.Mute) { volume.Mute = true; mutedByUs = true; }
            else if (!mute && mutedByUs) { volume.Mute = false; mutedByUs = false; }
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't change speaker mute: {ex.Message}");
        }
    }

    /// A short, soft two-note chime, made in memory as a WAV.
    static SoundPlayer Chime(double[] notes)
    {
        const int rate = 44100, noteMs = 70;
        int perNote = rate * noteMs / 1000;
        var samples = new short[perNote * notes.Length];
        for (int n = 0; n < notes.Length; n++)
        {
            for (int i = 0; i < perNote; i++)
            {
                double t = (double)i / rate;
                double envelope = Math.Min(1, i / (rate * 0.004)) * Math.Exp(-t * 28); // quick attack, soft decay
                samples[n * perNote + i] = (short)(Math.Sin(2 * Math.PI * notes[n] * t) * envelope * 0.18 * short.MaxValue);
            }
        }

        var wav = new MemoryStream();
        using (var w = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int bytes = samples.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (var s in samples) w.Write(s);
        }
        wav.Position = 0;
        var player = new SoundPlayer(wav);
        player.Load();
        return player;
    }
}
