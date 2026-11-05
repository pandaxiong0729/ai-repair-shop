using System.IO;
using System.Runtime.InteropServices;

namespace QiaoQiaoAI.UI;

/// <summary>Tiny synthesized, memory-only effects; no audio files, network, or external packages.</summary>
internal sealed class LocalSounds : IDisposable
{
    private IntPtr _hammer;
    private IntPtr _poop;
    private bool _disposed;
    private bool _started;

    public void Play(InteractionTool tool)
    {
        if (_disposed) return;
        try
        {
            if (_hammer == IntPtr.Zero) _hammer = Allocate(CreateWave(190, .085));
            if (_poop == IntPtr.Zero) _poop = Allocate(CreateWave(125, .12));
            _started = PlaySound(tool == InteractionTool.Hammer ? _hammer : _poop, IntPtr.Zero, 0x0001 | 0x0002 | 0x0004);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
    }

    public void Stop()
    {
        if (_disposed || !_started) return;
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        _started = false;
    }

    private static IntPtr Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    private static byte[] CreateWave(double frequency, double duration)
    {
        const int sampleRate = 22050;
        var count = (int)(sampleRate * duration);
        using var stream = new MemoryStream(44 + count * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(count * 2);
        for (var i = 0; i < count; i++)
        {
            var time = (double)i / sampleRate;
            var envelope = Math.Exp(-time * 36) * Math.Min(1, time * 800) * Math.Min(1, (duration - time) * 180);
            var wave = Math.Sin(2 * Math.PI * frequency * time * (1 - time * 1.4));
            writer.Write((short)(wave * envelope * 5000));
        }
        return stream.ToArray();
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        if (_hammer != IntPtr.Zero) Marshal.FreeHGlobal(_hammer);
        if (_poop != IntPtr.Zero) Marshal.FreeHGlobal(_poop);
        _hammer = _poop = IntPtr.Zero;
        _disposed = true;
    }
}
