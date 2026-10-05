using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace EchoFlow;

/// Small custom controls for a clean look in dark or light: cards with rows, toggles, buttons, key chips.
static class Ui
{
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    public static readonly float Scale = GetDpiForSystem() / 96f;

    /// Pixels at this screen's scaling, from pixels at 100%.
    public static int S(float pixels) => (int)Math.Round(pixels * Scale);

    public const string IconFont = "Segoe MDL2 Assets";
    public const string EditIcon = "", DeleteIcon = "", HomeIcon = "", BookIcon = "",
        HistoryIcon = "", SettingsIcon = "", OpenIcon = "";

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        using var path = Round(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void Open(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
}

/// Base for the custom-painted controls: double-buffered, sits on whatever its parent's color is.
abstract class Painted : Control
{
    protected Painted()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.StandardDoubleClick, false); // two quick clicks are two clicks
        Font = Theme.Body;
    }

    protected bool Hovered { get; private set; }

    protected override void OnMouseEnter(EventArgs e) { Hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Ui.Smooth(e.Graphics);
        Draw(e.Graphics);
    }

    protected abstract void Draw(Graphics g);
}

/// On/off switch.
sealed class Toggle : Painted
{
    bool on;
    public event Action<bool>? Toggled;

    public Toggle(bool on)
    {
        this.on = on;
        Size = new Size(Ui.S(40), Ui.S(22));
        Cursor = Cursors.Hand;
    }

    public bool Checked { get => on; set { on = value; Invalidate(); } }

    protected override void OnClick(EventArgs e)
    {
        on = !on;
        Invalidate();
        Toggled?.Invoke(on);
        base.OnClick(e);
    }

    protected override void Draw(Graphics g)
    {
        var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Ui.FillRound(g, track, track.Height / 2, on ? Theme.Strong : Theme.Off);
        float pad = Ui.S(3), d = track.Height - pad * 2;
        float x = on ? track.Right - d - pad : track.X + pad;
        using var knob = new SolidBrush(on ? Theme.OnStrong : Color.White);
        g.FillEllipse(knob, x, track.Y + pad, d, d);
    }
}

/// Rounded button. Primary is the solid one ("Done"); the other is soft ("Reset to default").
sealed class FlatButton : Painted
{
    readonly bool primary;

    public FlatButton(string text, bool primary = false)
    {
        this.primary = primary;
        Font = Theme.Bold;
        Cursor = Cursors.Hand;
        Text = text;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        var size = TextRenderer.MeasureText(Text, Font);
        Size = new Size(size.Width + Ui.S(28), Ui.S(34));
        Invalidate();
        base.OnTextChanged(e);
    }

    protected override void Draw(Graphics g)
    {
        Color fill = primary ? Theme.Strong : Hovered ? Theme.Selected : Theme.Chip;
        if (primary && Hovered) fill = ControlPaint.Light(fill, 0.1f);
        Ui.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(8), fill);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, primary ? Theme.OnStrong : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// A colored dot followed by text, e.g. "● AI cleanup is on".
sealed class StatusDot : Painted
{
    Color dot = Theme.Off;

    public StatusDot() => Height = Ui.S(24);

    public void Set(string text, Color color)
    {
        dot = color;
        Text = text;
        Width = TextRenderer.MeasureText(text, Font).Width + Ui.S(18);
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        float d = Ui.S(8);
        using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, 0, (Height - d) / 2, d, d);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(14), 0, Width, Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// A big number with a caption under it, for the Home page.
sealed class StatTile : Painted
{
    readonly string caption;
    string value = "–";
    static readonly Font Big = new("Segoe UI Semibold", 20f);

    public StatTile(string caption)
    {
        this.caption = caption;
        Height = Ui.S(92);
    }

    public string Value { get => value; set { this.value = value; Invalidate(); } }

