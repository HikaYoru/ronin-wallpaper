using System.Globalization;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace RoninHost;

/// <summary>
/// Captures what the speakers are playing (WASAPI loopback of the default output device)
/// and turns it into Lively's audio format: 128 values, 64 log-spaced bands for the left
/// channel then 64 for the right, each roughly 0..1.
/// </summary>
internal sealed class AudioFeed : IDisposable, IMMNotificationClient
{
    const int FftM = 11, N = 1 << FftM; // 2048 samples per channel (~43 ms at 48 kHz)
    const int Bands = 64;
    const double MinHz = 30, MaxHz = 16000;
    // Automatic gain: the top of the scale follows the loudest band of the last few seconds,
    // so the ink line moves the same at low or high playback volume. RangeDb is the visible
    // dynamic range below that top; the top never drops under MinTopDb (keeps hiss flat).
    const double RangeDb = 48, MinTopDb = -48, TopDecayDbPerSec = 4;
    double topDb = MinTopDb;
    long lastSpectrumTicks;
    readonly double[] bandDb = new double[Bands * 2];
    public string Stats { get; private set; } = "";

    readonly object gate = new();
    readonly float[] ringL = new float[N], ringR = new float[N];
    int ringPos;
    long lastDataTicks;
    int sampleRate = 48000;

    readonly float[] lin = new float[N];
    readonly Complex[] fft = new Complex[N];
    readonly float[] window = new float[N];
    readonly float[] mags = new float[N / 2];
    readonly float[] bins = new float[Bands * 2];
    double[] edges = Array.Empty<double>();

    WasapiLoopbackCapture? capture;
    readonly MMDeviceEnumerator enumerator = new();
    volatile bool deviceChanged;
    bool running;
    bool sentSilence = true;

    public AudioFeed()
    {
        for (int i = 0; i < N; i++) window[i] = (float)FastFourierTransform.HannWindow(i, N);
        try { enumerator.RegisterEndpointNotificationCallback(this); } catch { }
    }

    public void Start()
    {
        if (running) return;
        running = true;
        Open();
    }

    public void Stop()
    {
        running = false;
        Close();
    }

