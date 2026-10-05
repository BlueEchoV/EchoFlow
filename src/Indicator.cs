using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace EchoFlow;

/// The small bar at the bottom of the screen: a voice visualizer while you talk (mirrored bars with a
/// glow and fading echoes), a rolling wave while it works, short messages, and a Copy button when
/// there was nowhere to paste. Colorful while AI cleanup is working, plain white when it isn't.
/// Never takes focus from your app.
sealed class Indicator : Form
{
    enum Mode { Hidden, Idle, Listening, Working, Message, Copy }

    const float BottomGap96 = 12, Shadow96 = 6;
    // Bars mirror around the middle: lowest pitch in the middle, higher pitches toward both edges.
    const int BarCount = 13, Half = BarCount / 2;
    static readonly Color[] Palette = [Color.FromArgb(94, 234, 212), Color.FromArgb(129, 140, 248), Color.FromArgb(244, 114, 182)];
    static readonly Color Plain = Color.FromArgb(235, 236, 240);

    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly float[] bars = new float[BarCount], echoes = new float[BarCount];
    float loudness;
    Mode mode = Mode.Hidden, drawn = Mode.Hidden;
    Func<int, float[]> bands = n => new float[n];
    Func<float> level = () => 0;
    string message = "";
    long messageUntil, listeningSince, lastTick;
    bool handsFree, alwaysVisible;

    string copyText = "";
    long copyUntil, copyDuration;
    bool copied, hoverButton, hoverPill;
    RectangleF buttonRect;

    float opacity, width96, height96, scale = 1;
    Rectangle area;
    bool shown, clickThrough = true;

