using ArcGIS.Desktop.Core.Geoprocessing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    /// <summary>
    /// pro.executePython — the arcpy escape hatch. Runs arbitrary Python IN-PROCESS
    /// in Pro's embedded CPython, so arcpy.mp.ArcGISProject('CURRENT') manipulates
    /// the live open project — exposing the entire arcpy surface (mp/CIM, da
    /// cursors, every module) that the C# bridge doesn't wrap.
    ///
    /// CHANNEL: management.CalculateValue (a SYSTEM tool) with the user code
    /// base64-embedded in its code_block. Empirically verified 2026-06-11:
    ///   - ExecuteToolAsync with an out-of-project .pyt path ("...bridge.pyt\Tool")
    ///     hangs FOREVER in-proc (works fine in standalone propy) — so the
    ///     deployed-Python-toolbox design is a dead end here.
    ///   - CalculateValue resolves like any system tool and runs the embedded
    ///     Python with full CURRENT access in ~0s once Python is warm.
    ///
    /// WARM-UP GATE: a Python-touching GP call issued in the first minutes after
    /// Pro launch can wedge the GP Python lane permanently (every later Python
    /// call queues forever; native tools unaffected). Until Pro's Python has
    /// warmed, executePython returns a clean "retry in N seconds" error instead
    /// of risking the wedge. First successful call flips the gate off for the
    /// rest of the session.
    /// </summary>
    internal partial class ProBridgeService
    {
        private const int PythonWarmupSeconds = 180;

        // Once any Python call succeeds, the lane is warm — no more gating.
        private static volatile bool _pythonProven;

        private static async Task<IpcResponse> HandleExecutePython(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("code", out string? code) ||
                string.IsNullOrWhiteSpace(code))
                return new(false, "arg 'code' required (Python source; set a variable named 'result' to return a value)", null);

            if (!_pythonProven)
            {
                double upSeconds;
                try { upSeconds = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds; }
                catch { upSeconds = double.MaxValue; }
                if (upSeconds < PythonWarmupSeconds)
                {
                    var wait = (int)Math.Ceiling(PythonWarmupSeconds - upSeconds);
                    return new(false,
                        $"Pro's Python environment is still warming up after launch ({(int)upSeconds}s of " +
                        $"{PythonWarmupSeconds}s). Calling Python too early can permanently wedge geoprocessing " +
                        $"for this Pro session, so this call was refused. Retry in ~{wait} seconds.",
                        null);
                }
            }

            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));

            // The wrapper returns its payload through CalculateValue's derived
            // output. Base64 in, base64 out — survives GP value parsing (no ';'
            // splitting, no newline mangling) in both directions.
            var codeBlock =
                "import base64, contextlib, io, json, traceback\n" +
                "def mcp_exec():\n" +
                "    out = {'ok': True}\n" +
                "    buf = io.StringIO()\n" +
                "    try:\n" +
                "        import arcpy\n" +
                "        ns = {'arcpy': arcpy}\n" +
                $"        code = base64.b64decode('{b64}').decode('utf-8')\n" +
                "        with contextlib.redirect_stdout(buf):\n" +
                "            exec(compile(code, '<mcp>', 'exec'), ns)\n" +
                "        r = ns.get('result')\n" +
                "        if r is not None:\n" +
                "            try:\n" +
                "                json.dumps(r)\n" +
                "            except Exception:\n" +
                "                r = repr(r)\n" +
                "        out['result'] = r\n" +
                "    except Exception:\n" +
                "        out['ok'] = False\n" +
                "        out['error'] = traceback.format_exc()\n" +
                "    out['stdout'] = buf.getvalue()\n" +
                "    return 'MCPRESULT:' + base64.b64encode(json.dumps(out).encode('utf-8')).decode('ascii')\n";

            var valueArray = Geoprocessing.MakeValueArray("mcp_exec()", codeBlock, "String");

            // GPThread alone (not Default): skip AddOutputsToMap + history noise —
            // this is a programmatic channel, not a user-visible GP run.
            var result = await Geoprocessing.ExecuteToolAsync(
                "management.CalculateValue", valueArray, null, null, null, GPExecuteToolFlags.GPThread);

            var gpMessages = result.Messages
                .Select(m => new { type = m.Type.ToString(), text = m.Text })
                .ToList();

            // The MCPRESULT payload lands in the tool's output values (and often
            // in message text); scan values first, then messages as fallback.
            string? payload = null;
            try
            {
                payload = result.Values?
                    .Where(v => v != null && v.Contains("MCPRESULT:", StringComparison.Ordinal))
                    .Select(ExtractPayload)
                    .LastOrDefault(p => p != null);
            }
            catch { }
            payload ??= result.Messages
                .Select(m => m.Text)
                .Where(t => t != null && t.Contains("MCPRESULT:", StringComparison.Ordinal))
                .Select(ExtractPayload)
                .LastOrDefault(p => p != null);

            if (payload == null)
            {
                var msgText = gpMessages.Any()
                    ? string.Join("; ", gpMessages.Select(m => m.text))
                    : "no GP messages";
                return new(false,
                    $"execute_python produced no result payload — the wrapper itself may have failed " +
                    $"to run. GP says: {msgText}", null);
            }

            JsonNode? resultNode;
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                resultNode = JsonNode.Parse(json);
            }
            catch (Exception ex)
            {
                return new(false, $"execute_python: could not parse result payload: {ex.Message}", null);
            }

            _pythonProven = true;

            // ok=false inside the payload means USER code raised — return the
            // traceback as DATA (not an op error): the agent reads it and
            // self-corrects; the channel itself worked.
            return new(true, null, resultNode);
        }

        /// <summary>Pulls the base64 payload following "MCPRESULT:" out of a value/message string.</summary>
        private static string? ExtractPayload(string? text)
        {
            if (text == null) return null;
            var idx = text.IndexOf("MCPRESULT:", StringComparison.Ordinal);
            if (idx < 0) return null;
            var start = idx + "MCPRESULT:".Length;
            var end = start;
            while (end < text.Length &&
                   (char.IsLetterOrDigit(text[end]) || text[end] is '+' or '/' or '='))
                end++;
            return end > start ? text[start..end] : null;
        }
    }
}
