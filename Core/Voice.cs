using Concentus;
using Concentus.Enums;

namespace ArdUi;

// Voice: 16 kHz mono, 20 ms frames, Opus (VoIP mode). The pieces are separate so that each can be tested without a sound card or a network:
// the codec, the jitter buffer, the packet format and the engine that ties capture, transport and playback together.
static class VoiceFormat
{
    public const int SampleRate = 16000, FrameMs = 20, FrameSamples = SampleRate * FrameMs / 1000;   // 320 samples per frame
    public const int Bitrate = 24000, MaxOpus = 400;
}

// One Opus encoder and one decoder. Not thread safe: the engine uses the encoder from its sending thread and the decoder from its playout thread.
sealed class VoiceCodec
{
    readonly IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(VoiceFormat.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
    readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(VoiceFormat.SampleRate, 1);
    readonly byte[] scratch = new byte[VoiceFormat.MaxOpus];

    public VoiceCodec()
    {
        encoder.Bitrate = VoiceFormat.Bitrate; encoder.Complexity = 5; encoder.UseVBR = true; encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        encoder.PacketLossPercent = 8;
    }

    public byte[] Encode(ReadOnlySpan<short> frame)
    {
        var length = encoder.Encode(frame, VoiceFormat.FrameSamples, scratch, scratch.Length);
        return scratch.AsSpan(0, length).ToArray();
    }

    // A null packet is a lost one: the decoder fills the gap from what it knows (packet loss concealment).
    public short[] Decode(byte[]? packet)
    {
        var frame = new short[VoiceFormat.FrameSamples];
        if (packet == null) decoder.Decode(ReadOnlySpan<byte>.Empty, frame, VoiceFormat.FrameSamples, false);
        else decoder.Decode(packet, frame, VoiceFormat.FrameSamples, false);
        return frame;
    }
}

// What goes over the wire: "V1", sequence (2), flags (1), then the Opus packet (empty while muted: only a keepalive that keeps the path open).
static class VoicePacket
{
    public const byte Muted = 1;
    public const byte NoMic = 2;   // with Muted: the sender has no working microphone (not just switched off)
    public const int Header = 5;

    public static byte[] Build(ushort sequence, byte flags, ReadOnlySpan<byte> opus)
    {
        var data = new byte[Header + opus.Length];
        data[0] = (byte)'V'; data[1] = (byte)'1';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), sequence);
        data[4] = flags; opus.CopyTo(data.AsSpan(Header));
        return data;
    }

    public static bool TryParse(byte[] data, out ushort sequence, out byte flags, out byte[] opus)
    {
        sequence = 0; flags = 0; opus = [];
        if (data.Length < Header || data[0] != (byte)'V' || data[1] != (byte)'1' || data.Length > Header + VoiceFormat.MaxOpus) return false;
        sequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)); flags = data[4]; opus = data.AsSpan(Header).ToArray();
        return true;
    }
}

enum VoiceTick { Packet, Lost, Underrun, Muted }

// Puts packets back in order and releases one per 20 ms tick. It waits a few frames to absorb jitter, grows that wait when packets come late
// and shrinks it again when the line is steady; a frame that never arrives is reported as lost (the decoder conceals it).
sealed class JitterBuffer
{
    public const int Min = 2, Max = 12;
    readonly SortedDictionary<int, (byte Flags, byte[] Opus)> frames = new();
    readonly object gate = new();
    int expected = -1, high = -1;        // sequence numbers continued past the 16-bit wrap
    int target = 3, steady, empty;
    bool playing;
    public int Received, Late, Lost, Underruns, Duplicates;
    public int Target { get { lock (gate) return target; } }
    public int Depth { get { lock (gate) return frames.Count; } }

    int Unwrap(ushort sequence) => high < 0 ? sequence : high + (short)(sequence - (ushort)high);

    public void Push(ushort sequence, byte flags, byte[] opus)
    {
        lock (gate)
        {
            var number = Unwrap(sequence);
            Received++;
            if (expected < 0) expected = number;
            if (number < expected) { Late++; steady = 0; if (target < Max) target++; return; }   // too late to play
            if (!frames.TryAdd(number, (flags, opus))) { Duplicates++; return; }
            if (number > high) high = number;
        }
    }

    // One tick of the playout clock.
    public (VoiceTick Kind, byte[] Opus) Pop()
    {
        lock (gate)
        {
            if (!playing)
            {
                if (frames.Count < target) return (VoiceTick.Underrun, []);   // still filling after the start or after a gap
                playing = true; empty = 0;
            }
            // far ahead of the clock (a burst after a stall): skip the oldest frames to keep the delay small
            while (frames.Count > target + 6) { var first = frames.Keys.First(); frames.Remove(first); expected = first + 1; Lost++; }
            if (frames.Remove(expected, out var frame))
            {
                expected++; empty = 0; steady++;
                if (steady >= 250 && target > Min) { target--; steady = 0; }
                return ((frame.Flags & VoicePacket.Muted) != 0 && frame.Opus.Length == 0 ? VoiceTick.Muted : VoiceTick.Packet, frame.Opus);
            }
            // the expected one is missing: when newer ones have arrived and the wait is over, it is lost
            if (frames.Count > 0 && high - expected >= target) { expected++; Lost++; steady = 0; return (VoiceTick.Lost, []); }
            Underruns++; steady = 0;
            if (frames.Count == 0 && ++empty >= 3) { playing = false; if (target < Max) target++; }   // the line ran dry: wait longer next time
            return (VoiceTick.Underrun, []);
        }
    }
}
