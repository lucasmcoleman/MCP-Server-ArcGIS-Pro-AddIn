using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcGisMcpServer.Ipc
{
    /// <summary>
    /// Thrown when ARCGIS_PROJECT is set but no live bridge has that project
    /// open. Pinning is strict by design: a pinned MCP server must never
    /// silently route to a different Pro instance, because in multi-agent
    /// setups that would mean one agent editing another agent's project.
    /// BridgeClient catches this and converts it to a structured response.
    /// </summary>
    public sealed class BridgePinException : Exception
    {
        public BridgePinException(string pin, IReadOnlyList<BridgeDiscovery.BridgeEntry> live)
            : base(BuildMessage(pin, live)) { }

        private static string BuildMessage(string pin, IReadOnlyList<BridgeDiscovery.BridgeEntry> live)
        {
            var available = live.Count == 0
                ? "none (no ArcGIS Pro instance with the bridge Add-In is running)"
                : string.Join(", ", live.Select(e => $"'{e.ProjectName ?? "<no project>"}' (pid {e.Pid})"));
            return $"ARCGIS_PROJECT is pinned to '{pin}' but no live ArcGIS Pro instance has " +
                   $"that project open. Live bridges: {available}.";
        }
    }

    /// <summary>
    /// Discovers active ArcGIS Pro bridge processes by reading per-PID
    /// JSON files from %LOCALAPPDATA%\ArcGisMcpBridge\. Each file describes
    /// one bridge (its pipe name + the project it's currently bound to).
    /// Stale entries (dead PIDs) are silently cleaned up.
    ///
    /// Selection logic:
    ///   1. If ARCGIS_PROJECT env var is set, the pin is STRICT: route only to
    ///      a bridge whose project matches (case-insensitive; '.aprx' suffix
    ///      and full paths are tolerated on either side). No match — throw
    ///      BridgePinException rather than fall back, so a pinned agent can
    ///      never drive the wrong Pro instance. The caller's retry loop keeps
    ///      re-resolving, which covers the window where Pro is still loading
    ///      the project.
    ///   2. Otherwise prefer the most-recently-started bridge (latest startedUtc).
    ///   3. Unpinned with no live entries: fall back to the legacy hard-coded
    ///      "ArcGisProBridgePipe" name (preserves single-Pro setups that
    ///      haven't yet rebuilt the new Add-In).
    /// </summary>
    public static class BridgeDiscovery
    {
        private const string LegacyPipeName = "ArcGisProBridgePipe";

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGisMcpBridge");

        public static string? PinnedProject =>
            Environment.GetEnvironmentVariable("ARCGIS_PROJECT") is { } p
            && !string.IsNullOrWhiteSpace(p) ? p : null;

        public static string Discover()
        {
            var entries = ReadAllLive();
            var pin = PinnedProject;

            if (pin != null)
            {
                var match = SelectPinned(entries, pin);
                if (match == null) throw new BridgePinException(pin, entries);
                Console.Error.WriteLine($"[BridgeDiscovery] ARCGIS_PROJECT='{pin}' matched bridge pid={match.Pid} pipe={match.PipeName}.");
                return match.PipeName;
            }

            if (entries.Count == 0)
            {
                Console.Error.WriteLine($"[BridgeDiscovery] No live bridge entries; falling back to legacy pipe '{LegacyPipeName}'.");
                return LegacyPipeName;
            }

            var pick = entries.OrderByDescending(e => e.StartedUtc).First();
            if (entries.Count > 1)
                Console.Error.WriteLine($"[BridgeDiscovery] {entries.Count} live bridges; selected most recent: pid={pick.Pid} project={pick.ProjectName ?? "<none>"} pipe={pick.PipeName}.");
            else
                Console.Error.WriteLine($"[BridgeDiscovery] Selected bridge pid={pick.Pid} project={pick.ProjectName ?? "<none>"} pipe={pick.PipeName}.");
            return pick.PipeName;
        }

        /// <summary>
        /// The entry Discover() would route to right now, or null (pinned with
        /// no match, or nothing live). Shared so list_bridges reports the same
        /// answer the next real request will act on.
        /// </summary>
        public static BridgeEntry? SelectCurrent(IReadOnlyList<BridgeEntry> entries)
        {
            var pin = PinnedProject;
            if (pin != null) return SelectPinned(entries, pin);
            return entries.OrderByDescending(e => e.StartedUtc).FirstOrDefault();
        }

        private static BridgeEntry? SelectPinned(IReadOnlyList<BridgeEntry> entries, string pin)
            => entries
                .Where(e => MatchesPin(e, pin))
                .OrderByDescending(e => e.StartedUtc)
                .FirstOrDefault();

        /// <summary>
        /// Tolerant project match: the pin may be a bare name, a name with
        /// '.aprx', or a full path; the registry side may store any of those
        /// too. Everything is reduced to the extensionless file/project name
        /// before a case-insensitive compare.
        /// </summary>
        private static bool MatchesPin(BridgeEntry e, string pin)
        {
            var want = Normalize(pin);
            if (want.Length == 0) return false;
            return Normalize(e.ProjectName) == want || Normalize(e.ProjectPath) == want;
        }

        private static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var name = value.Trim();
            try { name = Path.GetFileName(name.TrimEnd('\\', '/')); } catch { /* invalid path chars — use as-is */ }
            if (name.EndsWith(".aprx", StringComparison.OrdinalIgnoreCase))
                name = name[..^5];
            return name.ToUpperInvariant();
        }

        public static List<BridgeEntry> ReadAllLive()
        {
            var live = new List<BridgeEntry>();
            if (!Directory.Exists(Dir)) return live;

            foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                BridgeEntry? entry = null;
                try { entry = JsonSerializer.Deserialize(File.ReadAllText(file), McpJsonContext.Default.BridgeEntry); }
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
