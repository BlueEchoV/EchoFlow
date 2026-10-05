namespace EchoFlow;

/// Past dictations, newest first: the time, then the text on one line. Double-click one to copy it.
sealed class HistoryList : ListBox
{
    public HistoryList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Ui.S(54);
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        Font = Theme.Body;
    }

    public void Reload(History history)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var entry in history.Recent()) Items.Add(entry);
        EndUpdate();
    }

    public string? SelectedText => (SelectedItem as History.Entry)?.Text;

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items[e.Index] is not History.Entry entry) return;
        var g = e.Graphics;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.Selected : BackColor)) g.FillRectangle(back, e.Bounds);

        int pad = Ui.S(16);
        var time = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + Ui.S(8), e.Bounds.Width - pad * 2, Ui.S(18));
        var text = time with { Y = time.Bottom + Ui.S(2), Height = Ui.S(22) };
        TextRenderer.DrawText(g, entry.Time.ToString("MMM d, h:mm tt"), Theme.Small, time, Theme.Muted, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, entry.Text.ReplaceLineEndings(" "), Theme.Body, text, Theme.Text,
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        using var line = new Pen(Theme.Divider);
        g.DrawLine(line, e.Bounds.X + pad, e.Bounds.Bottom - 1, e.Bounds.Right - pad, e.Bounds.Bottom - 1);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate(); // ellipses depend on the width
    }
}
