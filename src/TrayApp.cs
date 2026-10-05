using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EchoFlow;

/// Runs in the system tray and ties it together: hotkey → record → transcribe → AI cleanup → paste.
/// Hold the hotkey to talk. Double-tap it to go hands-free, then tap it again to finish. Esc cancels.
sealed partial class TrayApp : ApplicationContext
{
    enum State { Idle, Holding, TapPending, HandsFree }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int TapMs = 300, DoubleTapWindowMs = 400;
    static readonly TimeSpan HandsFreeLimit = TimeSpan.FromMinutes(10);

    internal readonly Settings Settings = Settings.Load();
    internal readonly History History = new();
    internal readonly WordList Words;
    internal readonly Cleaner Cleaner;
    internal readonly Transcriber Transcriber;
    readonly Recorder recorder = new();
    readonly Indicator indicator = new();
    readonly Icon icon = CreateIcon();
    readonly NotifyIcon tray;
    readonly SemaphoreSlim oneAtATime = new(1, 1);
    readonly System.Windows.Forms.Timer tapTimer = new() { Interval = DoubleTapWindowMs };
    readonly System.Windows.Forms.Timer handsFreeTimer = new() { Interval = (int)HandsFreeLimit.TotalMilliseconds };
    HoldHotkey? hotkey, handsFreeHotkey;
    MainForm? mainForm;
    CorrectionWatcher? watcher;
    Task quieting = Task.CompletedTask;
    bool quieted;

    State state = State.Idle;
    bool ready, stopping, ignoreNextRelease, stopOnRelease;
    long pressedAt;
    string targetApp = "";

    /// Set when the last AI request failed (no credits, rate limit, bad key, no internet). Cleared by the next success.
    string? aiError;

    public TrayApp(bool showWindow)
    {
        Log.Write("Starting");
        Theme.Set(Settings.DarkMode);
        Words = new WordList(Settings.WordListPath);
        Transcriber = new Transcriber(Settings);
        Cleaner = new Cleaner(Settings);
        recorder.Microphone = Settings.Microphone;

        tray = new NotifyIcon { Icon = icon, Visible = true, ContextMenuStrip = BuildMenu(), Text = "EchoFlow — starting…" };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };

        tapTimer.Tick += (_, _) => { tapTimer.Stop(); if (state == State.TapPending) Discard(); };
        handsFreeTimer.Tick += (_, _) => { handsFreeTimer.Stop(); if (state == State.HandsFree) _ = FinishAsync(); };

