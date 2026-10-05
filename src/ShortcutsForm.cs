namespace EchoFlow;

/// Shows a shortcut as key chips ("Ctrl" "Win"). Click it (or the pencil) and press new keys to change it.
sealed class ShortcutBox : Painted
{
    readonly string? prefix;
    readonly bool editable, deletable;
    readonly System.Windows.Forms.Timer errorTimer = new() { Interval = 2500 };
    static readonly Font IconFont = new(Ui.IconFont, 10f);
    string spec, live = "";
    string? error;
    ShortcutRecorder? recorder;
    Rectangle deleteRect;

    /// The new shortcut, or "" when deleted.
    public event Action<string>? Changed;
    /// True while waiting for keys; the app's own shortcuts are paused meanwhile.
    public event Action<bool>? RecordingChanged;

    public ShortcutBox(string spec, string? prefix = null, bool editable = true, bool deletable = false)
    {
        this.spec = spec;
        this.prefix = prefix;
        this.editable = editable;
        this.deletable = deletable;
        Size = new Size(Ui.S(270), Ui.S(46));
        if (editable) Cursor = Cursors.Hand;
        errorTimer.Tick += (_, _) => { errorTimer.Stop(); error = null; Invalidate(); };
    }

    public string Spec { get => spec; set { spec = value; Invalidate(); } }

    public void ShowError(string text)
    {
        error = text;
        errorTimer.Stop();
        errorTimer.Start();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (deletable && spec.Length > 0 && recorder is null && deleteRect.Contains(e.Location))
        {
            spec = "";
            Invalidate();
            Changed?.Invoke("");
            return;
        }
        if (editable && recorder is null) StartRecording();
    }

    void StartRecording()
    {
        error = null;
        recorder = new ShortcutRecorder();
        recorder.Changed += keys => { live = keys; Invalidate(); };
        recorder.Recorded += Finish;
        recorder.Cancelled += Cancel;
        RecordingChanged?.Invoke(true);
        Invalidate();
    }

    void Finish(string keys)
    {
        Cancel();
        var (mods, key) = HoldHotkey.Parse(keys);
        if (key == 0 && System.Numerics.BitOperations.PopCount((uint)mods) < 2)
        {
            ShowError("Use two keys, or a key like F8");
            return;
        }
        spec = keys;
        Invalidate();
        Changed?.Invoke(keys);
    }