    protected override void Draw(Graphics g)
    {
        Ui.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(12), Theme.Card);
        int pad = Ui.S(18);
        TextRenderer.DrawText(g, value, Big, new Point(pad - Ui.S(2), Ui.S(12)), Theme.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, caption, Theme.Small, new Point(pad, Height - Ui.S(30)), Theme.Muted, TextFormatFlags.NoPadding);
    }
}

/// Thin live microphone level bar.
sealed class LevelBar : Painted
{
    float level;

    public LevelBar() => Size = new Size(Ui.S(90), Ui.S(6));

    public float Level { get => level; set { level = value; Invalidate(); } }

    protected override void Draw(Graphics g)
    {
        var track = new RectangleF(0, 0, Width - 1, Height - 1);
        Ui.FillRound(g, track, track.Height / 2, Theme.Off);
        if (level > 0.01f)
            Ui.FillRound(g, track with { Width = track.Width * Math.Clamp(level, 0, 1) }, track.Height / 2, Theme.Good);
    }
}

/// Sidebar navigation item: icon and label, highlighted when selected.
sealed class NavItem : Painted
{
    readonly string icon;
    bool selected;
    static readonly Font IconFont = new(Ui.IconFont, 11f);

    public NavItem(string text, string icon)
    {
        this.icon = icon;
        Text = text;
        Height = Ui.S(38);
        Cursor = Cursors.Hand;
    }

    public bool Selected { get => selected; set { selected = value; Invalidate(); } }

    protected override void Draw(Graphics g)
    {
        if (selected || Hovered)
            Ui.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(8), selected ? Theme.Selected : Theme.Hover);
        TextRenderer.DrawText(g, icon, IconFont, new Rectangle(Ui.S(12), 0, Ui.S(24), Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Text, selected ? Theme.Bold : Theme.Body, new Rectangle(Ui.S(42), 0, Width, Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// Rounded panel of settings rows: title and description on the left, a control on the right,
/// with thin lines between rows. Sizes its own height.
sealed class Card : Panel
{
    public sealed class Row(string title, string? subtitle, Control? right)
    {
        public string Title = title;
        public string? Subtitle = subtitle;
        public Control? Right = right;
        internal Rectangle Bounds, TextBounds;
    }

    readonly List<Row> rows = [];

    public Card()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Card;
    }

    public Row AddRow(string title, string? subtitle = null, Control? right = null)
    {
        var row = new Row(title, subtitle, right);
        rows.Add(row);
        if (right is not null)
        {
            Controls.Add(right);
            right.SizeChanged += (_, _) => PerformLayout();
        }
        PerformLayout();
        return row;
    }

    public void SetSubtitle(Row row, string? subtitle)
    {
        row.Subtitle = subtitle;
        PerformLayout();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int padX = Ui.S(20), padY = Ui.S(16), gap = Ui.S(16), y = 0;
        foreach (var row in rows)
        {
            int rightW = row.Right?.Width ?? 0, rightH = row.Right?.Height ?? 0;
            int textW = Math.Max(Ui.S(80), Width - padX * 2 - (rightW > 0 ? rightW + gap : 0));
            int titleH = TextRenderer.MeasureText(row.Title, Theme.Bold).Height;
            int subH = row.Subtitle is null ? 0
                : Ui.S(2) + TextRenderer.MeasureText(row.Subtitle, Theme.Small, new Size(textW, 0), TextFormatFlags.WordBreak).Height;
            int rowH = Math.Max(titleH + subH, rightH) + padY * 2;
            row.Bounds = new Rectangle(0, y, Width, rowH);
            row.TextBounds = new Rectangle(padX, y + (rowH - titleH - subH) / 2, textW, titleH + subH);
            if (row.Right is not null) row.Right.Location = new Point(Width - padX - rightW, y + (rowH - rightH) / 2);
            y += rowH;
        }
        if (Height != y) Height = y;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        Ui.Smooth(g);
        Ui.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(12), Theme.Card);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int titleH = TextRenderer.MeasureText(row.Title, Theme.Bold).Height;
            TextRenderer.DrawText(g, row.Title, Theme.Bold, row.TextBounds.Location, Theme.Text, TextFormatFlags.NoPadding);
            if (row.Subtitle is not null)
            {
                var sub = new Rectangle(row.TextBounds.X, row.TextBounds.Y + titleH + Ui.S(2), row.TextBounds.Width, row.TextBounds.Height);
                TextRenderer.DrawText(g, row.Subtitle, Theme.Small, sub, Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
            if (i > 0)
            {
                using var pen = new Pen(Theme.Divider);
                g.DrawLine(pen, Ui.S(20), row.Bounds.Y, Width - Ui.S(20), row.Bounds.Y);
            }
        }
    }
}

/// Rounded input field that holds a borderless TextBox.
sealed class Field : Panel
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None, Dock = DockStyle.Fill };

    public Field(bool multiline = false)
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Field;
        Padding = multiline ? new Padding(Ui.S(14), Ui.S(12), Ui.S(6), Ui.S(12)) : new Padding(Ui.S(12), Ui.S(8), Ui.S(12), 0);
        if (!multiline) Height = Ui.S(34);
        Box.Multiline = multiline;
        Controls.Add(Box);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Ui.FillRound(g, r, Ui.S(8), Theme.Field);
        using var path = Ui.Round(r, Ui.S(8));
        using var pen = new Pen(Theme.Divider);
        g.DrawPath(pen, path);
    }
}

/// A scrolling page that stacks headings, notes and cards from top to bottom.
sealed class Page : Panel
{
    public Page()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(Ui.S(32), Ui.S(26), Ui.S(32), Ui.S(26));
    }

