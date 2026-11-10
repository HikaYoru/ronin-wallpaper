using System.Globalization;
using System.Text.Json;

namespace RoninHost.Render;

/// <summary>Live weather from Open-Meteo (free, no key), same requests and mapping as the page.</summary>
internal sealed partial class Scene
{
    sealed class WxData { public int Precip; public float Amount, Fog, Wind; public bool Storm; public string Label = ""; public int Temp; public string Unit = "C"; }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    WxData? wx;
    string wxErr = "", wxPlace = "", wxKey = "";
    double wxLat, wxLon;
    int? wxSunrise, wxSunset;
    double wxDue = -1;
    bool wxBusy;

    void ScheduleWeather(double delaySeconds) => wxDue = now + delaySeconds; // typing a city waits until you pause

    void StepWeather()
    {
        if (wxDue < 0 || now < wxDue || wxBusy) return;
        wxDue = -1;
        var city = Str("city").Trim();
        if (!B("liveWeather") || city.Length == 0)
        {
            wx = null; wxErr = ""; wxSunrise = wxSunset = null;
            hudDirty = true; ComputeEffective(); UpdateTimeOfDay();
            return;
        }
        wxBusy = true;
        bool fahrenheit = (int)N("units") == 1;
        string key = wxKey; double lat = wxLat, lon = wxLon; string place = wxPlace;
        _ = Task.Run(async () =>
        {
            WxData? data = null; string err = ""; int? sr = null, ss = null;
            try
            {
                if (key != city)
                {
                    // "Berlin, Germany" -> search "Berlin", prefer a result matching "Germany"
                    var parts = city.Split(',').Select(s => s.Trim()).ToArray();
                    var hint = string.Join(" ", parts.Skip(1)).ToLowerInvariant();
                    using var g = JsonDocument.Parse(await Http.GetStringAsync(
                        "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name=" + Uri.EscapeDataString(parts[0])));
                    if (!g.RootElement.TryGetProperty("results", out var list) || list.GetArrayLength() == 0) throw new Exception("City not found");
                    var pick = list[0];
                    if (hint.Length > 0)
                        foreach (var r in list.EnumerateArray())
                            if (new[] { "country", "country_code", "admin1" }.Any(p => r.TryGetProperty(p, out var v) && v.GetString() is { } s && hint.Contains(s.ToLowerInvariant())))
                            { pick = r; break; }
                    lat = pick.GetProperty("latitude").GetDouble(); lon = pick.GetProperty("longitude").GetDouble();
                    place = pick.GetProperty("name").GetString() ?? city; key = city;
                }
                var url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(CultureInfo.InvariantCulture) +
                    "&longitude=" + lon.ToString(CultureInfo.InvariantCulture) +
                    "&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m,cloud_cover" +
                    "&daily=sunrise,sunset&forecast_days=1&timezone=auto" + (fahrenheit ? "&temperature_unit=fahrenheit" : "");
                using var j = JsonDocument.Parse(await Http.GetStringAsync(url));
                var c = j.RootElement.GetProperty("current");
                var (precip, amount, fog, storm, label) = Decode(c.GetProperty("weather_code").GetInt32());
                // wind direction is where it blows FROM; take the east-west part for the screen
                double dir = Num(c, "wind_direction_10m") * Math.PI / 180;
                float wind = (float)Math.Clamp(-Math.Sin(dir) * Num(c, "wind_speed_10m") * 2.2, -100, 100);
                data = new WxData
                {
                    Precip = precip, Amount = amount, Storm = storm, Label = label, Wind = wind,
                    Fog = Math.Max(fog, (float)Num(c, "cloud_cover") * .3f),
                    Temp = (int)Math.Round(Num(c, "temperature_2m")), Unit = fahrenheit ? "F" : "C",
                };
                if (j.RootElement.TryGetProperty("daily", out var daily))
                {
                    sr = HhMm(daily, "sunrise"); ss = HhMm(daily, "sunset");
                }
            }
            catch (Exception e) { err = e.Message.Length > 60 ? "Weather unavailable" : e.Message; }

            post(s =>
            {
                s.wxBusy = false;
                if (data != null) { s.wx = data; s.wxErr = ""; s.wxSunrise = sr; s.wxSunset = ss; s.wxKey = key; s.wxLat = lat; s.wxLon = lon; s.wxPlace = place; }
                else { s.wxErr = err; if (s.wxKey != city) s.wx = null; }
                s.hudDirty = true;
                s.ComputeEffective();
                s.UpdateTimeOfDay();
                s.wxDue = s.now + 15 * 60;
            });
        });
    }

    static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    static int? HhMm(JsonElement daily, string name)
    {
        if (!daily.TryGetProperty(name, out var arr) || arr.GetArrayLength() == 0) return null;
        var s = arr[0].GetString() ?? "";
        int i = s.IndexOf('T');
        return i >= 0 && s.Length >= i + 6 && int.TryParse(s.AsSpan(i + 1, 2), out var h) && int.TryParse(s.AsSpan(i + 4, 2), out var m) ? h * 60 + m : null;
    }

    // weather code -> (precip 0 snow / 1 rain / 2 none, amount, fog, storm, label)
    static (int, float, float, bool, string) Decode(int code) => code switch
    {
        0 => (2, 0, 0, false, "Clear"), 1 => (2, 0, 5, false, "Mostly clear"), 2 => (2, 0, 15, false, "Partly cloudy"), 3 => (2, 0, 30, false, "Overcast"),
        45 => (2, 0, 90, false, "Fog"), 48 => (2, 0, 90, false, "Freezing fog"),
        51 => (1, 20, 30, false, "Light drizzle"), 53 => (1, 30, 35, false, "Drizzle"), 55 => (1, 40, 40, false, "Heavy drizzle"),
        56 => (1, 30, 35, false, "Freezing drizzle"), 57 => (1, 45, 40, false, "Freezing drizzle"),
        61 => (1, 40, 35, false, "Light rain"), 63 => (1, 65, 40, false, "Rain"), 65 => (1, 90, 45, false, "Heavy rain"),
        66 => (1, 50, 40, false, "Freezing rain"), 67 => (1, 80, 45, false, "Freezing rain"),
        71 => (0, 35, 30, false, "Light snow"), 73 => (0, 60, 35, false, "Snow"), 75 => (0, 90, 45, false, "Heavy snow"), 77 => (0, 25, 30, false, "Snow grains"),
        80 => (1, 50, 35, false, "Showers"), 81 => (1, 70, 40, false, "Heavy showers"), 82 => (1, 95, 45, false, "Violent showers"),
        85 => (0, 55, 35, false, "Snow showers"), 86 => (0, 90, 45, false, "Heavy snow showers"),
        95 => (1, 80, 45, true, "Thunderstorm"), 96 => (1, 90, 50, true, "Thunderstorm, hail"), 99 => (1, 95, 50, true, "Thunderstorm, hail"),
        _ => (2, 0, 20, false, "Unknown"),
    };
}
