using Microsoft.Win32;
using NAudio.Wave;

namespace ArdUi;

// Settings > 麦克风诊断: which recording and playback devices Windows offers, whether Windows lets desktop apps use the microphone, and what
// the microphone actually delivers (frames, level, clipping, a flat zero line). The measurements use the same code path as a voice call.
static class MicDiag
{
    public sealed record Measurement(int Frames, double RmsDb, double PeakDb, double Clipped, double Dc, double ActiveShare, TimeSpan Length);

    public static DiagSection Devices()
    {
        var section = new DiagSection { Title = "音频设备" };
        try
        {
            var inputs = WaveInEvent.DeviceCount; var outputs = WaveInterop.waveOutGetNumDevs();
            for (var i = 0; i < inputs; i++) section.Add(i == 0 ? "录音设备" : "", WaveInEvent.GetCapabilities(i).ProductName);
            for (var i = 0; i < outputs; i++) section.Add(i == 0 ? "播放设备" : "", OutputName(i));
            section.Add("通话使用", "Windows 的默认录音设备和默认播放设备（在系统“声音”设置里更改）");
            section.Level = inputs == 0 ? DiagLevel.Bad : outputs == 0 ? DiagLevel.Warn : DiagLevel.Good;
            section.Summary = inputs == 0 ? "没有找到任何录音设备：别人听不到你。请接入麦克风，或在设备管理器 / 声音设置里启用它。"
                : outputs == 0 ? "没有找到播放设备：你听不到对方。" : $"{inputs} 个录音设备，{outputs} 个播放设备。";
        }
        catch (Exception ex) when (ex is NAudio.MmException or DllNotFoundException) { section.Level = DiagLevel.Bad; section.Summary = "读取音频设备失败：" + ex.Message; }
        return section;
    }

    static string OutputName(int device)
    {
        var caps = new WaveOutCapabilities();
        WaveInterop.waveOutGetDevCaps((IntPtr)device, out caps, System.Runtime.InteropServices.Marshal.SizeOf(caps));
        return caps.ProductName;
    }