    public T Add<T>(T control, int spaceAfter = 12) where T : Control
    {
        control.Dock = DockStyle.Top;
        Controls.Add(control);
        control.BringToFront(); // docked controls stack in reverse z-order
        if (spaceAfter > 0)
        {
            var gap = new Panel { Dock = DockStyle.Top, Height = Ui.S(spaceAfter) };
            Controls.Add(gap);
            gap.BringToFront();
        }
        return control;
    }

    public Label Heading(string text) => Add(new Label { Text = text, Font = Theme.Heading, AutoSize = false, Height = Ui.S(38) }, 2);

    public Note Note(string text, int spaceAfter = 18) => Add(new Note(text), spaceAfter);

    public Label Section(string text) =>
        Add(new Label { Text = text, Font = Theme.Section, AutoSize = false, Height = Ui.S(26), Padding = new Padding(Ui.Scale > 1 ? 1 : 0, 0, 0, 0) }, 6);
}

/// Muted text that wraps to its width.
sealed class Note : Label
{
    public Note(string text)
    {
        Text = text;
        Tag = "muted";
        AutoSize = false;
        Font = Theme.Body;
        ForeColor = Theme.Muted;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Fit(); }

    void Fit()
    {
        if (Width <= 0) return;
        int h = TextRenderer.MeasureText(Text, Font, new Size(Width, 0), TextFormatFlags.WordBreak).Height;
        if (Height != h) Height = h;
    }
}

/// Lays controls side by side (or top to bottom), centered, and sizes itself to fit them.
sealed class Stack : Panel
{
    readonly bool vertical;
    readonly int gap;

    public Stack(bool vertical, int gap, params Control[] items)
    {
        this.vertical = vertical;
        this.gap = Ui.S(gap);
        Margin = Padding.Empty;
        foreach (var item in items)
        {
            Controls.Add(item);
            item.SizeChanged += (_, _) => Arrange();
        }
        Arrange();
    }

    void Arrange()
    {
        var shown = Controls.Cast<Control>().ToList();
        int across = shown.Count == 0 ? 0 : shown.Max(c => vertical ? c.Width : c.Height), along = 0;
        foreach (var c in shown)
        {
            c.Location = vertical ? new Point(0, along) : new Point(along, (across - c.Height) / 2);
            along += (vertical ? c.Height : c.Width) + gap;
        }
        along = Math.Max(0, along - gap);
        Size = vertical ? new Size(across, along) : new Size(along, across);
    }
}
