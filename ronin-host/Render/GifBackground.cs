using System.Drawing.Imaging;
using Vortice.Direct2D1;

namespace RoninHost.Render;

/// <summary>
/// An animated GIF background: GDI+ decodes and composes each frame, which is copied into one GPU bitmap
/// when its delay is up (frames are never all held in memory). Runs on the render thread.
/// </summary>
internal sealed class GifBackground : IAnimatedBackground
{
    readonly Image img;
    readonly System.Drawing.Bitmap buffer;
    readonly int[] delaysMs;
    readonly int frames;
    int index;
    double next = -1;
    public ID2D1Bitmap1 Bitmap { get; }
    public int FrameCount => frames;

    GifBackground(Image img, Gfx g)
    {
        this.img = img;
        var dim = new FrameDimension(img.FrameDimensionsList[0]);
        frames = img.GetFrameCount(dim);
        // per-frame delays in 1/100 s (property 0x5100); browsers treat 0-1 as 100 ms, so do we
        delaysMs = new int[frames];
        try
        {
            var raw = img.GetPropertyItem(0x5100)?.Value;
            for (int i = 0; i < frames; i++)
            {
                int d = raw != null && raw.Length >= (i + 1) * 4 ? BitConverter.ToInt32(raw, i * 4) : 10;
                delaysMs[i] = d <= 1 ? 100 : d * 10;
            }
        }
        catch { Array.Fill(delaysMs, 100); }
        buffer = new System.Drawing.Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
        Compose(0);
        var bits = buffer.LockBits(new Rectangle(0, 0, buffer.Width, buffer.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { Bitmap = g.CreateBitmap(buffer.Width, buffer.Height, bits.Scan0, bits.Stride); }
        finally { buffer.UnlockBits(bits); }
    }

    /// <summary>An animation if the file is a GIF with more than one frame, else null (use it as a still image).</summary>
    public static GifBackground? TryOpen(string path, Gfx g)
    {
        Image? img = null;
        try
        {
            img = Image.FromFile(path);
            if (img.FrameDimensionsList.Length == 0 || img.GetFrameCount(new FrameDimension(img.FrameDimensionsList[0])) < 2) { img.Dispose(); return null; }
            var anim = new GifBackground(img, g);
            Log.Write($"native: animated background, {anim.frames} frames, {img.Width}x{img.Height}");
            return anim;
        }
        catch (Exception e)
        {
            img?.Dispose();
            Log.Write("native: gif failed: " + e.Message);
            return null;
        }
    }

    void Compose(int i)
    {
        img.SelectActiveFrame(new FrameDimension(img.FrameDimensionsList[0]), i);
        using var gr = Graphics.FromImage(buffer);
        gr.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        gr.DrawImage(img, 0, 0, img.Width, img.Height);
    }

    /// <summary>Show the next frame when its time has come.</summary>
    public void Step(double now)
    {
        if (next < 0) { next = now + delaysMs[0] / 1000.0; return; }
        if (now < next) return;
        // skip frames if we fell behind (paused, slow machine), but never more than one loop
        int steps = 0;
        while (now >= next && steps++ < frames) { index = (index + 1) % frames; next += delaysMs[index] / 1000.0; }
        if (now - next > 1) next = now + delaysMs[index] / 1000.0;
        Compose(index);
        var bits = buffer.LockBits(new Rectangle(0, 0, buffer.Width, buffer.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { Bitmap.CopyFromMemory(bits.Scan0, (uint)bits.Stride); }
        finally { buffer.UnlockBits(bits); }
    }

    /// <summary>The image and buffer (the GPU bitmap belongs to the scene's background and is disposed there).</summary>
    public void Dispose()
    {
        img.Dispose();
        buffer.Dispose();
    }
}
