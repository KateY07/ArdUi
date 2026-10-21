using Vortice.MediaFoundation;

namespace ArdUi;

// Screen frames in, H.264 files out, using the H.264 encoder that comes with Windows (Media Foundation): nothing to ship, no codec
// licence to carry. Frames are BGRA, top-down, Width x Height (even numbers). The recording is cut into segments of a fixed length
// (rec-0001.mp4, rec-0002.mp4 ...). Files are fragmented MP4, so a crash or power loss leaves everything but the last second playable.
sealed class H264Recorder : IDisposable
{
    static readonly Lazy<bool> available = new(Probe);
    // False where Windows has no H.264 encoder (for example an N edition without the Media Feature Pack).
    public static bool Available => available.Value;

    readonly string folder;
    readonly int width, height, fps, segmentSeconds, bitrate;
    readonly Stopwatch clock = new();
    IMFSinkWriter? writer;
    int stream, number;
    long segmentStart;   // clock milliseconds at which the current segment began
    bool disposed;

    public int Segments => number;

    public H264Recorder(string folder, int width, int height, int fps, int segmentSeconds, int firstNumber)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("画面尺寸必须是偶数。");
        this.folder = folder; this.width = width; this.height = height; this.fps = fps; this.segmentSeconds = segmentSeconds; number = firstNumber - 1;
        bitrate = (int)Math.Clamp((long)width * height * fps / 14, 400_000, 4_000_000);   // about 0.07 bit per pixel: a desktop stays readable
        Startup();
        clock.Start();
        Open();
    }

    static void Startup() { var result = MediaFactory.MFStartup(); if (result.Failure) throw new IOException("无法初始化 Windows 媒体组件：" + result.Code.ToString("X")); }

    // One frame, taken now. Segments rotate on their own.
    public void Write(byte[] bgra)
    {
        if (disposed) throw new ObjectDisposedException(nameof(H264Recorder));
        var now = clock.ElapsedMilliseconds;
        if (writer == null || now - segmentStart >= segmentSeconds * 1000L) { Close(); Open(); now = clock.ElapsedMilliseconds; }
        var size = width * height * 4;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        buffer.Lock(out var pointer, out _, out _);
        try { System.Runtime.InteropServices.Marshal.Copy(bgra, 0, pointer, size); } finally { buffer.Unlock(); }
        buffer.CurrentLength = size;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = (now - segmentStart) * 10_000L;       // 100 ns units, from the real clock: a skipped frame does not speed the video up
        sample.SampleDuration = 10_000_000L / fps;
        writer!.WriteSample(stream, sample);
    }

    void Open()
    {
        number++;
        var path = Path.Combine(folder, $"rec-{number:D4}.mp4");
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Fmpeg4);   // fragmented MP4
        var sink = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);
        try
        {
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
                output.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)(fps * 5));
                output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
                MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
                MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                stream = sink.AddStream(output);
            }
            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
                MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
                MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));   // positive: top-down, as the grabber delivers it
                sink.SetInputMediaType(stream, input, null);
            }
            sink.BeginWriting();
        }
        catch { sink.Dispose(); throw; }
        writer = sink; segmentStart = clock.ElapsedMilliseconds;
    }

    void Close()
    {
        var current = writer; writer = null;
        if (current == null) return;
        try { current.Finalize(); } finally { current.Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Close(); } catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or IOException) { }
        MediaFactory.MFShutdown();
    }

    // Writes one small file to see whether the encoder exists on this machine.
    static bool Probe()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ArdUi-h264-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            using var recorder = new H264Recorder(folder, 320, 240, 10, 60, 1);
            recorder.Write(new byte[320 * 240 * 4]);
            return true;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or IOException or DllNotFoundException or EntryPointNotFoundException or ArgumentException or InvalidOperationException)
        { FileLog.Write("audit", "没有可用的 H.264 编码器：" + ex.Message); return false; }
        finally { try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
