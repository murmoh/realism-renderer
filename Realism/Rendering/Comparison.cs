using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Realism.Rendering;

/// <summary>
/// Comparison video: Minecraft on the left of a divider, Realism Renderer on the right, the divider gliding slowly
/// back and forth with a round handle, labels in the corners, the original sound kept.
///   Realism --compare sidebyside.mp4 [--swap]         one recording with both windows side by side (Minecraft left,
///                                                     or right with --swap); black borders around them are cropped off
///   Realism --compare minecraft.mp4 realism.mp4       two recordings of the same play (same moment at the start)
/// Options: --out file.mp4, --offset seconds (start the second video later/earlier), --slider "t:pos,t:pos,..."
/// (divider keyframes, pos 0 = left edge .. 1 = right edge; default: a slow repeating sweep).
/// </summary>
public static class Comparison
{
    /// <summary>ffmpeg: REALISM_FFMPEG, else the copy in tools\ next to the program or the project, else on PATH.</summary>
    public static string Ffmpeg()
    {
        if (Environment.GetEnvironmentVariable("REALISM_FFMPEG") is { Length: > 0 } env) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "ffmpeg.exe"))) return Path.Combine(dir.FullName, "tools", "ffmpeg.exe");
        return "ffmpeg";
    }

    public static int Run(string[] args)
    {
        var files = new List<string>();
        string? output = null, slider = null;
        double offset = 0;
        bool swap = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--compare": break;
                case "--out": output = args[++i]; break;
                case "--offset": offset = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--slider": slider = args[++i]; break;
                case "--swap": swap = true; break; // side by side: Realism Renderer on the left, Minecraft on the right
                default: files.Add(args[i]); break;
            }
        }
        if (files.Count is < 1 or > 2 || files.Any(f => !File.Exists(f)))
        {
            Console.WriteLine("Usage: --compare sidebyside.mp4 [--swap]   or   --compare minecraft.mp4 realism.mp4   [--out file] [--offset s] [--slider \"t:pos,...\"]");
            return 1;
        }
        string ffmpeg = Ffmpeg();
        try { Process.Start(new ProcessStartInfo(ffmpeg, "-version") { RedirectStandardOutput = true, UseShellExecute = false })!.WaitForExit(); }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("ffmpeg not found: put ffmpeg.exe in the Realism\\tools folder (or set REALISM_FFMPEG to its path).");
            return 1;
        }
        output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!, Path.GetFileNameWithoutExtension(files[0]) + "_comparison.mp4");

        var (w0, h0, fps, duration) = Probe(ffmpeg, files[0]);
        // Where each view is in its recording (x, y, w, h).
        (int x, int y, int w, int h) mcBox, rrBox;
        string mcFile = files[0], rrFile = files.Count == 2 ? files[1] : files[0];
        if (files.Count == 1)
        {
            // One recording with both windows side by side: the picture inside any black border, split down the middle.
            var c = ContentBox(ffmpeg, files[0], w0, h0, duration);
            int half = c.w / 2;
            var left = (c.x, c.y, half, c.h);
            var right = (c.x + c.w - half, c.y, half, c.h);
            (mcBox, rrBox) = swap ? (right, left) : (left, right);
            Console.WriteLine($"Side-by-side recording {w0}x{h0}: views {half}x{c.h} at ({c.x},{c.y}); Minecraft on the {(swap ? "right" : "left")}, Realism Renderer on the {(swap ? "left" : "right")}");
        }
        else
        {
            mcBox = (0, 0, w0, h0);
            var (w1, h1, _, _) = Probe(ffmpeg, files[1]);
            rrBox = (0, 0, w1, h1);
        }
        // Output: the Minecraft view's size, brought up to at least 720p (small window captures look soft otherwise).
        int h = mcBox.h, w = mcBox.w;
        if (h < 720) { w = (int)Math.Round(w * 720.0 / h); h = 720; }
        w -= w % 2;
        h -= h % 2;
        var keys = slider != null ? ParseKeys(slider) : DefaultSweep(duration);
        Console.WriteLine($"Making {Path.GetFileName(output)}: {w}x{h} at {fps:0.##} fps, {duration:0.0} s");

        // Decoders: raw RGB frames of each view, cropped and scaled to the output size, at one frame rate.
        var a = Decode(ffmpeg, mcFile, mcBox, (w, h), fps, 0);
        var b = Decode(ffmpeg, rrFile, rrBox, (w, h), fps, files.Count == 2 ? offset : 0);
        string font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf");
        string Label(string text, string x) =>
            $"drawtext=fontfile='{font.Replace("\\", "/").Replace(":", "\\:")}':text='{text}':fontsize=h/24:fontcolor=white:" +
            $"shadowcolor=black@0.7:shadowx=2:shadowy=2:x={x}:y=h-th-h/18";
        var encode = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}",
                     "-r", fps.ToString(CultureInfo.InvariantCulture), "-i", "-", "-i", files[0], "-map", "0:v", "-map", "1:a?",
                     "-vf", $"{Label("MINECRAFT", "w/30")},{Label("REALISM RENDERER", "w-tw-w/30")}",
                     "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
                     "-shortest", "-movflags", "+faststart", output })
            encode.ArgumentList.Add(arg);
        using var enc = Process.Start(encode)!;
        var outStream = enc.StandardInput.BaseStream;

        var fa = new byte[w * h * 3];
        var fb = new byte[w * h * 3];
        var frame = new byte[w * h * 3];
        int n = 0;
        var clock = Stopwatch.StartNew();
        while (ReadFull(a, fa) && ReadFull(b, fb))
        {
            double t = n / fps;
            int split = (int)Math.Round(Position(keys, t) * w);
            for (int y = 0; y < h; y++)
            {
                // Left of the divider: Minecraft; right: Realism Renderer.
                int row = y * w * 3;
                Buffer.BlockCopy(fa, row, frame, row, split * 3);
                Buffer.BlockCopy(fb, row + split * 3, frame, row + split * 3, (w - split) * 3);
            }
            DrawDivider(frame, w, h, split);
            outStream.Write(frame);
            if (++n % (int)Math.Max(fps * 5, 1) == 0)
                Console.Write($"\r  {t:0}/{duration:0} s  ({n / clock.Elapsed.TotalSeconds:0} frames/s)   ");
        }
        outStream.Close();
        enc.WaitForExit();
        a.Kill();
        b.Kill();
        Console.WriteLine($"\rSaved {output} ({n} frames)                ");
        return enc.ExitCode;
    }

    /// <summary>The part of a recording that isn't black border (from a few frames spread over the video).</summary>
    static (int x, int y, int w, int h) ContentBox(string ffmpeg, string file, int w, int h, double duration)
    {
        var max = new byte[w * h];
        var gray = new byte[w * h];
        for (int k = 1; k <= 6; k++)
        {
            var p = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in new[] { "-loglevel", "error", "-ss", (duration * k / 7).ToString("0.00", CultureInfo.InvariantCulture), "-i", file,
                         "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-" })
                p.ArgumentList.Add(arg);
            using var proc = Process.Start(p)!;
            if (!ReadFull(proc, gray)) continue;
            for (int i = 0; i < gray.Length; i++) if (gray[i] > max[i]) max[i] = gray[i];
        }
        int x0 = w, x1 = -1, y0 = h, y1 = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (max[y * w + x] > 20) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        return x1 < 0 ? (0, 0, w, h) : (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>The divider: a thin white line with a soft shadow, and a round handle with arrows in the middle.</summary>
    static void DrawDivider(byte[] f, int w, int h, int split)
    {
        void Blend(int x, int y, byte r, byte g, byte b, float a)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h || a <= 0) return;
            int i = (y * w + x) * 3;
            f[i] = (byte)(f[i] + (r - f[i]) * a);
            f[i + 1] = (byte)(f[i + 1] + (g - f[i + 1]) * a);
            f[i + 2] = (byte)(f[i + 2] + (b - f[i + 2]) * a);
        }
        int lineW = Math.Max(2, h / 360);
        for (int y = 0; y < h; y++)
        {
            for (int d = 1; d <= 3; d++) { Blend(split - lineW / 2 - d, y, 0, 0, 0, 0.18f / d); Blend(split + (lineW + 1) / 2 + d - 1, y, 0, 0, 0, 0.18f / d); }
            for (int x = split - lineW / 2; x < split + (lineW + 1) / 2; x++) Blend(x, y, 255, 255, 255, 0.95f);
        }
        // Handle: a white ring with a dark centre and two arrows.
        float radius = h / 22f, cx = split, cy = h / 2f;
        for (int y = (int)(cy - radius - 3); y <= cy + radius + 3; y++)
        for (int x = (int)(cx - radius - 3); x <= cx + radius + 3; x++)
        {
            float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            float inside = Math.Clamp(radius - d + 0.5f, 0, 1);
            Blend(x, y, 20, 20, 20, inside * 0.55f);
            float ring = Math.Clamp(1.5f - MathF.Abs(d - radius + 1.5f), 0, 1);
            Blend(x, y, 255, 255, 255, ring);
            // Arrows: triangles pointing left and right.
            float ax = MathF.Abs(x - cx), ay = MathF.Abs(y - cy), tip = radius * 0.62f, back = radius * 0.22f;
            if (ax >= back && ax <= tip && ay <= (tip - ax) * 0.75f) Blend(x, y, 255, 255, 255, 1);
        }
    }

    /// <summary>Divider position (0..1) at time t: keyframes eased in and out (the divider glides, never jumps).</summary>
    static double Position(List<(double t, double p)> keys, double t)
    {
        if (t <= keys[0].t) return keys[0].p;
        for (int i = 0; i + 1 < keys.Count; i++)
            if (t < keys[i + 1].t)
            {
                double u = (t - keys[i].t) / (keys[i + 1].t - keys[i].t);
                u = u * u * u * (u * (u * 6 - 15) + 10); // smootherstep: slow start, slow stop
                return keys[i].p + (keys[i + 1].p - keys[i].p) * u;
            }
        return keys[^1].p;
    }

    /// <summary>A slow repeating sweep: rest in the middle, glide right (more Minecraft), back left (more realism).</summary>
    static List<(double t, double p)> DefaultSweep(double duration)
    {
        var keys = new List<(double, double)>();
        var cycle = new (double dt, double p)[] { (0, 0.5), (2.0, 0.5), (4.0, 0.82), (1.5, 0.82), (6.0, 0.18), (1.5, 0.18), (4.0, 0.5) };
        double t = 0;
        while (t < duration + 1)
            foreach (var (dt, p) in cycle)
            {
                if (keys.Count > 0 && dt == 0) continue;
                t += dt;
                keys.Add((t, p));
            }
        return keys;
    }

    static List<(double t, double p)> ParseKeys(string s) =>
        s.Split(',').Select(k => k.Split(':')).Select(k => (double.Parse(k[0], CultureInfo.InvariantCulture), Math.Clamp(double.Parse(k[1], CultureInfo.InvariantCulture), 0, 1)))
            .OrderBy(k => k.Item1).ToList();

    static (int w, int h, double fps, double duration) Probe(string ffmpeg, string file)
    {
        var p = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        p.ArgumentList.Add("-hide_banner");
        p.ArgumentList.Add("-i");
        p.ArgumentList.Add(file);
        using var proc = Process.Start(p)!;
        string info = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        var size = Regex.Match(info, @"Video:.*?, (\d{2,5})x(\d{2,5})");
        var rate = Regex.Match(info, @"([\d.]+) fps");
        var dur = Regex.Match(info, @"Duration: (\d+):(\d+):([\d.]+)");
        if (!size.Success) throw new InvalidDataException($"No video found in {file}");
        double fps = rate.Success ? double.Parse(rate.Groups[1].Value, CultureInfo.InvariantCulture) : 30;
        double seconds = dur.Success ? int.Parse(dur.Groups[1].Value) * 3600 + int.Parse(dur.Groups[2].Value) * 60 + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture) : 60;
        return (int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value), Math.Clamp(fps, 1, 120), seconds);
    }

    static Process Decode(string ffmpeg, string file, (int x, int y, int w, int h) box, (int w, int h) size, double fps, double offset)
    {
        var p = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, UseShellExecute = false };
        var args = new List<string> { "-loglevel", "error" };
        if (offset > 0) args.AddRange(["-ss", offset.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", file]);
        string vf = $"fps={fps.ToString(CultureInfo.InvariantCulture)},crop={box.w}:{box.h}:{box.x}:{box.y},scale={size.w}:{size.h}:flags=lanczos";
        if (offset < 0) vf = $"tpad=start_duration={(-offset).ToString(CultureInfo.InvariantCulture)}:start_mode=clone," + vf;
        args.AddRange(["-vf", vf, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]);
        foreach (var a in args) p.ArgumentList.Add(a);
        return Process.Start(p)!;
    }

    static bool ReadFull(Process p, byte[] buffer)
    {
        int got = 0;
        var s = p.StandardOutput.BaseStream;
        while (got < buffer.Length)
        {
            int r = s.Read(buffer, got, buffer.Length - got);
            if (r <= 0) return false;
            got += r;
        }
        return true;
    }
}
