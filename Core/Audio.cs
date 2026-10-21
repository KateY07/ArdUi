using NAudio.Wave;

namespace ArdUi;

// The sound card, through Windows' own audio (WinMM): the default recording device in, the default playback device out.
sealed class MicrophoneInput : IVoiceInput
{
    readonly WaveInEvent capture = new()
    {
        DeviceNumber = -1, WaveFormat = new WaveFormat(VoiceFormat.SampleRate, 16, 1), BufferMilliseconds = VoiceFormat.FrameMs, NumberOfBuffers = 4,
    };
    readonly short[] pending = new short[VoiceFormat.FrameSamples * 4]; int filled;
    public event Action<short[]>? Frame;

    public MicrophoneInput() { capture.DataAvailable += OnData; }

    public void Start()
    {
        try { capture.StartRecording(); }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        { throw new IOException("无法打开麦克风：" + ex.Message + "（请检查 Windows 设置里的麦克风权限和默认录音设备）", ex); }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        // the device delivers its own chunk sizes: cut them into exact 20 ms frames
        var samples = e.BytesRecorded / 2;
        for (var i = 0; i < samples; i++)
        {
            pending[filled++] = BitConverter.ToInt16(e.Buffer, i * 2);
            if (filled == VoiceFormat.FrameSamples) { Frame?.Invoke(pending.AsSpan(0, VoiceFormat.FrameSamples).ToArray()); filled = 0; }
        }
    }

    public void Dispose() { capture.DataAvailable -= OnData; try { capture.StopRecording(); } catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException) { } capture.Dispose(); }
}

sealed class SpeakerOutput : IVoiceOutput
{
    readonly WaveOutEvent player = new() { DeviceNumber = -1, DesiredLatency = 80, NumberOfBuffers = 3 };
    readonly BufferedWaveProvider buffer = new(new WaveFormat(VoiceFormat.SampleRate, 16, 1)) { BufferDuration = TimeSpan.FromMilliseconds(400), DiscardOnBufferOverflow = true };
    public double Volume { get => player.Volume; set => player.Volume = (float)Math.Clamp(value, 0, 1); }

    public void Start()
    {
        try { player.Init(buffer); player.Play(); }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        { throw new IOException("无法打开扬声器：" + ex.Message + "（请检查默认播放设备）", ex); }
    }

    public void Write(short[] frame)
    {
        var bytes = new byte[frame.Length * 2]; Buffer.BlockCopy(frame, 0, bytes, 0, bytes.Length);
        buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose() { try { player.Stop(); } catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException) { } player.Dispose(); }
}

// A telephone's ring (incoming) or ringback (outgoing), made from two tones; it plays until stopped.
sealed class Ringtone : IDisposable
{
    sealed class Tones(bool incoming) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(VoiceFormat.SampleRate, 16, 1);
        long position;
        public int Read(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count / 2; i++)
            {
                var seconds = (position++ % (VoiceFormat.SampleRate * 4)) / (double)VoiceFormat.SampleRate;   // a 4 second cycle
                var on = incoming ? seconds < 0.4 || seconds is >= 0.6 and < 1.0 : seconds < 1.0;
                var t = position / (double)VoiceFormat.SampleRate;
                var value = on ? (Math.Sin(2 * Math.PI * (incoming ? 440 : 425) * t) + (incoming ? Math.Sin(2 * Math.PI * 480 * t) : 0)) * (incoming ? 0.12 : 0.2) : 0;
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + i * 2), (short)(value * short.MaxValue));
            }
            return count;
        }
    }

    WaveOutEvent? player;
    public void Start(bool incoming)
    {
        Stop();
        try { player = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 150 }; player.Init(new Tones(incoming)); player.Play(); }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException) { Diagnostics.Log("ringtone", ex.Message); player?.Dispose(); player = null; }
    }
    public void Stop() { try { player?.Stop(); } catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException) { } player?.Dispose(); player = null; }
    public void Dispose() => Stop();
}
