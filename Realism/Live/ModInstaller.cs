using System.Text.Json;

namespace Realism.Live;

/// <summary>
/// Player builds (BuildRelease.bat) ship the bridge mod and Fabric API in a mods folder next to RealismRenderer.exe.
/// This copies them into the mods folder of every Fabric 1.21.5 profile in the Minecraft launcher (asking first), so a
/// player only needs Fabric installed and this app. Profiles can load mods from elsewhere than .minecraft\mods: the
/// Iris installer's use -Dfabric.modsFolder, and custom profiles have their own game directory.
/// </summary>
public static class ModInstaller
{
    const string McVersion = "1.21.5";

    public static void Run()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "mods");
        var mod = Directory.Exists(bundled) ? Directory.GetFiles(bundled, "realism-renderer*.jar").FirstOrDefault() : null;
        var api = Directory.Exists(bundled) ? Directory.GetFiles(bundled, "fabric-api*.jar").FirstOrDefault() : null;
        if (mod == null) return; // a developer build: the mod comes from MinecraftMod\
        string mc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        var folders = FabricModsFolders(mc);
        if (folders.Count == 0)
        {
            Console.WriteLine($"No Fabric {McVersion} profile found in the Minecraft launcher. Install Fabric Loader for Minecraft " +
                              $"{McVersion} (https://fabricmc.net/use/installer/), then start Realism Renderer again.");
            return;
        }
        var modBytes = File.ReadAllBytes(mod);
        foreach (var (profile, folder) in folders)
        {
            var old = Directory.Exists(folder) ? Directory.GetFiles(folder, "realism-renderer*.jar") : [];
            bool upToDate = old.Any(f => File.ReadAllBytes(f).AsSpan().SequenceEqual(modBytes));
            bool hasApi = Directory.Exists(folder) && Directory.GetFiles(folder, "fabric-api*.jar").Length > 0;
            if (upToDate && hasApi) continue;
            string what = upToDate ? "Fabric API" : hasApi || api == null ? "the Realism Renderer mod" : "the Realism Renderer mod and Fabric API";
            Console.Write($"Install {what} for the \"{profile}\" profile (into {folder})? [Y/n] ");
            var answer = Console.ReadLine();
            if (answer == null || answer.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.CreateDirectory(folder);
                if (!upToDate)
                {
                    foreach (var f in old) File.Delete(f); // an older version of this mod
                    File.Copy(mod, Path.Combine(folder, Path.GetFileName(mod)));
                }
                if (!hasApi && api != null) File.Copy(api, Path.Combine(folder, Path.GetFileName(api)));
                Console.WriteLine("  Installed. If Minecraft is open, restart it.");
            }
            catch (IOException e) { Console.WriteLine($"  Couldn't install ({e.Message}). Close Minecraft and start Realism Renderer again."); }
            catch (UnauthorizedAccessException e) { Console.WriteLine($"  Couldn't install ({e.Message})."); }
        }
    }

    /// <summary>Each launcher profile that runs Fabric on this Minecraft version, with the folder its mods load from.</summary>
    static List<(string profile, string folder)> FabricModsFolders(string mc)
    {
        var found = new List<(string, string)>();
        // The Microsoft Store launcher keeps its own profile list in the same folder.
        foreach (var file in new[] { "launcher_profiles.json", "launcher_profiles_microsoft_store.json" })
        {
            JsonElement profiles;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(mc, file)));
                if (!doc.RootElement.TryGetProperty("profiles", out profiles)) continue;
                profiles = profiles.Clone();
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { continue; }
            foreach (var p in profiles.EnumerateObject())
            {
                string? id = Str(p.Value, "lastVersionId");
                if (id == null) continue;
                var versionArgs = FabricVersionJvmArgs(mc, id);
                if (versionArgs == null) continue;
                string gameDir = Str(p.Value, "gameDir") is { Length: > 0 } g ? g : mc;
                // The profile's own Java arguments come after the version's, so they win.
                string folder = ModsFolderArg(Str(p.Value, "javaArgs") ?? "")
                                ?? versionArgs.Where(a => a.StartsWith(ModsFolderKey)).Select(a => a[ModsFolderKey.Length..].Trim('"')).LastOrDefault()
                                ?? Path.Combine(gameDir, "mods");
                if (found.Any(f => string.Equals(Path.GetFullPath(f.Item2), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))) continue;
                found.Add((Str(p.Value, "name") is { Length: > 0 } n ? n : id, folder));
            }
        }
        return found;
    }

    /// <summary>The version's own JVM arguments if it is Fabric on <see cref="McVersion"/>; otherwise null.</summary>
    static List<string>? FabricVersionJvmArgs(string mc, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(mc, "versions", id, id + ".json")));
            var root = doc.RootElement;
            if (Str(root, "inheritsFrom") != McVersion || Str(root, "mainClass")?.StartsWith("net.fabricmc.") != true) return null;
            if (!root.TryGetProperty("arguments", out var a) || !a.TryGetProperty("jvm", out var jvm)) return [];
            return jvm.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    const string ModsFolderKey = "-Dfabric.modsFolder=";

    /// <summary>The mods folder set in a profile's Java arguments (one string, as typed in the launcher), if any.</summary>
    static string? ModsFolderArg(string args)
    {
        int i = args.LastIndexOf(ModsFolderKey, StringComparison.Ordinal);
        if (i < 0) return null;
        int start = i + ModsFolderKey.Length;
        // A quoted path can hold spaces; otherwise it runs to the next space.
        if (start < args.Length && args[start] == '"') return args[(start + 1)..].Split('"')[0];
        return args[start..].Split(' ')[0];
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
