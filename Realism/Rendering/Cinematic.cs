using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace Realism.Rendering;

/// <summary>
/// A scripted camera film: shots of camera keyframes, rendered at a fixed frame rate and piped into ffmpeg.
///
/// Script (text): blank lines and # comments ignored.
///   shot &lt;seconds&gt; [time=&lt;0..2 time of day&gt;] [weather=clear|overcast|rain]
///   &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;yaw°&gt; &lt;pitch°&gt; [time]     one keyframe per line, spread evenly over the shot
/// Yaw 0 looks along +X, 90 along +Z; pitch is positive up. A keyframe's time overrides the shot's (sun timelapse).
/// </summary>
public sealed class Cinematic
{
    public const int Fps = 30;

    public sealed record Key(Vector3 Pos, float Yaw, float Pitch, float? Time);
    public sealed record Shot(float Seconds, float Time, int Weather, List<Key> Keys);

    public readonly List<Shot> Shots = new();
    public int FrameCount => Shots.Sum(s => (int)MathF.Round(s.Seconds * Fps));

    public static Cinematic Load(string path)
    {
        var c = new Cinematic();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            if (parts[0] == "shot")
            {
                float time = 0.4f;
                int weather = 0;
                foreach (var p in parts.Skip(2))
                {
                    if (p.StartsWith("time=")) time = F(p[5..]);
                    if (p.StartsWith("weather=")) weather = Array.IndexOf(new[] { "clear", "overcast", "rain" }, p[8..]);
                }
                c.Shots.Add(new Shot(F(parts[1]), time, Math.Max(weather, 0), new List<Key>()));
            }
            else
                c.Shots[^1].Keys.Add(new Key(new Vector3(F(parts[0]), F(parts[1]), F(parts[2])), F(parts[3]), F(parts[4]),
                    parts.Length > 5 ? F(parts[5]) : null));
        }
        // Unwrap yaw within each shot so the camera turns the short way.
        foreach (var s in c.Shots)
            for (int i = 1; i < s.Keys.Count; i++)
            {
                float y = s.Keys[i].Yaw, prev = s.Keys[i - 1].Yaw;
                while (y - prev > 180) y -= 360;
                while (y - prev < -180) y += 360;
                s.Keys[i] = s.Keys[i] with { Yaw = y };
            }
        return c;
    }

    /// <summary>Camera at a frame: shot index, frame within the shot, position, yaw/pitch (radians), time of day.</summary>
    public (int shot, int local, Vector3 pos, float yaw, float pitch, float time, int weather) At(int frame)
    {
        int start = 0;
        for (int si = 0; si < Shots.Count; si++)
        {
            var s = Shots[si];
            int n = (int)MathF.Round(s.Seconds * Fps);
            if (frame < start + n || si == Shots.Count - 1)
            {
                int local = Math.Min(frame - start, n - 1);
                float u = n <= 1 ? 0 : local / (float)(n - 1);
                u = u * u * (3 - 2 * u) * 0.35f + u * 0.65f; // gentle ease in and out, never stopping
                var k = s.Keys;
                float x = u * (k.Count - 1);
                int i = Math.Min((int)x, k.Count - 2);
                float t = x - i;
                if (k.Count == 1) { i = 0; t = 0; }
                Key K(int j) => k[Math.Clamp(j, 0, k.Count - 1)];
                Vector3 pos = k.Count == 1 ? k[0].Pos : CatmullRom(K(i - 1).Pos, K(i).Pos, K(i + 1).Pos, K(i + 2).Pos, t);
                float yaw = k.Count == 1 ? k[0].Yaw : CatmullRom(K(i - 1).Yaw, K(i).Yaw, K(i + 1).Yaw, K(i + 2).Yaw, t);
                float pitch = k.Count == 1 ? k[0].Pitch : CatmullRom(K(i - 1).Pitch, K(i).Pitch, K(i + 1).Pitch, K(i + 2).Pitch, t);
                float t0 = K(i).Time ?? s.Time, t1 = (k.Count == 1 ? K(i) : K(i + 1)).Time ?? s.Time;
                float time = t0 + (t1 - t0) * t;
                return (si, local, pos, yaw * MathF.PI / 180, pitch * MathF.PI / 180, time, s.Weather);
            }
            start += n;
        }
        throw new InvalidOperationException("Empty cinematic");
    }

    /// <summary>Brightness of a frame: fades in at the start, out at the end, and dips briefly through black at cuts.</summary>
    public float Fade(int frame)
    {
        int total = FrameCount, start = 0;
        float f = 1;
        f = MathF.Min(f, Math.Clamp(frame / 24f, 0, 1));
        f = MathF.Min(f, Math.Clamp((total - 1 - frame) / 30f, 0, 1));
        for (int si = 0; si < Shots.Count - 1; si++)
        {
            start += (int)MathF.Round(Shots[si].Seconds * Fps);
            int d = Math.Abs(frame - start);
            if (d < 6) f = MathF.Min(f, d / 6f); // a quick dip at each cut
        }
        return f * f * (3 - 2 * f);
    }

    static Vector3 CatmullRom(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t) =>
        0.5f * (2 * b + (c - a) * t + (2 * a - 5 * b + 4 * c - d) * t * t + (3 * b - a - 3 * c + d) * t * t * t);

    static float CatmullRom(float a, float b, float c, float d, float t) =>
        0.5f * (2 * b + (c - a) * t + (2 * a - 5 * b + 4 * c - d) * t * t + (3 * b - a - 3 * c + d) * t * t * t);
}

