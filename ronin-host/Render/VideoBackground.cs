using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.MediaFoundation;

namespace RoninHost.Render;

/// <summary>A background that changes over time (animated GIF or video): one GPU bitmap updated in place.</summary>
internal interface IAnimatedBackground : IDisposable
{
    ID2D1Bitmap1 Bitmap { get; }
    void Step(double now);
}

/// <summary>
/// A looping video background (mp4, mov, mkv, wmv, webm…) through Media Foundation's source reader: video only,
/// converted to 32-bit RGB, each frame copied into one GPU bitmap when its time comes. Decoding happens on the
/// render thread, so a paused or covered monitor doesn't decode anything. Sound is ignored.
/// </summary>
internal sealed class VideoBackground : IAnimatedBackground
{
    static readonly Lazy<bool> started = new(() => { MediaFactory.MFStartup(true).CheckError(); return true; });

    // Media Foundation GUIDs (mfapi.h)
    static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid MediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid VideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    readonly IMFSourceReader reader;
    readonly int width, height, stride;
    readonly byte[] frame;
    public ID2D1Bitmap1 Bitmap { get; }

    double playStart = -1, lastStep = -1;
    long shownTs = -1;
    bool ended;

    VideoBackground(IMFSourceReader reader, int w, int h, int stride, Gfx g)
    {
        this.reader = reader;
        width = w; height = h; this.stride = stride;
        frame = new byte[w * h * 4];
        Array.Fill(frame, (byte)0);
        var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try { Bitmap = g.CreateBitmap(w, h, handle.AddrOfPinnedObject(), w * 4); }
        finally { handle.Free(); }
        ReadNext(out _); // the first frame right away
    }

    public static bool IsVideo(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".mkv" or ".wmv" or ".avi" or ".webm";

    public static VideoBackground? TryOpen(string path, Gfx g)
    {
        IMFSourceReader? reader = null;
        try
        {
            _ = started.Value;
            using var attrs = MediaFactory.MFCreateAttributes(1);
            attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u); // lets the reader convert to RGB32
            reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using (var want = MediaFactory.MFCreateMediaType())
            {
                want.Set(MT_MAJOR_TYPE, MediaType_Video);
                want.Set(MT_SUBTYPE, VideoFormat_RGB32);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, want);
            }
            using var got = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            MediaFactory.MFGetAttributeSize(got, MT_FRAME_SIZE, out var w, out var h).CheckError();
            int stride = (int)w * 4;
            if (got.GetUInt32(MT_DEFAULT_STRIDE, out var s).Success) stride = unchecked((int)s);
            var v = new VideoBackground(reader, (int)w, (int)h, stride, g);
            Log.Write($"native: video background {w}x{h}{(stride < 0 ? " (bottom-up)" : "")}: {Path.GetFileName(path)}");
            return v;
        }
        catch (Exception e)
        {
            reader?.Dispose();
            Log.Write($"native: video '{Path.GetFileName(path)}' failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Read the next frame into <see cref="frame"/>; false at the end of the video.</summary>
    bool ReadNext(out long ts)
    {
        ts = -1;
        using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out ts);
        if ((flags & SourceReaderFlag.EndOfStream) != 0) return false;
        if (sample == null) return true; // a gap (stream tick): nothing to show
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var len);
        try
        {
            int row = width * 4;
            if (stride == row && len >= row * height) Marshal.Copy(ptr, frame, 0, row * height);
            else
                for (int y = 0; y < height; y++)
                {
                    // bottom-up (negative stride): the first row in memory is the last on screen
                    long src = stride >= 0 ? (long)y * stride : (long)(height - 1 - y) * -stride;
                    if (src + row > len) break;
                    Marshal.Copy(ptr + (nint)src, frame, y * row, row);
                }
            // RGB32 leaves alpha undefined: make it opaque
            for (int i = 3; i < frame.Length; i += 4) frame[i] = 255;
        }
        finally { buffer.Unlock(); }
        shownTs = ts;
        return true;
    }

    public void Step(double now)
    {
        if (playStart < 0) playStart = now;
        // a pause (monitor covered): carry on from the same spot, don't race to catch up
        if (lastStep >= 0 && now - lastStep > .5) playStart += now - lastStep - 1 / 30.0;
        lastStep = now;
        long target = (long)((now - playStart) * 10_000_000);
        if (shownTs >= target) return;

        bool changed = false;
        for (int n = 0; n < 8 && shownTs < target; n++) // a few frames at most per step (slow PC: skip ahead, don't stall)
        {
            if (ended || !ReadNext(out _))
            {
                // loop
                reader.SetCurrentPosition(0);
                ended = false;
                playStart = now;
                shownTs = -1;
                target = 0;
                if (!ReadNext(out _)) return;
                changed = true;
                break;
            }
            changed = true;
        }
        if (!changed) return;
        var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try { Bitmap.CopyFromMemory(handle.AddrOfPinnedObject(), (uint)(width * 4)); }
        finally { handle.Free(); }
    }

    public void Dispose() => reader.Dispose();
}
