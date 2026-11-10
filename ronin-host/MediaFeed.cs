using Windows.Media.Control;

namespace RoninHost;

/// <summary>
/// Now playing, from Windows' media sessions (the same source as the volume flyout).
/// Prefers a session that is actually playing; otherwise the current paused one; nothing when stopped.
/// </summary>
internal sealed class MediaFeed : IDisposable
{
    readonly SynchronizationContext ui;
    GlobalSystemMediaTransportControlsSessionManager? mgr;
    readonly List<GlobalSystemMediaTransportControlsSession> hooked = new();
    int refreshQueued;

    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    /// <summary>Raised on the UI thread when the track changes.</summary>
    public event Action? Changed;

    public MediaFeed(SynchronizationContext ui) => this.ui = ui;

    public async Task StartAsync()
    {
        try
        {
            mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            mgr.SessionsChanged += (_, _) => { Rehook(); QueueRefresh(); };
            mgr.CurrentSessionChanged += (_, _) => QueueRefresh();
            Rehook();
            QueueRefresh();
        }
        catch (Exception e) { Log.Write("media: unavailable: " + e.Message); }
    }

    void Rehook()
    {
        lock (hooked)
        {
            foreach (var s in hooked)
            {
                s.MediaPropertiesChanged -= OnSessionEvent;
                s.PlaybackInfoChanged -= OnSessionEvent;
            }
            hooked.Clear();
            if (mgr == null) return;
            foreach (var s in mgr.GetSessions())
            {
                s.MediaPropertiesChanged += OnSessionEvent;
                s.PlaybackInfoChanged += OnSessionEvent;
                hooked.Add(s);
            }
        }
    }

    void OnSessionEvent(GlobalSystemMediaTransportControlsSession s, object e) => QueueRefresh();

    // events arrive in bursts; coalesce them
    void QueueRefresh()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            Interlocked.Exchange(ref refreshQueued, 0);
            await RefreshAsync();
        });
    }

    /// <summary>The track's cover image as the app provides it (usually JPEG or PNG), or null.</summary>
    public byte[]? Art { get; private set; }
    /// <summary>Raised on the UI thread when the cover changes (it often arrives a moment after the title).</summary>
    public event Action? ArtChanged;

    async Task RefreshAsync()
    {
        string? title = null, artist = null;
        byte[]? art = null;
        try
        {
            if (mgr != null)
            {
                var sessions = mgr.GetSessions();
                var current = mgr.GetCurrentSession();
                var pick = sessions.FirstOrDefault(s => Status(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                           ?? (current != null && Status(current) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ? current : null);
                if (pick != null)
                {
                    var p = await pick.TryGetMediaPropertiesAsync();
                    if (!string.IsNullOrWhiteSpace(p?.Title))
                    {
                        title = p.Title; artist = p.Artist;
                        art = await ReadThumbnailAsync(p.Thumbnail);
                    }
                }
            }
        }
        catch (Exception e) { Log.Write("media: refresh failed: " + e.Message); }

        ui.Post(_ =>
        {
            bool artChanged = !SameBytes(art, Art);
            if (artChanged) { Art = art; ArtChanged?.Invoke(); }
            if (title == Title && artist == Artist) return;
            Title = title; Artist = artist;
            Log.Write($"media: {(title == null ? "(nothing playing)" : $"{title} — {artist}")}{(art != null ? $" (cover {art.Length / 1024} KB)" : "")}");
            Changed?.Invoke();
        }, null);
    }

    static async Task<byte[]?> ReadThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
    {
        if (thumb == null) return null;
        try
        {
            using var ras = await thumb.OpenReadAsync();
            if (ras.Size == 0 || ras.Size > 8_000_000) return null;
            using var s = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(ras);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch (Exception e) { Log.Write("media: cover failed: " + e.Message); return null; }
    }

    static bool SameBytes(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

    static GlobalSystemMediaTransportControlsSessionPlaybackStatus Status(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus; }
        catch { return GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed; }
    }

    public void Dispose()
    {
        lock (hooked)
        {
            foreach (var s in hooked)
            {
                s.MediaPropertiesChanged -= OnSessionEvent;
                s.PlaybackInfoChanged -= OnSessionEvent;
            }
            hooked.Clear();
        }
        mgr = null;
    }
}
