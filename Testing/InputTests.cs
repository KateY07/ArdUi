using System.Runtime.InteropServices;

namespace ArdUi;

// `ArdUi.exe --input-test`: the low-level hooks really see software-injected input as injected. The test sends a harmless key (F24) and a one
// pixel mouse move and back with SendInput, which is how FRD injects a remote controller's input. It never presses a mouse button.
static class InputTests
{
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public int Dx, Dy, Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Key { public short Vk, Scan; public int Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct Input { [FieldOffset(0)] public int Type; [FieldOffset(8)] public Mouse M; [FieldOffset(8)] public Key K; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg message, nint window, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, nint w, nint l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] struct Msg { public nint Hwnd; public uint Message; public nint W, L; public uint Time; public int X, Y; }

    static int failures;
    static void Check(bool ok, string name) { if (!ok) { failures++; Console.Error.WriteLine("FAIL " + name); } else Console.WriteLine("ok   " + name); }

    public static int Run()
    {
        uint pump = 0; using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            pump = GetCurrentThreadId();
            LocalInput.Start(); ready.Set();
            while (GetMessage(out _, 0, 0, 0) > 0) { }   // hooks are served by this thread's message loop
            LocalInput.Stop();
        }) { IsBackground = true };
        thread.Start(); ready.Wait();
        Thread.Sleep(200);
        LocalInput.Reset();
        Check(LocalInput.ActivationByPerson, "before any input, nothing is refused");

        var key = new[] { new Input { Type = 1, K = new Key { Vk = 0x87 } }, new Input { Type = 1, K = new Key { Vk = 0x87, Flags = 2 } } };   // F24 down, up
        Check(SendInput(2, key, Marshal.SizeOf<Input>()) == 2, "SendInput delivered the key");
        Thread.Sleep(300);
        // (a person at the keyboard during the test would add physical events, so the hook is checked for what it saw, not for the final verdict)
        Check(Environment.TickCount64 - LocalInput.InjectedPressAt < 2000 && LocalInput.InjectedPressAt != 0, "a key sent with SendInput is seen as injected by the hook");

        LocalInput.Reset();
        var move = new[] { new Input { Type = 0, M = new Mouse { Dx = 1, Flags = 1 } }, new Input { Type = 0, M = new Mouse { Dx = -1, Flags = 1 } } };
        SendInput(2, move, Marshal.SizeOf<Input>());
        Thread.Sleep(300);
        Check(LocalInput.InjectedMoveAt != 0 && Environment.TickCount64 - LocalInput.InjectedMoveAt < 2000, "a mouse move sent with SendInput is seen as injected by the hook");

        PostThreadMessage(pump, 0x0012, 0, 0);   // WM_QUIT
        thread.Join(2000);
        Console.WriteLine(failures == 0 ? "PASS: injected input is told apart from the person's." : failures + " failure(s).");
        return failures == 0 ? 0 : 1;
    }
}
