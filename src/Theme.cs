using System.Runtime.InteropServices;

namespace EchoFlow;

/// Colors for the app's windows, in dark or light. Custom controls read these when they paint.
static class Theme
{
    public static bool Dark { get; private set; } = true;

    public static Color Window => Dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(250, 249, 247);
    public static Color Sidebar => Dark ? Color.FromArgb(19, 19, 22) : Color.FromArgb(242, 240, 236);
    public static Color Card => Dark ? Color.FromArgb(34, 34, 38) : Color.FromArgb(241, 239, 234);
    public static Color Field => Dark ? Color.FromArgb(44, 44, 49) : Color.White;
    public static Color Chip => Dark ? Color.FromArgb(58, 58, 64) : Color.FromArgb(240, 238, 232);
    public static Color Divider => Dark ? Color.FromArgb(48, 48, 54) : Color.FromArgb(226, 223, 216);
    public static Color Text => Dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(28, 28, 30);
    public static Color Muted => Dark ? Color.FromArgb(150, 150, 160) : Color.FromArgb(110, 108, 104);
    public static Color Hover => Dark ? Color.FromArgb(44, 44, 50) : Color.FromArgb(232, 229, 223);
    public static Color Selected => Dark ? Color.FromArgb(52, 52, 58) : Color.FromArgb(225, 222, 215);
    /// Main buttons and switched-on toggles: near-white on dark, near-black on light.
    public static Color Strong => Dark ? Color.FromArgb(240, 240, 244) : Color.FromArgb(28, 28, 30);
    public static Color OnStrong => Dark ? Color.FromArgb(20, 20, 22) : Color.White;
    public static Color Good => Color.FromArgb(52, 199, 120);
    public static Color Warn => Color.FromArgb(240, 160, 60);
    public static Color Off => Dark ? Color.FromArgb(90, 90, 98) : Color.FromArgb(200, 196, 188);

    public static readonly FontFamily Family = new("Segoe UI");
    public static Font Body { get; } = new("Segoe UI", 10f);
    public static Font Small { get; } = new("Segoe UI", 9f);
    public static Font Bold { get; } = new("Segoe UI Semibold", 10f);
    public static Font Heading { get; } = new("Segoe UI Semibold", 16f);
    public static Font Section { get; } = new("Segoe UI Semibold", 10.5f);

    public static event Action? Changed;

    public static void Set(bool dark)
    {
        if (dark == Dark) return;
        Dark = dark;
        Changed?.Invoke();
    }

    /// Recolors standard controls and the title bar of a window. Custom controls repaint themselves.
    public static void Apply(Control root)
    {
        if (root is Form form) TitleBar(form);
        Recolor(root);
        root.Invalidate(true);
    }

    static void Recolor(Control c)
    {
        switch (c)
        {
            case TextBox or ListBox or ComboBox:
                c.BackColor = Field;
                c.ForeColor = Text;
                break;
            case Label label when label.Tag as string == "muted":
                label.ForeColor = Muted;
                break;
            case Label:
                c.ForeColor = Text;
                break;
        }
        if (c is Form) c.BackColor = Window;
        if (c is Card) c.BackColor = Card;
        if (c is Field) c.BackColor = Field;
        if (c.Tag as string == "sidebar") c.BackColor = Sidebar;
        if (c.IsHandleCreated) ScrollBars(c);
        else c.HandleCreated += (_, _) => ScrollBars(c);
        foreach (Control child in c.Controls) Recolor(child);
    }

    // ---- Native dark title bar and scrollbars ----

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    static void TitleBar(Form form)
    {
        void Set()
        {
            int on = Dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        }
        if (form.IsHandleCreated) Set(); else form.HandleCreated += (_, _) => Set();
    }

    static void ScrollBars(Control c)
    {
        if (c is TextBox or ListBox or ScrollableControl)
            SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
        if (c is ComboBox)
            SetWindowTheme(c.Handle, Dark ? "DarkMode_CFD" : "CFD", null);
    }
}