/// <summary>Raw frames into ffmpeg (H.264 MP4), with cinematic letterbox bars and fades applied on the way.</summary>
public sealed class VideoWriter : IDisposable
{
    readonly Process _ffmpeg;
    readonly Stream _input;
    readonly int _w, _h;
    readonly byte[] _row;
    readonly bool _letterbox;
    // Frames go to ffmpeg on a background thread, so recording never holds up rendering (a frame that has to wait
    // for the encoder would be shown twice in the film).
    readonly System.Collections.Concurrent.BlockingCollection<(byte[] rgba, float fade)> _queue = new(boundedCapacity: 90);
    readonly Thread _writer;

    /// <param name="hardware">Real-time recording: a light, fast encoder setting.</param>
    public VideoWriter(string ffmpeg, string output, int w, int h, int fps, bool letterbox = true, bool hardware = false)
    {
        _letterbox = letterbox;
        _w = w;
        _h = h;
        _row = new byte[w * 4];
        // Real-time recording: a fast preset (the game and renderer need the CPU); offline films take their time.
        string codec = hardware ? "-c:v libx264 -preset veryfast -tune film -crf 15" : "-c:v libx264 -preset fast -crf 16";
        var psi = new ProcessStartInfo(ffmpeg,
            $"-y -loglevel error -f rawvideo -pix_fmt rgba -s {w}x{h} -r {fps} -i - -vf vflip {codec} -pix_fmt yuv420p -movflags +faststart \"{output}\"")
        { RedirectStandardInput = true, UseShellExecute = false };
        _ffmpeg = Process.Start(psi)!;
        try { _ffmpeg.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { } // never slows the game
        _input = _ffmpeg.StandardInput.BaseStream;
        _writer = new Thread(() =>
        {
            foreach (var (rgba, fade) in _queue.GetConsumingEnumerable()) WriteNow(rgba, fade);
        }) { IsBackground = true, Name = "Video writer" };
        _writer.Start();
    }

    /// <summary>One bottom-up RGBA frame (as read from OpenGL). The array must not be reused by the caller.</summary>
    public void Write(byte[] rgba, float fade) => _queue.Add((rgba, fade));

    void WriteNow(byte[] rgba, float fade)
    {
        int bar = _letterbox ? (int)(_h - _w / 2.39f) / 2 : 0; // 2.39:1 widescreen bars
        for (int y = 0; y < _h; y++)
        {
            var row = rgba.AsSpan(y * _w * 4, _w * 4);
            if (y < bar || y >= _h - bar) { Array.Clear(_row); _input.Write(_row); continue; }
            if (fade < 0.999f)
            {
                row.CopyTo(_row);
                for (int i = 0; i < _row.Length; i++) if ((i & 3) != 3) _row[i] = (byte)(_row[i] * fade);
                _input.Write(_row);
            }
            else _input.Write(row);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join();
        _input.Close();
        _ffmpeg.WaitForExit();
    }
}