    // Windows' own switches for microphone access (Settings > Privacy > Microphone) and the group policy over them.
    public static DiagSection Privacy()
    {
        var section = new DiagSection { Title = "Windows 麦克风权限" };
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        var global = Read(Registry.CurrentUser, store, "Value"); var desktop = Read(Registry.CurrentUser, store + @"\NonPackaged", "Value");
        var machine = Read(Registry.LocalMachine, store, "Value");
        var policy = Read(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessMicrophone");
        section.Add("麦克风访问（当前用户）", Describe(global), Level(global));
        section.Add("允许桌面应用访问", Describe(desktop), Level(desktop));
        if (machine != null) section.Add("麦克风访问（整台电脑）", Describe(machine), Level(machine));
        if (policy != null) section.Add("组策略", policy switch { "0" => "由用户决定", "1" => "强制允许", "2" => "强制禁止", _ => policy }, policy == "2" ? DiagLevel.Bad : DiagLevel.Info);
        var denied = global == "Deny" || desktop == "Deny" || machine == "Deny" || policy == "2";
        section.Level = denied ? DiagLevel.Bad : DiagLevel.Good;
        section.Summary = denied ? "Windows 禁止应用使用麦克风：请打开“设置 > 隐私和安全性 > 麦克风”，打开“麦克风访问”和“允许桌面应用访问你的麦克风”。" : "Windows 允许桌面应用使用麦克风。";
        return section;

        static string Describe(string? v) => v switch { "Allow" => "允许", "Deny" => "禁止", null => "未设置（默认允许）", _ => v };
        static DiagLevel Level(string? v) => v == "Deny" ? DiagLevel.Bad : DiagLevel.Good;
        static string? Read(RegistryKey root, string path, string name)
        {
            try { using var key = root.OpenSubKey(path); return key?.GetValue(name)?.ToString(); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
    }

    // Listens for a few seconds and reports the level; onLevel is called with the current level (0..1) for a meter.
    public static async Task<(Measurement? Result, string? Error, short[] Samples)> Listen(TimeSpan length, Action<double>? onLevel, CancellationToken ct, bool keep = false)
    {
        var samples = new List<short>(); var frames = 0; var clipped = 0L; var total = 0L; double sum = 0, dc = 0; var peak = 0; var active = 0;
        var gate = new object();
        MicrophoneInput input;
        try { input = new MicrophoneInput(); } catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException) { return (null, ex.Message, []); }
        using (input)
        {
            input.Frame += frame =>
            {
                lock (gate)
                {
                    frames++; var frameSum = 0.0;
                    foreach (var s in frame) { var a = Math.Abs((int)s); if (a > peak) peak = a; if (a >= 32000) clipped++; sum += (double)s * s; frameSum += (double)s * s; dc += s; total++; }
                    if (Math.Sqrt(frameSum / frame.Length) > 300) active++;
                    if (keep) samples.AddRange(frame);
                }
                onLevel?.Invoke(VoiceLevel.Of(frame));
            };
            try { input.Start(); } catch (IOException ex) { return (null, ex.Message, []); }
            try { await Task.Delay(length, ct); } catch (OperationCanceledException) { }
        }
        lock (gate)
        {
            if (frames == 0) return (new Measurement(0, double.NegativeInfinity, double.NegativeInfinity, 0, 0, 0, length), null, []);
            var rms = Math.Sqrt(sum / Math.Max(1, total));
            return (new Measurement(frames, Db(rms), Db(peak), clipped / (double)Math.Max(1, total), dc / Math.Max(1, total), active / (double)frames, length), null, samples.ToArray());
        }
    }

    static double Db(double v) => v <= 0 ? double.NegativeInfinity : 20 * Math.Log10(v / 32768.0);

    public static DiagSection Judge(Measurement? m, string? error)
    {
        var section = new DiagSection { Title = "麦克风实测" };
        if (error != null)
        {
            section.Level = DiagLevel.Bad; section.Summary = "无法打开麦克风：" + error;
            section.Add("可能原因", "没有录音设备、被其他程序独占、或 Windows 隐私设置禁止了麦克风（见“Windows 麦克风权限”）。");
            return section;
        }
        var expected = (int)(m!.Length.TotalMilliseconds / VoiceFormat.FrameMs);
        section.Add("采集时长", $"{m.Length.TotalSeconds:0.#} 秒，收到 {m.Frames} 帧（应有约 {expected} 帧）", m.Frames < expected * 0.8 ? DiagLevel.Warn : DiagLevel.Info);
        if (m.Frames == 0)
        {
            section.Level = DiagLevel.Bad; section.Summary = "设备打开了，但一帧声音也没有收到：驱动或设备异常。请换一个默认录音设备，或重新插拔麦克风。"; return section;
        }
        section.Add("平均音量", double.IsNegativeInfinity(m.RmsDb) ? "完全静音（数据全为 0）" : $"{m.RmsDb:0.0} dBFS", double.IsNegativeInfinity(m.RmsDb) ? DiagLevel.Bad : m.RmsDb < -50 ? DiagLevel.Warn : DiagLevel.Info);
        section.Add("峰值", double.IsNegativeInfinity(m.PeakDb) ? "—" : $"{m.PeakDb:0.0} dBFS", m.PeakDb > -1 ? DiagLevel.Warn : DiagLevel.Info);
        section.Add("有声音的时间", $"{m.ActiveShare * 100:0}%");
        section.Add("削波（爆音）", $"{m.Clipped * 100:0.00}%", m.Clipped > 0.01 ? DiagLevel.Warn : DiagLevel.Info);
        section.Add("直流偏移", $"{m.Dc:0}", Math.Abs(m.Dc) > 2000 ? DiagLevel.Warn : DiagLevel.Info);
        if (double.IsNegativeInfinity(m.RmsDb)) { section.Level = DiagLevel.Bad; section.Summary = "麦克风在工作，但收到的全是静音：很可能被硬件开关、Windows 设备静音或驱动关掉了。检查设备上的静音键、声音设置里的“输入音量”和“静音”。"; }
        else if (m.Frames < expected * 0.8) { section.Level = DiagLevel.Warn; section.Summary = "收到的声音帧偏少，设备时断时续：可能是 USB 供电、蓝牙耳机省电或驱动问题。"; }
        else if (m.PeakDb < -45) { section.Level = DiagLevel.Warn; section.Summary = "几乎没有声音：请对着麦克风说话再测，或调高 Windows“输入音量”。"; }
        else if (m.RmsDb < -50) { section.Level = DiagLevel.Warn; section.Summary = "音量很低：对方可能听不清，请调高“输入音量”或靠近麦克风。"; }
        else if (m.Clipped > 0.01) { section.Level = DiagLevel.Warn; section.Summary = "音量过大并有爆音：请调低“输入音量”或关闭“麦克风加强”。"; }
        else { section.Level = DiagLevel.Good; section.Summary = "麦克风正常。"; }
        return section;
    }

    // Plays what was recorded (or a tone) through the default playback device.
    public static async Task Play(short[] samples, CancellationToken ct)
    {
        using var output = new SpeakerOutput(); output.Start();
        for (var i = 0; i + VoiceFormat.FrameSamples <= samples.Length && !ct.IsCancellationRequested; i += VoiceFormat.FrameSamples)
        {
            output.Write(samples.AsMemory(i, VoiceFormat.FrameSamples).ToArray());
            if ((i / VoiceFormat.FrameSamples) % 10 == 9) await Task.Delay(180, CancellationToken.None);   // stay ahead of the speaker, not far ahead
        }
        try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
    }

    public static short[] Tone(double seconds = 1.2)
    {
        var tone = new short[(int)(seconds * VoiceFormat.SampleRate)];
        for (var i = 0; i < tone.Length; i++) { var fade = Math.Min(1.0, Math.Min(i, tone.Length - i) / 800.0); tone[i] = (short)(Math.Sin(2 * Math.PI * (i < tone.Length / 2 ? 523 : 659) * i / VoiceFormat.SampleRate) * 9000 * fade); }
        return tone;
    }
}
