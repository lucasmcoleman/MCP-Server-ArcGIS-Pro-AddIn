using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace APBridgeAddIn
{
    /// <summary>
    /// Per-Pro-instance discovery registry for the MCP bridge. Each Pro
    /// process writes a JSON file at %LOCALAPPDATA%\ArcGisMcpBridge\&lt;PID&gt;.json
    /// describing its pipe name and (when available) the active project.
    /// The MCP server enumerates these files to find which bridge to talk
    /// to, so multiple Pro instances can each have their own MCP-accessible
    /// bridge instead of racing for a single shared pipe name.
    /// </summary>
    internal static class BridgeRegistry
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGisMcpBridge");

        private static readonly JsonSerializerOptions JsonOpts =
            new(JsonSerializerOptions.Default) { WriteIndented = true };

        public static string FilePath(int pid) => Path.Combine(Dir, $"{pid}.json");

        public static void Register(int pid, string pipeName, string? projectPath, string? projectName)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                Write(pid, new BridgeEntry
                {
                    Pid = pid,
                    PipeName = pipeName,
                    ProjectPath = projectPath,
                    ProjectName = projectName,
                    StartedUtc = DateTime.UtcNow.ToString("O")
                });
            }
            catch { /* best effort — never break the bridge to maintain discovery */ }
        }

        public static void UpdateProject(int pid, string? projectPath, string? projectName)
        {
            try
            {
                var path = FilePath(pid);
                if (!File.Exists(path)) return;
                var entry = JsonSerializer.Deserialize<BridgeEntry>(File.ReadAllText(path));
                if (entry == null) return;
                entry.ProjectPath = projectPath;
                entry.ProjectName = projectName;
                Write(pid, entry);
            }
            catch { /* best effort */ }
        }

        public static void Unregister(int pid)
        {
            try
            {
                var path = FilePath(pid);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
        }

        private static void Write(int pid, BridgeEntry entry)
        {
            File.WriteAllText(FilePath(pid), JsonSerializer.Serialize(entry, JsonOpts));
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
