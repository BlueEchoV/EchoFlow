using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EchoFlow;

/// Watches the keyboard for the dictation shortcut and reports when it's pressed and released.
/// The shortcut is modifiers (e.g. Ctrl+Win) or modifiers plus one key (e.g. F8, Ctrl+Space).
/// For modifier-only shortcuts, pressing another key cancels, so normal shortcuts like Ctrl+Win+→ keep working.
/// A shortcut with a regular key swallows that key so it doesn't also type into your app.
sealed class HoldHotkey : IDisposable
{
    [Flags] public enum Mod { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

    public event Action? Pressed, Released, Cancelled, EscapePressed;

    readonly Mod mods;
    readonly int key; // 0 when the shortcut is modifiers only
    readonly SynchronizationContext ui;
    readonly Native.LowLevelKeyboardProc proc; // kept in a field so the GC doesn't collect it
    readonly IntPtr hook;
    readonly HashSet<int> modsDown = [];
    readonly HashSet<int> othersDown = [];
    bool active;

    public HoldHotkey(string spec)
    {
        (mods, key) = Parse(spec);
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Must be created on the UI thread.");
        proc = HookProc;
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // ---- Names ----

    public static (Mod Mods, int Key) Parse(string spec)
    {
        Mod mods = Mod.None;
        int key = 0;
        foreach (var part in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Mod.Ctrl; break;
                case "alt": mods |= Mod.Alt; break;
                case "shift": mods |= Mod.Shift; break;
                case "win" or "windows": mods |= Mod.Win; break;
                default:
                    if (key != 0 || !Enum.TryParse<Keys>(part, ignoreCase: true, out var k) || ModFor((int)k) != Mod.None)
                        throw new FormatException($"Can't use \"{part}\" in the shortcut \"{spec}\".");
                    key = (int)k;
                    break;
            }
        }
        return mods == Mod.None && key == 0 ? throw new FormatException("The shortcut is empty.") : (mods, key);
    }

    public static string Format(Mod mods, int key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(Mod.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(Mod.Alt)) parts.Add("Alt");
        if (mods.HasFlag(Mod.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Mod.Win)) parts.Add("Win");
        if (key != 0) parts.Add(((Keys)key).ToString());
        return string.Join("+", parts);
    }

    /// "Ctrl+Win" → "Ctrl + Win", for showing to people.
    public static string Display(string spec) => spec.Replace("+", " + ");

    public static Mod ModFor(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Mod.Shift,
        0x11 or 0xA2 or 0xA3 => Mod.Ctrl,
        0x12 or 0xA4 or 0xA5 => Mod.Alt,
        0x5B or 0x5C => Mod.Win,
        _ => Mod.None,
    };

    // ---- Watching ----

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & Native.LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                if (OnKey((int)info.vkCode, msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)) return 1; // swallowed
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// Returns true to swallow the key.
    bool OnKey(int vk, bool down)
    {
        // Drop keys we never saw released (e.g. released on the lock screen), so they can't block the shortcut.
        modsDown.RemoveWhere(k => k != vk && !Native.IsDown(k));
        othersDown.RemoveWhere(k => k != vk && k != key && !Native.IsDown(k));

        if (down && vk == 0x1B) ui.Post(_ => EscapePressed?.Invoke(), null);

        var set = ModFor(vk) == Mod.None ? othersDown : modsDown;
        if (down) set.Add(vk); else set.Remove(vk);
        var held = modsDown.Aggregate(Mod.None, (m, k) => m | ModFor(k));

        return key == 0 ? OnModifierShortcut(held, down) : OnKeyShortcut(vk, held, down);
    }

    bool OnModifierShortcut(Mod held, bool down)
    {
        bool comboHeld = held == mods && othersDown.Count == 0;
        if (!active && comboHeld && down)
        {
            active = true;
            Native.SendMaskKey(); // so letting go of Win or Alt doesn't open the Start menu or menu bar
            ui.Post(_ => Pressed?.Invoke(), null);
        }
        else if (active && !comboHeld)
        {
            active = false;
            bool extraKey = othersDown.Count > 0 || (held & ~mods) != 0;
            ui.Post(_ => (extraKey ? Cancelled : Released)?.Invoke(), null);
        }
        return false;
    }

    bool OnKeyShortcut(int vk, Mod held, bool down)
    {
        if (vk != key) return false;
        if (down)
        {
            if (active) return true; // key repeat while held
            if (held != mods || othersDown.Count > 1) return false;
            active = true;
            if (mods.HasFlag(Mod.Win) || mods.HasFlag(Mod.Alt)) Native.SendMaskKey();
            ui.Post(_ => Pressed?.Invoke(), null);
            return true;
        }
        if (!active) return false;
        active = false;
        ui.Post(_ => Released?.Invoke(), null);
        return true;
    }

    public void Dispose() => Native.UnhookWindowsHookEx(hook);
}

/// Records a new shortcut: press the keys you want, let go, and it reports them (e.g. "Ctrl+Win" or "F8").
/// Swallows the keys meanwhile so they don't open the Start menu or do anything else. Esc cancels.
sealed class ShortcutRecorder : IDisposable
{
    public event Action<string>? Recorded;
    public event Action? Cancelled;
    /// What's held right now, for showing live while recording.
    public event Action<string>? Changed;

    readonly SynchronizationContext ui;
    readonly Native.LowLevelKeyboardProc proc;
    readonly IntPtr hook;
    readonly HashSet<int> down = [];
    HoldHotkey.Mod mods;
    int key;

    public ShortcutRecorder()
    {
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Must be created on the UI thread.");
        proc = HookProc;
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        if ((info.flags & Native.LLKHF_INJECTED) != 0) return Native.CallNextHookEx(hook, nCode, wParam, lParam);

        int vk = (int)info.vkCode;
        bool isDown = (int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        if (isDown)
        {
            if (vk == 0x1B) { ui.Post(_ => Cancelled?.Invoke(), null); return 1; }
            down.Add(vk);
            var mod = HoldHotkey.ModFor(vk);
            if (mod != HoldHotkey.Mod.None) mods |= mod; else key = vk;
            string spec = HoldHotkey.Format(mods, key);
            ui.Post(_ => Changed?.Invoke(spec), null);
        }
        else
        {
            down.Remove(vk);
            if (down.Count == 0 && (mods != HoldHotkey.Mod.None || key != 0))
            {
                string spec = HoldHotkey.Format(mods, key);
                mods = HoldHotkey.Mod.None;
                key = 0;
                ui.Post(_ => Recorded?.Invoke(spec), null);
            }
        }
        return 1;
    }

    public void Dispose() => Native.UnhookWindowsHookEx(hook);
}
