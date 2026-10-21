using System.Runtime.InteropServices;

namespace ArdUi;

// Who is driving the mouse: the person at this machine, or software (a remote controller whose input FRD injects with SendInput). Windows marks
// injected mouse input in a low-level hook, so the floating island can leave the remote controller's pointer alone: the island belongs to the
// person sitting at the machine, and the controller's clicks go straight through it to whatever is underneath.
static class LocalInput
{
    delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);

    const int MouseLowLevel = 14, MouseFlagsOffset = 12, KeyboardLowLevel = 13, KeyFlagsOffset = 8;
    const uint Injected = 0x01 | 0x02;      // LLMHF_INJECTED, LLMHF_LOWER_IL_INJECTED
    const uint KeyInjected = 0x10 | 0x02;   // LLKHF_INJECTED, LLKHF_LOWER_IL_INJECTED
    // mouse messages that press or release a button
    static bool IsButton(nint message) => message is 0x201 or 0x202 or 0x204 or 0x205 or 0x207 or 0x208 or 0x20B or 0x20C;
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(1500);

    static HookProc? proc, keyProc;
    static nint hook, keyHook;
    static long injectedAt, physicalAt, pressInjectedAt, pressPhysicalAt;
    static bool forced;   // tests: pretend the hooks are in place

    // Must be called on a thread that pumps messages (the UI thread).
    public static void Start()
    {
        if (hook != 0 || !OperatingSystem.IsWindows()) return;
        proc = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                var injected = (Marshal.ReadInt32(lParam, MouseFlagsOffset) & Injected) != 0; var now = Environment.TickCount64;
                Observe(injected, now);
                if (IsButton(wParam)) Press(injected, now);
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        };
        hook = SetWindowsHookEx(MouseLowLevel, proc, GetModuleHandle(null), 0);
        if (hook == 0) Diagnostics.Log("local-input", "无法安装鼠标钩子：" + Marshal.GetLastWin32Error());
        // the keyboard too: Enter or Space on a focused button is an activation as well
        keyProc = (code, wParam, lParam) =>
        {
            if (code >= 0) Press((Marshal.ReadInt32(lParam, KeyFlagsOffset) & KeyInjected) != 0, Environment.TickCount64);
            return CallNextHookEx(keyHook, code, wParam, lParam);
        };
        keyHook = SetWindowsHookEx(KeyboardLowLevel, keyProc, GetModuleHandle(null), 0);
        if (keyHook == 0) Diagnostics.Log("local-input", "无法安装键盘钩子：" + Marshal.GetLastWin32Error());
    }

    public static void Stop()
    {
        if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
        if (keyHook != 0) { UnhookWindowsHookEx(keyHook); keyHook = 0; }
        proc = null; keyProc = null;
    }

    internal static void Observe(bool injected, long now)
    {
        if (injected) Interlocked.Exchange(ref injectedAt, now); else Interlocked.Exchange(ref physicalAt, now);
    }

    // The mouse is being moved by software, and the person at the machine has not touched it since.
    public static bool RemoteDriving => RemoteDrivingAt(Environment.TickCount64);
    internal static bool RemoteDrivingAt(long now)
    {
        var software = Interlocked.Read(ref injectedAt); var person = Interlocked.Read(ref physicalAt);
        return software != 0 && now - software < Window.TotalMilliseconds && software >= person;
    }
    // ---- decisions only the person at the machine may make --------------------------------------------------------------------------
    // Pressing or releasing a mouse button, or any key, is what activates a control. The latest of those events says who did it. A remote
    // controller's clicks and keys are injected, so a control that grants access can refuse them. (RDP input is not marked: it arrives as if it
    // were typed here. That is a different kind of remote access, and it needs the Windows account.)
    internal static void Press(bool injected, long now)
    {
        if (injected) Interlocked.Exchange(ref pressInjectedAt, now); else Interlocked.Exchange(ref pressPhysicalAt, now);
    }
    public static bool ActivationByPerson => ActivationByPersonAt(Environment.TickCount64);
    internal static bool ActivationByPersonAt(long now)
    {
        if (!forced && hook == 0 && keyHook == 0) return true;   // no hooks: nothing is known, nothing can be refused
        var software = Interlocked.Read(ref pressInjectedAt); var person = Interlocked.Read(ref pressPhysicalAt);
        return software == 0 || person > software || now - software > 5000;   // a stale injected event says nothing about this click
    }
    internal static void Force() => forced = true;
    internal static long InjectedPressAt => Interlocked.Read(ref pressInjectedAt);   // tests
    internal static long InjectedMoveAt => Interlocked.Read(ref injectedAt);
    internal static void Reset() { Interlocked.Exchange(ref injectedAt, 0); Interlocked.Exchange(ref physicalAt, 0); Interlocked.Exchange(ref pressInjectedAt, 0); Interlocked.Exchange(ref pressPhysicalAt, 0); }
}
