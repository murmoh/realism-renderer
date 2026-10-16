using System.Globalization;
using Realism.Live;

namespace Realism.Rendering;

/// <summary>
/// A gameplay film: segments of real play recorded live from a running Minecraft (test client). Each segment runs
/// Minecraft commands (teleport, time, summon, give, ... via the test client's director file), waits until the
/// scene has loaded, then plays timed player actions while recording.
///
/// Script (text): blank lines and # comments ignored.
///   seg &lt;seconds&gt; [view=first|third]
///   cmd &lt;minecraft command&gt;                      run when the segment starts (before recording)
///   at &lt;t&gt; look &lt;yaw&gt; &lt;pitch&gt;                  Minecraft degrees; interpolated between look keys
///   at &lt;t&gt; keys forward,back,left,right,jump,sneak,sprint,attack,use | none
///   at &lt;t&gt; slot &lt;0-8&gt;                           hotbar slot
///   at &lt;t&gt; cam &lt;yaw offset°&gt; &lt;distance&gt; &lt;height&gt; [tilt°]  third-person camera around the player (tilt: look
///                                               above the player); interpolated; pulled in before rock and trees
/// </summary>
public sealed class Gameplay
{
    public sealed class Segment
    {
        public float Seconds;
        public bool Third;
        public readonly List<string> Commands = new();
        public readonly List<(float t, float yaw, float pitch)> Looks = new();
        public readonly List<(float t, int keys)> Keys = new();
        public readonly List<(float t, int slot)> Slots = new();
        public readonly List<(float t, float off, float dist, float height, float tilt)> Cams = new();

        public (float yaw, float pitch) LookAt(float t)
        {
            if (Looks.Count == 0) return (0, 0);
            if (t <= Looks[0].t) return (Looks[0].yaw, Looks[0].pitch);
            for (int i = 0; i + 1 < Looks.Count; i++)
                if (t < Looks[i + 1].t)
                {
                    float u = (t - Looks[i].t) / (Looks[i + 1].t - Looks[i].t);
                    u = u * u * (3 - 2 * u); // ease between keys
                    float dy = Looks[i + 1].yaw - Looks[i].yaw;
                    return (Looks[i].yaw + dy * u, Looks[i].pitch + (Looks[i + 1].pitch - Looks[i].pitch) * u);
                }
            return (Looks[^1].yaw, Looks[^1].pitch);
        }

        public int KeysAt(float t) => Keys.LastOrDefault(k => k.t <= t).keys;
        public int SlotAt(float t) => Slots.Count == 0 ? -1 : Slots.LastOrDefault(k => k.t <= t, Slots[0]).slot;

        public (float off, float dist, float height, float tilt) CamAt(float t)
        {
            if (Cams.Count == 0) return (0, 4, 0.3f, 0);
            if (t <= Cams[0].t) return (Cams[0].off, Cams[0].dist, Cams[0].height, Cams[0].tilt);
            for (int i = 0; i + 1 < Cams.Count; i++)
                if (t < Cams[i + 1].t)
                {
                    float u = (t - Cams[i].t) / (Cams[i + 1].t - Cams[i].t);
                    u = u * u * (3 - 2 * u);
                    var a = Cams[i];
                    var b = Cams[i + 1];
                    return (a.off + (b.off - a.off) * u, a.dist + (b.dist - a.dist) * u, a.height + (b.height - a.height) * u, a.tilt + (b.tilt - a.tilt) * u);
                }
            return (Cams[^1].off, Cams[^1].dist, Cams[^1].height, Cams[^1].tilt);
        }
    }

    public readonly List<Segment> Segments = new();

    public static Gameplay Load(string path)
    {
        var g = new Gameplay();
        float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "seg")
            {
                g.Segments.Add(new Segment { Seconds = F(parts[1]), Third = parts.Contains("view=third") });
                continue;
            }
            var seg = g.Segments[^1];
            if (parts[0] == "cmd") { seg.Commands.Add(line[4..].Trim()); continue; }
            if (parts[0] != "at") throw new FormatException($"Unknown line: {line}");
            float t = F(parts[1]);
            switch (parts[2])
            {
                case "look": seg.Looks.Add((t, F(parts[3]), F(parts[4]))); break;
                case "slot": seg.Slots.Add((t, int.Parse(parts[3]))); break;
                case "cam": seg.Cams.Add((t, F(parts[3]), F(parts[4]), F(parts[5]), parts.Length > 6 ? F(parts[6]) : 0)); break;
                case "keys":
                    int k = 0;
                    foreach (var name in parts[3].Split(','))
                        k |= name switch
                        {
                            "forward" => BridgeProtocol.KeyForward, "back" => BridgeProtocol.KeyBack, "left" => BridgeProtocol.KeyLeft,
                            "right" => BridgeProtocol.KeyRight, "jump" => BridgeProtocol.KeyJump, "sneak" => BridgeProtocol.KeySneak,
                            "sprint" => BridgeProtocol.KeySprint, "attack" => BridgeProtocol.KeyAttack, "use" => BridgeProtocol.KeyUse,
                            _ => 0,
                        };
                    seg.Keys.Add((t, k));
                    break;
            }
        }
        // Unwrap yaws so turns take the short way.
        foreach (var s in g.Segments)
            for (int i = 1; i < s.Looks.Count; i++)
            {
                float y = s.Looks[i].yaw, prev = s.Looks[i - 1].yaw;
                while (y - prev > 180) y -= 360;
                while (y - prev < -180) y += 360;
                s.Looks[i] = (s.Looks[i].t, y, s.Looks[i].pitch);
            }
        return g;
    }
}
