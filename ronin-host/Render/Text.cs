using Vortice.DirectWrite;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace RoninHost.Render;

/// <summary>
/// DirectWrite text with the wallpaper's own fonts (fonts\*.ttf next to the exe), CSS-like letter
/// spacing and line height. Families that aren't in the folder fall back to Windows fonts.
/// </summary>
internal static class Text
{
    public const string Cinzel = "Cinzel", Cormorant = "Cormorant Garamond", Shippori = "Shippori Mincho";

    static IDWriteFontCollection1? custom;
    static bool tried, withShippori, shipporiLoaded;
    static IDWriteInMemoryFontFileLoader? loader;
    static readonly List<IDWriteFontFile> files = new();
    static readonly Dictionary<string, (float asc, float desc)> metricsCache = new();
    static readonly object Gate = new();

    /// <summary>
    /// Shippori Mincho is 8.6 MB, so it is only loaded once the "brush" font is actually chosen.
    /// Returns true if the font collection changed (text must be laid out again).
    /// </summary>
    public static bool UseShippori(bool on)
    {
        lock (Gate)
        {
            if (!on || shipporiLoaded) return false;
            withShippori = true;
            custom = null; tried = false; metricsCache.Clear();
            return true;
        }
    }

    static IDWriteFontCollection? Collection
    {
        get
        {
            lock (Gate)
            {
                if (custom != null || tried) return custom;
                tried = true;
                try
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "fonts");
                    if (!Directory.Exists(dir)) return null;
                    // Fonts are read into memory and handed to DirectWrite's in-memory loader: loading them
                    // by path through Vortice only ever succeeded for the first file.
                    if (loader == null)
                    {
                        loader = Gfx.DW.CreateInMemoryFontFileLoader();
                        Gfx.DW.RegisterFontFileLoader(loader);
                    }
                    using var builder = Gfx.DW.CreateFontSetBuilder();
                    foreach (var f in Directory.EnumerateFiles(dir, "*.ttf").Concat(Directory.EnumerateFiles(dir, "*.otf")))
                    {
                        if (!withShippori && Path.GetFileName(f).StartsWith("Shippori", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var bytes = File.ReadAllBytes(f);
                            unsafe
                            {
                                fixed (byte* p = bytes)
                                {
                                    // no owner object: DirectWrite keeps its own copy of the data
                                    var file = loader.CreateInMemoryFontFileReference(Gfx.DW, (IntPtr)p, (uint)bytes.Length, null);
                                    files.Add(file);
                                    builder.AddFontFile(file);
                                }
                            }
                        }
                        catch (Exception e) { Log.Write($"fonts: {Path.GetFileName(f)} failed: {e.Message.Trim()}"); }
                    }
                    using var set = builder.CreateFontSet();
                    custom = Gfx.DW.CreateFontCollectionFromFontSet(set);
                    shipporiLoaded = withShippori;
                    Log.Write($"fonts: {custom.FontFamilyCount} families loaded");
                }
                catch (Exception e) { Log.Write("fonts: load failed: " + e.Message); }
                return custom;
            }
        }
    }

    /// <summary>Collection that has this family (ours, else the system's, which also handles fallbacks).</summary>
    static IDWriteFontCollection? For(string family)
    {
        var c = Collection;
        return c != null && c.FindFamilyName(family, out _) ? c : null;
    }

    /// <summary>Family name with Windows fallbacks when the font file is missing.</summary>
    static string Resolve(string family) => For(family) != null ? family : family switch
    {
        Shippori => "Yu Mincho",
        "Segoe UI Symbol" => family, // Windows' own symbol font (✎ ↑ ↓ ✕)
        _ => "Georgia",
    };

    /// <summary>Ascent and descent as fractions of the font size (for CSS line-height maths).</summary>
    public static (float asc, float desc) Metrics(string family, bool italic)
    {
        var key = family + (italic ? "/i" : "");
        lock (Gate)
        {
            if (metricsCache.TryGetValue(key, out var m)) return m;
            m = (0.9f, 0.25f);
            try
            {
                var name = Resolve(family);
                var coll = For(name) ?? Gfx.DW.GetSystemFontCollection(false);
                if (coll.FindFamilyName(name, out var idx))
                {
                    using var fam = coll.GetFontFamily(idx);
                    using var font = fam.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, italic ? FontStyle.Italic : FontStyle.Normal);
                    var fm = font.Metrics;
                    m = (fm.Ascent / (float)fm.DesignUnitsPerEm, fm.Descent / (float)fm.DesignUnitsPerEm);
                }
            }
            catch { }
            metricsCache[key] = m;
            return m;
        }
    }

    /// <summary>
    /// A text layout. <paramref name="spacing"/> is CSS letter-spacing in px (added after every character).
    /// <paramref name="lineHeight"/> is a CSS line-height in px (null = the font's normal spacing).
    /// <paramref name="maxWidth"/> wraps at that width (null = one line).
    /// </summary>
    public static IDWriteTextLayout Layout(string text, string family, float size, bool italic = false,
        float spacing = 0, float? lineHeight = null, float? maxWidth = null, TextAlignment align = TextAlignment.Leading)
    {
        var name = Resolve(family);
        using var format = Gfx.DW.CreateTextFormat(name, For(name), FontWeight.Normal,
            italic ? FontStyle.Italic : FontStyle.Normal, FontStretch.Normal, size, "en-us");
        format.WordWrapping = maxWidth.HasValue ? WordWrapping.Wrap : WordWrapping.NoWrap;
        format.TextAlignment = align;
        if (lineHeight is float lh)
        {
            var (asc, desc) = Metrics(family, italic);
            // CSS half-leading: the glyph box (ascent + descent) is centred in the line box
            format.SetLineSpacing(LineSpacingMethod.Uniform, lh, (lh - (asc + desc) * size) / 2 + asc * size);
        }
        var layout = Gfx.DW.CreateTextLayout(text, format, maxWidth ?? 100000, 100000);
        if (spacing != 0 && text.Length > 0)
        {
            using var l1 = layout.QueryInterface<IDWriteTextLayout1>();
            l1.SetCharacterSpacing(0, spacing, 0, new TextRange(0, (uint)text.Length));
        }
        return layout;
    }

    public static float Width(IDWriteTextLayout l) => l.Metrics.WidthIncludingTrailingWhitespace;
    public static float Height(IDWriteTextLayout l) => l.Metrics.Height;
    public static float Baseline(IDWriteTextLayout l) { var lm = l.LineMetrics; return lm.Length > 0 ? lm[0].Baseline : 0; }
}