        ApplyHotkey();
        ApplyBar();
        UpdateAiStatus();
        if (StartsWithWindows()) SetStartWithWindows(true); // keeps the startup entry current
        _ = LoadModelAsync();
        _ = CheckKeyAsync();
        if (showWindow) ShowMain();
    }

    // ---- Status, shown in the tray tooltip and the main window ----

    internal string Status { get; private set; } = "Starting…";

    internal string AiStatus =>
        !Settings.Cleanup ? "AI cleanup is off"
        : !Cleaner.HasKey ? "AI cleanup needs an API key"
        : aiError is not null ? $"AI cleanup isn't working: {aiError}"
        : "AI cleanup is on";

    internal string? AiError => aiError;

    internal bool AiActive => Settings.Cleanup && Cleaner.HasKey && aiError is null;

    void SetStatus(string text)
    {
        Status = text;
        var tip = "EchoFlow — " + text;
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
        mainForm?.RefreshStatus();
    }

    internal void UpdateAiStatus()
    {
        indicator.AiActive = AiActive;
        mainForm?.RefreshStatus();
    }

    async Task LoadModelAsync()
    {
        try
        {
            await Transcriber.LoadAsync(status =>
            {
                SetStatus(status);
                if (state == State.Idle && status.StartsWith("Downloading")) indicator.ShowMessage(status);
            });
            ready = true;
            SetStatus($"Ready — {Transcriber.ModelName} on {Transcriber.Backend}");
            if (state == State.Idle) indicator.ShowMessage($"Ready — hold {HoldHotkey.Display(Settings.Hotkey)} to talk", 2500);
        }
        catch (Exception ex)
        {
            Log.Write($"Model failed to load: {ex}");
            SetStatus("Speech model failed to load — see log");
            indicator.ShowMessage("Speech model failed to load", 4000);
        }
    }

    /// A one-token test at startup, so a bad key or empty balance shows right away rather than at the first dictation.
    async Task CheckKeyAsync()
    {
        if (!Settings.Cleanup || !Cleaner.HasKey) return;
        aiError = await Cleaner.CheckKeyAsync();
        UpdateAiStatus();
    }

    // ---- Hotkey ----

    internal void ApplyHotkey()
    {
        SuspendHotkeys();
        try
        {
            hotkey = new HoldHotkey(Settings.Hotkey);
            hotkey.Pressed += OnPressed;
            hotkey.Released += OnReleased;
            hotkey.Cancelled += OnCancelled;
            hotkey.EscapePressed += OnEscape;

            // Installed second so it sees keys first, and can swallow its key before push-to-talk sees it.
            if (Settings.HandsFreeHotkey.Length > 0)
            {
                handsFreeHotkey = new HoldHotkey(Settings.HandsFreeHotkey);
                handsFreeHotkey.Pressed += OnHandsFreePressed;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Hotkey failed: {ex}");
            tray.ShowBalloonTip(5000, "EchoFlow", $"Shortcut problem: {ex.Message}", ToolTipIcon.Error);
        }
    }

    /// Stops listening for shortcuts, e.g. while you're choosing new ones.
    internal void SuspendHotkeys()
    {
        hotkey?.Dispose();
        handsFreeHotkey?.Dispose();
        hotkey = handsFreeHotkey = null;
    }

    internal void ApplyBar() => indicator.AlwaysVisible = Settings.ShowBarAlways;

    internal void ApplyMicrophone() => recorder.Microphone = Settings.Microphone;

    void OnPressed()
    {
        switch (state)
        {
            case State.Idle:
                if (stopping) return;
                if (!ready) { indicator.ShowMessage("Still loading the speech model…", 1500); return; }
                StartRecording();
                break;

            case State.TapPending:
                // Second tap: keep the recording going, hands-free.
                ignoreNextRelease = true;
                GoHandsFree();
                break;

            case State.HandsFree:
                stopOnRelease = true;
                break;
        }
    }

    void OnReleased()
    {
        if (ignoreNextRelease) { ignoreNextRelease = false; return; }

        switch (state)
        {
            case State.Holding when Environment.TickCount64 - pressedAt < TapMs:
                // A quick tap: wait to see whether a second tap makes it hands-free.
                state = State.TapPending;
                tapTimer.Start();
                break;

            case State.Holding:
                _ = FinishAsync();
                break;

            case State.HandsFree when stopOnRelease:
                _ = FinishAsync();
                break;
        }
    }

    void OnHandsFreePressed()
    {
        switch (state)
        {
            case State.Idle:
                if (stopping) return;
                if (!ready) { indicator.ShowMessage("Still loading the speech model…", 1500); return; }
                if (StartRecording()) GoHandsFree();
                break;

            case State.Holding or State.TapPending:
                ignoreNextRelease = state == State.Holding; // letting go of push-to-talk shouldn't stop it now
                GoHandsFree();
                break;

            case State.HandsFree:
                _ = FinishAsync();
                break;
        }
    }

    void GoHandsFree()
    {
        tapTimer.Stop();
        state = State.HandsFree;
        indicator.SetHandsFree();
        handsFreeTimer.Start();
    }

    void OnCancelled()
    {
        // The hotkey was part of another shortcut (e.g. Ctrl+Win+→).
        if (state == State.Holding) Discard();
        else if (state == State.HandsFree) stopOnRelease = ignoreNextRelease = false;
    }

    void OnEscape()
    {
        if (state == State.Idle) return;
        Discard();
        indicator.ShowMessage("Cancelled", 1000);
    }

    bool StartRecording()
    {
        try
        {
            recorder.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"Microphone failed: {ex}");
            indicator.ShowMessage("Couldn't open the microphone", 2500);
            return false;
        }
        state = State.Holding;
        pressedAt = Environment.TickCount64;
        ignoreNextRelease = stopOnRelease = false;
        targetApp = Native.ActiveApp();
        indicator.ShowListening(recorder.Bands, () => recorder.Level);
        if (Settings.Sounds) Sounds.PlayStart();
        if (Settings.QuietWhileDictating) quieting = QuietOthersAsync();
        return true;
    }

    async Task QuietOthersAsync()
    {
        if (Settings.Sounds) await Task.Delay(180); // let the chime finish first
        if (state == State.Idle || quieted) return;
        quieted = true;
        await Sounds.QuietOthersAsync();
    }

    /// Lets music and videos play again as soon as you stop talking.
    async Task RestoreOthersAsync()
    {
        await quieting;
        if (!quieted) return;
        quieted = false;
        await Sounds.RestoreOthersAsync();
    }

    void Discard()
    {
        tapTimer.Stop();
        handsFreeTimer.Stop();
        state = State.Idle;
        recorder.Stop();
        _ = RestoreOthersAsync();
        indicator.Dismiss();
    }

    async Task FinishAsync()
    {
        tapTimer.Stop();
        handsFreeTimer.Stop();
        state = State.Idle;

        // Keep listening a moment longer so the last word isn't clipped.
        stopping = true;
        await Task.Delay(120);
        var audio = recorder.Stop();
        stopping = false;
        await RestoreOthersAsync();
        if (Settings.Sounds) Sounds.PlayStop();

        if (audio.Length == 0 || audio.Max(MathF.Abs) < 0.015f)
        {
            indicator.ShowMessage("Didn't hear anything — check your mic", 1800);
            return;
        }

        indicator.ShowWorking();
        await oneAtATime.WaitAsync();
        try
        {
            var watch = Stopwatch.StartNew();
            string raw = Words.Apply(Tidy(await Transcriber.TranscribeAsync(audio, Words.Prompt())));
            Log.Write($"Transcribed {audio.Length / 16000.0:0.0}s of audio in {watch.ElapsedMilliseconds} ms");

            if (raw.Length == 0)
            {
                if (state == State.Idle) indicator.ShowMessage("Didn't catch that", 1500);
                return;
            }

            string text = raw;
            long inputTokens = 0, outputTokens = 0;
            if (Settings.Cleanup && Cleaner.HasKey)
            {
                try
                {
                    var result = await Cleaner.CleanAsync(raw, targetApp, Words.Prompt());
                    text = Words.Apply(result.Text);
                    (inputTokens, outputTokens) = (result.InputTokens, result.OutputTokens);
                    aiError = null;
                }
                catch (Exception ex)
                {
                    // Never lose a dictation: paste what Whisper heard. The sparkle on the pill goes grey.
                    Log.Write($"AI cleanup failed, pasting raw text: {ex.Message}");
                    aiError = Cleaner.Friendly(ex);
                }
                UpdateAiStatus();
            }

            History.Add(text, raw, inputTokens, outputTokens);
            mainForm?.RefreshStatus();

            // Paste wherever you are now, even if you switched apps while talking.
            bool? canPaste = await FocusCheck.HasTextFieldAsync();
            if (state == State.Idle)
            {
                if (canPaste == false) indicator.ShowCopy(text, 8000);
                else if (!indicator.ShowingMessage) indicator.Dismiss();
            }
            // A trailing space lets the next dictation flow on; not after multi-line text like lists.
            await Paster.PasteAsync(text.Contains('\n') ? text : text + " ");

            // Watch for a quick fix of a misheard word, to learn it.
            watcher?.Dispose();
            watcher = null;
            if (Settings.AutoAddWords && canPaste != false)
            {
                watcher = CorrectionWatcher.Start(text);
                watcher.Corrected += OnCorrected;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Dictation failed: {ex}");
            if (state == State.Idle) indicator.ShowMessage("Something went wrong — see log", 2500);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    void OnCorrected(string heard, string wanted)
    {
        // Same word with different capitals: just remember the spelling. Otherwise remember the swap.
        string line = heard.Equals(wanted, StringComparison.OrdinalIgnoreCase) ? wanted : $"{heard} -> {wanted}";
        if (!Words.Add(line)) return;
        Log.Write($"Added to dictionary: {line}");
        if (state == State.Idle) indicator.ShowMessage($"Added “{wanted}” to your dictionary", 2500);
    }

    /// Removes Whisper's sound tags like [BLANK_AUDIO], *click*, (laughs) or ♪, and extra spaces.
    static string Tidy(string text)
    {
        text = SoundTag().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\[[^\]]*\]|\*[^*\n]{1,40}\*|♪+|^\s*\([^)]*\)\s*$" +
        @"|\([^)]*\b(click|laugh|cough|music|noise|sigh|applause|silence|breath|inaudible|typing|keyboard|beep|sound)\w*[^)]*\)",
        RegexOptions.IgnoreCase)] private static partial Regex SoundTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    // ---- Tray menu and main window ----

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var open = menu.Items.Add("Open EchoFlow", null, (_, _) => ShowMain());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add("Copy last dictation", null, (_, _) => { if (History.Last() is { } t) Clipboard.SetText(t); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { Shutdown(); ExitThread(); });
        return menu;
    }

    void ShowMain()
    {
        if (mainForm is null || mainForm.IsDisposed) mainForm = new MainForm(this, icon);
        mainForm.Show();
        mainForm.WindowState = FormWindowState.Normal;
        mainForm.Activate();
    }

    /// Saves the key from whatever was pasted (even a whole curl command), then checks it works.
    internal async Task<string> SaveKeyAsync(string pasted)
    {
        if (pasted.Trim().Length == 0) return "Paste your key first";
        var match = ApiKey().Match(pasted);
        if (!match.Success) return "That isn't an API key. Keys start with sk-ant-";
        Cleaner.SaveKey(match.Value);
        aiError = await Cleaner.CheckKeyAsync();
        UpdateAiStatus();
        return aiError is null ? "Saved and working" : $"Saved, but {aiError}";
    }

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]+")] private static partial Regex ApiKey();

    internal static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("EchoFlow") is string || key?.GetValue("Dictation") is string;
    }

    internal static void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue("Dictation", throwOnMissingValue: false); // the app's old name
        if (on) key.SetValue("EchoFlow", $"\"{Application.ExecutablePath}\" --background");
        else key.DeleteValue("EchoFlow", throwOnMissingValue: false);
    }

    void Shutdown()
    {
        SuspendHotkeys();
        watcher?.Dispose();
        _ = Sounds.RestoreOthersAsync();
        recorder.Dispose();
        tapTimer.Dispose();
        handsFreeTimer.Dispose();
        tray.Visible = false;
        tray.Dispose();
        indicator.Dispose();
        mainForm?.Dispose();
        Transcriber.Dispose();
    }

    /// Tray icon: a white microphone on a dark circle.
    static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var dark = new SolidBrush(Color.FromArgb(28, 28, 32));
            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2.2f);
            g.FillEllipse(dark, 0, 0, 31, 31);
            using (var capsule = new GraphicsPath())
            {
                capsule.AddArc(12, 6, 8, 8, 180, 180);
                capsule.AddArc(12, 12, 8, 8, 0, 180);
                capsule.CloseFigure();
                g.FillPath(white, capsule);
            }
            g.DrawArc(pen, 9, 9, 14, 13, 0, 180);
            g.DrawLine(pen, 16, 22, 16, 26);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