    void Open()
    {
        Close();
        try
        {
            var cap = new WasapiLoopbackCapture();
            sampleRate = cap.WaveFormat.SampleRate;
            BuildEdges();
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, e) => { if (e.Exception != null) { Log.Write("audio stopped: " + e.Exception.Message); deviceChanged = true; } };
            cap.StartRecording();
            capture = cap;
            Log.Write($"audio: capturing {cap.WaveFormat}");
        }
        catch (Exception e) { Log.Write("audio open failed: " + e.Message); }
    }

    void Close()
    {
        var cap = capture;
        capture = null;
        if (cap == null) return;
        try { cap.DataAvailable -= OnData; cap.StopRecording(); } catch { }
        cap.Dispose();
    }

    void BuildEdges()
    {
        double top = Math.Min(MaxHz, sampleRate * 0.48);
        edges = new double[Bands + 1];
        for (int b = 0; b <= Bands; b++)
            edges[b] = MinHz * Math.Pow(top / MinHz, b / (double)Bands) * N / sampleRate; // as FFT bin index
    }

    // capture thread
    void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = ((WasapiLoopbackCapture)sender!).WaveFormat;
        int ch = fmt.Channels, bytes = fmt.BitsPerSample / 8, frame = ch * bytes;
        if (frame == 0) return;
        // the shared-mode mix format is 32-bit float on Windows 10/11 (usually reported as "Extensible")
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
        int frames = e.BytesRecorded / frame;
        var buf = e.Buffer;
        lock (gate)
        {
            for (int f = 0; f < frames; f++)
            {
                int o = f * frame;
                float l = Sample(buf, o, bytes, isFloat);
                float r = ch > 1 ? Sample(buf, o + bytes, bytes, isFloat) : l;
                ringL[ringPos] = l; ringR[ringPos] = r;
                ringPos = (ringPos + 1) & (N - 1);
            }
            if (frames > 0) lastDataTicks = Environment.TickCount64;
        }
    }

    static float Sample(byte[] b, int o, int bytes, bool isFloat) => bytes switch
    {
        4 when isFloat => BitConverter.ToSingle(b, o),
        4 => BitConverter.ToInt32(b, o) / 2147483648f,
        3 => ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f,
        2 => BitConverter.ToInt16(b, o) / 32768f,
        _ => 0f,
    };

    /// <summary>
    /// The next 128 audio values, or null when nothing new should be sent
    /// (silence: one all-zero frame is returned, then nothing until sound returns).
    /// Called from the UI timer about 30 times a second. The array is reused between calls.
    /// </summary>
    public float[]? NextBins()
    {
        if (deviceChanged && running) { deviceChanged = false; Open(); }
        if (!running || capture == null) return null;

        bool fresh;
        lock (gate) fresh = Environment.TickCount64 - lastDataTicks < 250;
        if (!fresh)
        {
            if (sentSilence) return null;
            sentSilence = true;
            Array.Clear(bins);
            return bins;
        }
        sentSilence = false;
        Spectrum(ringL, 0);
        Spectrum(ringR, Bands);
        MapBands();
        return bins;
    }

    void Spectrum(float[] ring, int offset)
    {
        lock (gate)
        {
            // unroll the ring so the oldest sample comes first
            int p = ringPos;
            Array.Copy(ring, p, lin, 0, N - p);
            Array.Copy(ring, 0, lin, N - p, p);
        }
        for (int i = 0; i < N; i++) { fft[i].X = lin[i] * window[i]; fft[i].Y = 0; }
        FastFourierTransform.FFT(true, FftM, fft); // NAudio scales the forward transform by 1/N
        for (int i = 0; i < N / 2; i++) mags[i] = MathF.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);

        for (int b = 0; b < Bands; b++)
        {
            double lo = edges[b], hi = edges[b + 1];
            float mag;
            if (hi - lo < 1)
            {
                // band narrower than one FFT bin (low end): interpolate at its centre
                double c = (lo + hi) / 2;
                int k = Math.Clamp((int)c, 0, N / 2 - 2);
                float fr = (float)(c - k);
                mag = mags[k] * (1 - fr) + mags[k + 1] * fr;
            }
            else
            {
                mag = 0;
                int k0 = Math.Clamp((int)Math.Floor(lo), 0, N / 2 - 1), k1 = Math.Clamp((int)Math.Ceiling(hi), k0 + 1, N / 2);
                for (int k = k0; k < k1; k++) if (mags[k] > mag) mag = mags[k];
            }
            // a full-scale sine reads ~0.25 here (1/2 one-sided, 1/2 Hann gain), so *4 puts it at 1.0 = 0 dB
            bandDb[offset + b] = 20 * Math.Log10(mag * 4 + 1e-9);
        }
    }

    void MapBands()
    {
        long now = Environment.TickCount64;
        double dt = lastSpectrumTicks == 0 ? 0 : Math.Min(1, (now - lastSpectrumTicks) / 1000.0);
        lastSpectrumTicks = now;

        double loudest = bandDb.Max();
        topDb = Math.Max(MinTopDb, Math.Max(loudest, topDb - TopDecayDbPerSec * dt));
        double floor = topDb - RangeDb;
        float peak = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            double v = Math.Clamp((bandDb[i] - floor) / RangeDb, 0, 1);
            bins[i] = (float)(Math.Pow(v, 1.7) * 0.9); // keep quiet bands low, peaks lively; loudest ~0.9
            peak = Math.Max(peak, bins[i]);
        }
        Stats = $"loudest band {loudest:0.0} dB, scale top {topDb:0.0} dB, peak out {peak:0.00}, bass L {bins[2]:0.00} mid {bins[32]:0.00} high {bins[56]:0.00}";
    }

    /// <summary>Lively's audio message for the web renderer.</summary>
    public static string Format(float[] values)
    {
        var sb = new StringBuilder(1100);
        sb.Append("{\"t\":\"a\",\"v\":[");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(values[i].ToString("0.###", CultureInfo.InvariantCulture));
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ---- follow the default output device ----
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) deviceChanged = true;
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        Stop();
        try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        enumerator.Dispose();
    }
}