    public Indicator()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        timer.Tick += (_, _) => Tick();
        _ = Handle;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // layered | click-through | no taskbar button | never activates | always on top
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; } // clicking Copy keeps focus in your app
        base.WndProc(ref m);
    }

    // ---- What to show ----

    /// Whether AI cleanup is working: colorful bars when true, plain white when not.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AiActive { get; set; }

    /// Keep a tiny bar on screen when idle.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AlwaysVisible
    {
        get => alwaysVisible;
        set
        {
            alwaysVisible = value;
            if (value && mode == Mode.Hidden) SwitchTo(Mode.Idle);
            else if (!value && mode == Mode.Idle) mode = Mode.Hidden;
        }
    }

    public void ShowListening(Func<int, float[]> bands, Func<float> level)
    {
        this.bands = bands;
        this.level = level;
        handsFree = false;
        listeningSince = clock.ElapsedMilliseconds;
        Array.Clear(bars);
        Array.Clear(echoes);
        loudness = 0;
        SwitchTo(Mode.Listening);
    }

    /// Switches to hands-free: a little wider, with a timer.
    public void SetHandsFree() => handsFree = true;

    public void ShowWorking() => SwitchTo(Mode.Working);

    /// Shows text. With a duration it fades away by itself; without one it stays until Dismiss.
    public void ShowMessage(string text, int ms = 0)
    {
        message = text;
        messageUntil = ms > 0 ? clock.ElapsedMilliseconds + ms : long.MaxValue;
        SwitchTo(Mode.Message);
    }

    /// Shows your words with a Copy button and a countdown. Hovering pauses the countdown.
    public void ShowCopy(string text, int ms = 8000)
    {
        copyText = text;
        message = text.ReplaceLineEndings(" ");
        copyDuration = ms;
        copyUntil = clock.ElapsedMilliseconds + ms;
        copied = hoverButton = false;
        SwitchTo(Mode.Copy);
    }

    public void Dismiss() => mode = RestingMode;

    public bool ShowingMessage => mode is Mode.Message or Mode.Copy;

    Mode RestingMode => alwaysVisible ? Mode.Idle : Mode.Hidden;

    void SwitchTo(Mode next)
    {
        if (!shown)
        {
            // Appear on whichever screen you're working on.
            var cursor = Cursor.Position;
            area = Screen.FromPoint(cursor).WorkingArea;
            scale = Native.ScaleAt(cursor);
            (width96, height96) = Size96(next);
            opacity = 0;
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            shown = true;
        }
        mode = next;
        drawn = next;
        lastTick = clock.ElapsedMilliseconds;
        timer.Start();
    }

    (float Width, float Height) Size96(Mode m) => m switch
    {
        Mode.Idle => (32, 6),
        Mode.Listening => (handsFree ? 90 : 54, 20),
        Mode.Working => (54, 20),
        Mode.Copy => (Math.Min(TextWidth96(message), 260) + 30 + 58, 30),
        _ => (TextWidth96(message) + 28, 26),
    };

    static float TextWidth96(string text)
    {
        using var font = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        return TextRenderer.MeasureText(text, font).Width;
    }

    // ---- Animation ----

    void Tick()
    {
        long now = clock.ElapsedMilliseconds;
        long dt = now - lastTick;
        lastTick = now;

        if (mode == Mode.Message && now >= messageUntil) mode = RestingMode;
        if (mode == Mode.Copy)
        {
            if (hoverPill && !copied) copyUntil += dt; // hold still while you're reaching for it
            if (now >= copyUntil) mode = RestingMode;
        }
        SetClickThrough(mode != Mode.Copy);

        // Going back to the idle bar: shrink into it rather than fading out.
        if (mode == Mode.Idle) drawn = Mode.Idle;

        float target = mode switch { Mode.Hidden => 0, Mode.Idle => 0.75f, _ => 1 };
        float step = target > opacity ? 0.16f : 0.12f;
        opacity = Math.Clamp(opacity + Math.Clamp(target - opacity, -step, step), 0, 1);
        if (opacity == 0 && mode == Mode.Hidden)
        {
            timer.Stop();
            Native.ShowWindow(Handle, Native.SW_HIDE);
            shown = false;
            hoverPill = hoverButton = false;
            return;
        }

        var (w, h) = Size96(drawn);
        width96 += (w - width96) * 0.25f;
        height96 += (h - height96) * 0.25f;
        Render(now / 1000f);

        // The idle bar doesn't animate; stop redrawing once it has settled.
        if (mode == Mode.Idle && MathF.Abs(w - width96) < 0.1f && MathF.Abs(h - height96) < 0.1f && opacity == target) timer.Stop();
    }

    void SetClickThrough(bool on)
    {
        if (on == clickThrough) return;
        clickThrough = on;
        Native.SetExStyle(Handle, 0x20, on);
    }

    // ---- Drawing ----

    void Render(float t)
    {
        float pad = Shadow96 * scale;
        float w = width96 * scale, h = height96 * scale;
        int bw = (int)MathF.Ceiling(w + pad * 2), bh = (int)MathF.Ceiling(h + pad * 2);
        var pill = new RectangleF(pad, pad, w, h);

        using var bitmap = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            DrawBody(g, pill);
            switch (drawn)
            {
                case Mode.Listening: DrawListening(g, pill, t); break;
                case Mode.Working: DrawWorking(g, pill, t); break;
                case Mode.Message: DrawText(g, pill, message); break;
                case Mode.Copy: DrawCopy(g, pill); break;
            }
        }

        // Ease up into place as it fades in.
        float rise = drawn == Mode.Idle ? 0 : (1 - EaseOut(opacity)) * 6 * scale;
        var position = new Point(
            area.Left + (area.Width - bw) / 2,
            (int)(area.Bottom - bh - BottomGap96 * scale + pad + rise));
        Push(bitmap, position, (byte)(opacity * 255));
    }

    static float EaseOut(float x) => 1 - (1 - x) * (1 - x) * (1 - x);

    void DrawBody(Graphics g, RectangleF pill)
    {
        // Soft shadow.
        for (int i = 4; i >= 1; i--)
        {
            var r = RectangleF.Inflate(pill, i * scale, i * scale);
            r.Offset(0, 1.5f * scale);
            using var shadowPath = Pill(r);
            using var shadow = new SolidBrush(Color.FromArgb(12, 0, 0, 0));
            g.FillPath(shadow, shadowPath);
        }

        using var path = Pill(pill);
        using (var fill = new LinearGradientBrush(RectangleF.Inflate(pill, 0, 1), Color.FromArgb(242, 34, 34, 40), Color.FromArgb(242, 16, 16, 20), LinearGradientMode.Vertical))
            g.FillPath(fill, path);
        using (var rimBrush = new LinearGradientBrush(RectangleF.Inflate(pill, 1, 1), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(14, 255, 255, 255), LinearGradientMode.Vertical))
        using (var rim = new Pen(rimBrush, MathF.Max(1, scale * 0.8f)))
            g.DrawPath(rim, path);
    }

    static int FromMiddle(int i) => Math.Abs(i - Half);

    Color BarColor(int i, int alpha = 255) => Color.FromArgb(alpha, AiActive ? Blend(FromMiddle(i) / (float)Half) : Plain);

    /// Thin rounded bars, tallest in the middle. Echoes are faint ghosts behind them that fade slowly, like reverb.
    void DrawBars(Graphics g, RectangleF area, Func<int, float> height, Func<int, float>? echo = null)
    {
        float barW = 2f * scale, gap = 1.6f * scale;
        float total = BarCount * barW + (BarCount - 1) * gap;
        float x0 = area.X + (area.Width - total) / 2, cy = area.Y + area.Height / 2;
        float minH = barW, maxH = area.Height - 6 * scale;
        for (int i = 0; i < BarCount; i++)
        {
            float shape = 1 - 0.4f * MathF.Pow(FromMiddle(i) / (float)Half, 2);
            float x = x0 + i * (barW + gap);
            if (echo is not null) FillBar(g, x, cy, barW, minH + (maxH - minH) * Math.Clamp(echo(i), 0, 1) * shape, BarColor(i, 70));
            FillBar(g, x, cy, barW, minH + (maxH - minH) * Math.Clamp(height(i), 0, 1) * shape, BarColor(i));
        }
    }

    static void FillBar(Graphics g, float x, float cy, float w, float h, Color color)
    {
        using var path = Ui.Round(new RectangleF(x, cy - h / 2, w, h), w / 2); // Pill() is for wide shapes only
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// A soft light inside the pill that swells as you speak.
    void DrawGlow(Graphics g, RectangleF pill, float strength)
    {
        if (strength < 0.02f) return;
        var center = new PointF(pill.X + pill.Width / 2, pill.Y + pill.Height / 2);
        var oval = new RectangleF(center.X - pill.Width * 0.5f, center.Y - pill.Height, pill.Width, pill.Height * 2);
        using var clip = Pill(pill);
        using var path = new GraphicsPath();
        path.AddEllipse(oval);
        Color tint = AiActive ? Blend(0.45f) : Plain;
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = center,
            CenterColor = Color.FromArgb((int)(120 * Math.Clamp(strength, 0, 1)), tint),
            SurroundColors = [Color.FromArgb(0, tint)],
        };
        g.SetClip(clip);
        g.FillPath(brush, path);
        g.ResetClip();
    }

    void DrawListening(Graphics g, RectangleF pill, float t)
    {
        // Real frequency bands from the mic, gated by loudness so background hiss stays still.
        var live = bands(Half + 1);
        float gate = Math.Clamp((level() - 0.04f) * 4, 0, 1);
        loudness += (gate - loudness) * (gate > loudness ? 0.5f : 0.08f);
        for (int i = 0; i < BarCount; i++)
        {
            float target = MathF.Pow(live[FromMiddle(i)] * gate, 0.7f); // lift quiet sounds so speech looks lively
            bars[i] += (target - bars[i]) * (target > bars[i] ? 0.6f : 0.2f); // jump up, fall gently
            echoes[i] = Math.Max(echoes[i] * 0.96f, bars[i]); // and leave a slow-fading trail
        }

        var barArea = handsFree ? new RectangleF(pill.X + 4 * scale, pill.Y, pill.Width - 36 * scale, pill.Height) : pill;
        DrawGlow(g, barArea, loudness);
        // While you're quiet, a faint ripple shows it's still listening.
        DrawBars(g, barArea, i => Math.Max(bars[i], 0.05f + 0.07f * Wave(t * 4 - FromMiddle(i) * 0.8f)), i => echoes[i]);

        if (handsFree)
        {
            var elapsed = TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds - listeningSince);
            using var font = new Font("Segoe UI Semibold", 10f * scale, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            using var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            var box = new RectangleF(pill.Right - 40 * scale, pill.Y, 31 * scale, pill.Height);
            g.DrawString($"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}", font, brush, box, format);
        }
    }

    void DrawWorking(Graphics g, RectangleF pill, float t)
    {
        // A gentle pulse and a wave rolling out from the middle.
        DrawGlow(g, pill, 0.3f + 0.25f * Wave(t * 3));
        DrawBars(g, pill, i => 0.12f + 0.45f * MathF.Pow(Wave(t * 7 - FromMiddle(i) * 0.9f), 2));
    }

    static float Wave(float x) => 0.5f + 0.5f * MathF.Sin(x);

    void DrawText(Graphics g, RectangleF box, string text)
    {
        using var font = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, box, format);
    }

    void DrawCopy(Graphics g, RectangleF pill)
    {
        float bw = 52 * scale, bh = 22 * scale;
        buttonRect = new RectangleF(pill.Right - bw - 4 * scale, pill.Y + (pill.Height - bh) / 2, bw, bh);
        using (var path = Pill(buttonRect))
        {
            Color fill = copied ? Color.FromArgb(52, 211, 153) : hoverButton ? Color.White : Color.FromArgb(232, 232, 238);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        using (var font = new Font("Segoe UI Semibold", 11.5f * scale, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(copied ? Color.White : Color.FromArgb(20, 20, 24)))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(copied ? "Copied" : "Copy", font, ink, buttonRect, center);

        var textBox = new RectangleF(pill.X + 14 * scale, pill.Y, buttonRect.X - pill.X - 18 * scale, pill.Height);
        using (var font = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(message, font, brush, textBox, format);

        // Countdown line along the bottom.
        if (!copied)
        {
            float left = Math.Clamp((copyUntil - clock.ElapsedMilliseconds) / (float)copyDuration, 0, 1);
            float x0 = pill.X + pill.Height / 2, x1 = pill.Right - pill.Height / 2;
            var line = new RectangleF(x0, pill.Bottom - 3 * scale, (x1 - x0) * left, 1.5f * scale);
            if (line.Width > 1)
            {
                using var brush = new SolidBrush(Color.FromArgb(110, 255, 255, 255));
                using var path = Pill(line);
                g.FillPath(brush, path);
            }
        }
    }

    // ---- Mouse (only active while the Copy button is showing) ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        hoverPill = true;
        hoverButton = mode == Mode.Copy && buttonRect.Contains(e.Location);
        Cursor = hoverButton ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hoverPill = hoverButton = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (mode != Mode.Copy || copied || e.Button != MouseButtons.Left || !buttonRect.Contains(e.Location)) return;
        try
        {
            Clipboard.SetText(copyText);
            copied = true;
            copyUntil = clock.ElapsedMilliseconds + 900;
        }
        catch (Exception ex)
        {
            Log.Write($"Copy failed: {ex.Message}");
        }
    }

    // ---- Helpers ----

    static Color Blend(float x)
    {
        x = Math.Clamp(x, 0, 1) * (Palette.Length - 1);
        int i = Math.Min((int)x, Palette.Length - 2);
        float f = x - i;
        Color a = Palette[i], b = Palette[i + 1];
        return Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
    }

    static GraphicsPath Pill(RectangleF r)
    {
        var path = new GraphicsPath();
        float d = Math.Min(r.Width, r.Height);
        if (d <= 0) return path;
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    void Push(Bitmap bitmap, Point position, byte alpha)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr previous = Native.SelectObject(memoryDc, hBitmap);
        try
        {
            var dst = new Native.POINT { X = position.X, Y = position.Y };
            var src = new Native.POINT();
            var size = new Native.SIZE { Cx = bitmap.Width, Cy = bitmap.Height };
            var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = alpha, AlphaFormat = 1 /* per-pixel alpha */ };
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memoryDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            Native.SelectObject(memoryDc, previous);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
