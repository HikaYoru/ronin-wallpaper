using System.Text.Json.Nodes;

namespace RoninHost;

/// <summary>
/// What the host needs from a wallpaper window. Two implementations:
/// <see cref="WallpaperForm"/> (the original index.html in WebView2) and
/// <see cref="Render.NativeWallpaper"/> (the same wallpaper drawn natively with Direct2D, much lighter).
/// </summary>
internal interface IWallpaperView : IDisposable
{
    Screen Screen { get; }
    bool IsReady { get; }

    /// <summary>Raised on the UI thread once the wallpaper can take settings.</summary>
    event Action? Ready;
    /// <summary>Raised on the UI thread if the wallpaper window is gone (closed, or Explorer took it down).</summary>
    event Action? Gone;
    /// <summary>Raised on the UI thread when the renderer itself failed and should be rebuilt.</summary>
    event Action<string>? Crashed;
    /// <summary>Native renderer: the wallpaper asks for something only the host can do ("task:cycle:board:item", "task:open:…").</summary>
    event Action<string>? Request;

    Task StartAsync();
    void SendProperties(JsonObject props);
    void SendTrack(string? title, string? artist);
    /// <summary>Native renderer: the playing track's cover image (encoded, e.g. JPEG/PNG; null = none).</summary>
    void SendAlbumArt(byte[]? image);
    void SendFpsLimit(int fps);
    /// <summary>128 values: 64 bands left, 64 right, ~0..1.</summary>
    void SendAudio(float[] bins);
    void SendClick(Point screenPt);
    void SendMouseMove(Point screenPt);
    Task SetPausedAsync(bool paused);
    /// <summary>The task board to show on this monitor (TaskStore.ForWallpaper; null = none).</summary>
    void SendBoard(JsonObject? board);
    /// <summary>Where the movable elements are right now, in physical pixels relative to the monitor.</summary>
    Task<LayoutBox[]> GetLayoutAsync();
    bool Owns(IntPtr hwnd);
    Task<string> DebugStateAsync();
    void CloseView();
}

/// <summary>A movable wallpaper element: "clock" (the clock block, with the quote when it is attached), "quote", "todo".</summary>
internal readonly record struct LayoutBox(string Id, RectangleF Bounds);