    /// Stops waiting for keys, keeping the old shortcut.
    public void Cancel()
    {
        if (recorder is null) return;
        recorder.Dispose();
        recorder = null;
        live = "";
        RecordingChanged?.Invoke(false);
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        var box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Ui.FillRound(g, box, Ui.S(10), Theme.Field);
        using (var path = Ui.Round(box, Ui.S(10)))
        using (var pen = new Pen(recorder is not null ? Theme.Strong : Theme.Divider, recorder is not null ? 1.5f : 1f))
            g.DrawPath(pen, path);

        int x = Ui.S(10);
        var center = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        if (prefix is not null)
        {
            x += Ui.S(4);
            TextRenderer.DrawText(g, prefix, Theme.Small, new Rectangle(x, 0, Width, Height), Theme.Muted, center);
            x += TextRenderer.MeasureText(prefix, Theme.Small).Width + Ui.S(8);
        }

        string keys = recorder is not null ? live : spec;
        string? hint = error
            ?? (recorder is not null && keys.Length == 0 ? "Press your keys…  (Esc to cancel)"
            : keys.Length == 0 ? "Click to add a shortcut" : null);

        if (hint is not null)
        {
            TextRenderer.DrawText(g, hint, Theme.Small, new Rectangle(x + Ui.S(4), 0, Width, Height), error is null ? Theme.Muted : Theme.Warn, center);
        }
        else
        {
            foreach (var part in keys.Split('+'))
            {
                int w = TextRenderer.MeasureText(part, Theme.Body).Width + Ui.S(16);
                var chip = new Rectangle(x, (Height - Ui.S(30)) / 2, w, Ui.S(30));
                Ui.FillRound(g, chip, Ui.S(6), Theme.Chip);
                TextRenderer.DrawText(g, part, Theme.Body, chip, Theme.Text, TextFormatFlags.HorizontalCenter | center);
                x += w + Ui.S(6);
            }
        }

        if (recorder is not null) return;
        int right = Width - Ui.S(14);
        if (editable)
        {
            right -= Ui.S(16);
            TextRenderer.DrawText(g, Ui.EditIcon, IconFont, new Rectangle(right, 0, Ui.S(16), Height), Theme.Muted, center);
        }
        if (deletable && spec.Length > 0)
        {
            right -= Ui.S(26);
            deleteRect = new Rectangle(right - Ui.S(4), 0, Ui.S(24), Height);
            TextRenderer.DrawText(g, Ui.DeleteIcon, IconFont, new Rectangle(right, 0, Ui.S(16), Height), Theme.Muted, center);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cancel();
            errorTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// "Shortcuts" window: push to talk, hands-free, and cancel.
sealed class ShortcutsForm : Form
{
    const string DefaultHotkey = "Ctrl+Win";

    public ShortcutsForm(TrayApp app, Icon icon)
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = "Shortcuts";
        Icon = icon;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.S(660), Ui.S(500));

        var talk = new ShortcutBox(app.Settings.Hotkey);
        var doubleTap = new ShortcutBox(app.Settings.Hotkey, prefix: "Double tap", editable: false);
        var handsFree = new ShortcutBox(app.Settings.HandsFreeHotkey, deletable: true);
        var stack = new Stack(vertical: true, 8, doubleTap, handsFree);

        var page = new Page();
        page.Note("Choose your shortcuts for dictating. Click a shortcut, then press the keys you want.", 16);
        page.Add(new Card()).AddRow("Push to talk", "Hold to say something short", talk);
        var handsFreeCard = page.Add(new Card());
        handsFreeCard.AddRow("Hands-free mode", "Dictate without holding keys. Use either shortcut to start, and again to stop.", stack);
        page.Add(new Card()).AddRow("Cancel", "Stop and throw away what you said", new ShortcutBox("Esc", editable: false));

        talk.Changed += keys =>
        {
            if (keys == app.Settings.HandsFreeHotkey) { talk.Spec = app.Settings.Hotkey; talk.ShowError("That's your hands-free shortcut"); return; }
            app.Settings.Hotkey = keys;
            app.Settings.Save();
            doubleTap.Spec = keys;
            app.ApplyHotkey();
        };
        handsFree.Changed += keys =>
        {
            if (keys.Length > 0 && keys == app.Settings.Hotkey) { handsFree.Spec = app.Settings.HandsFreeHotkey; handsFree.ShowError("That's your push-to-talk shortcut"); return; }
            app.Settings.HandsFreeHotkey = keys;
            app.Settings.Save();
            app.ApplyHotkey();
        };
        foreach (var box in new[] { talk, handsFree })
            box.RecordingChanged += recording => { if (recording) app.SuspendHotkeys(); else app.ApplyHotkey(); };
        Deactivate += (_, _) => { talk.Cancel(); handsFree.Cancel(); };

        // Bottom bar
        var reset = new FlatButton("Reset to default");
        var done = new FlatButton("Done", primary: true);
        reset.Click += (_, _) =>
        {
            app.Settings.Hotkey = talk.Spec = doubleTap.Spec = DefaultHotkey;
            app.Settings.HandsFreeHotkey = handsFree.Spec = "";
            app.Settings.Save();
            app.ApplyHotkey();
        };
        done.Click += (_, _) => Close();
        var bar = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(72) };
        bar.Controls.AddRange([reset, done]);
        bar.Resize += (_, _) =>
        {
            int y = (bar.Height - done.Height) / 2;
            reset.Location = new Point(Ui.S(32), y);
            done.Location = new Point(bar.Width - Ui.S(32) - done.Width, y);
        };

        Controls.Add(page);
        Controls.Add(bar);
        Theme.Apply(this);
    }
}
