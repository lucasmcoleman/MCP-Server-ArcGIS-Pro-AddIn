using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcGisMcpServer.Ipc
{
    /// <summary>
    /// Discovers active ArcGIS Pro bridge processes by reading per-PID
    /// JSON files from %LOCALAPPDATA%\ArcGisMcpBridge\. Each file describes
    /// one bridge (its pipe name + the project it's currently bound to).
    /// Stale entries (dead PIDs) are silently cleaned up.
    ///
    /// Selection logic:
    ///   1. If ARCGIS_PROJECT env var is set, prefer entries whose projectName
    ///      matches (case-insensitive).
    ///   2. Otherwise prefer the most-recently-started bridge (latest startedUtc).
    ///   3. If no live entries exist, fall back to the legacy hard-coded
    ///      "ArcGisProBridgePipe" name (preserves single-Pro setups that
    ///      haven't yet rebuilt the new Add-In).
    /// </summary>
    public static class BridgeDiscovery
    {
        private const string LegacyPipeName = "ArcGisProBridgePipe";

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGisMcpBridge");

        public static string Discover()
        {
            var entries = ReadAllLive();
            if (entries.Count == 0)
            {
                Console.Error.WriteLine($"[BridgeDiscovery] No live bridge entries; falling back to legacy pipe '{LegacyPipeName}'.");
                return LegacyPipeName;
            }

            var preferredProject = Environment.GetEnvironmentVariable("ARCGIS_PROJECT");
            if (!string.IsNullOrWhiteSpace(preferredProject))
            {
                var match = entries
                    .Where(e => string.Equals(e.ProjectName, preferredProject, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.StartedUtc)
                    .FirstOrDefault();
                if (match != null)
                {
                    Console.Error.WriteLine($"[BridgeDiscovery] ARCGIS_PROJECT='{preferredProject}' matched bridge pid={match.Pid} pipe={match.PipeName}.");
                    return match.PipeName;
                }
                Console.Error.WriteLine($"[BridgeDiscovery] ARCGIS_PROJECT='{preferredProject}' matched no live bridge; using most recent.");
            }

            var pick = entries.OrderByDescending(e => e.StartedUtc).First();
            if (entries.Count > 1)
                Console.Error.WriteLine($"[BridgeDiscovery] {entries.Count} live bridges; selected most recent: pid={pick.Pid} project={pick.ProjectName ?? "<none>"} pipe={pick.PipeName}.");
            else
                Console.Error.WriteLine($"[BridgeDiscovery] Selected bridge pid={pick.Pid} project={pick.ProjectName ?? "<none>"} pipe={pick.PipeName}.");
            return pick.PipeName;
        }

        private static List<BridgeEntry> ReadAllLive()
        {
            var live = new List<BridgeEntry>();
            if (!Directory.Exists(Dir)) return live;

            foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                BridgeEntry? entry = null;
                try { entry = JsonSerializer.Deserialize<BridgeEntry>(File.ReadAllText(file)); }
                catch { /* corrupt file; skip */ }
                if (entry == null || string.IsNullOrWhiteSpace(entry.PipeName)) continue;

                if (IsAlive(entry.Pid))
                {
                    live.Add(entry);
                }
                else
                {
                    // Stale — clean up so the directory doesn't grow unbounded
                    try { File.Delete(file); } catch { /* best effort */ }
                }
            }
            return live;
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (ArgumentException) { return false; } // PID not found
            catch { return false; }
        }

        public class BridgeEntry
        {
            [JsonPropertyName("pid")] public int Pid { get; set; }
            [JsonPropertyName("pipeName")] public string PipeName { get; set; } = "";
            [JsonPropertyName("projectPath")] public string? ProjectPath { get; set; }
            [JsonPropertyName("projectName")] public string? ProjectName { get; set; }
            [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
        }
    }
}
