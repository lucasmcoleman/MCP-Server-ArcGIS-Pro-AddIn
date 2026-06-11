using ArcGIS.Desktop.Core.Geoprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    /// <summary>
    /// pro.executePython — the arcpy escape hatch. Runs arbitrary Python IN-PROCESS
    /// in Pro's embedded CPython via a deployed Python toolbox (bridge.pyt) invoked
    /// through Geoprocessing.ExecuteToolAsync. Because it's in-process,
    /// arcpy.mp.ArcGISProject('CURRENT') manipulates the live open project —
    /// exposing the entire arcpy surface (mp/CIM, da cursors, every module) that
    /// the C# bridge doesn't wrap. This is Esri's sanctioned channel for C# → Python
    /// (no official PythonWindow automation API exists).
    ///
    /// Contract: the code runs in a fresh namespace with `arcpy` pre-imported.
    /// stdout (print) is captured via redirect_stdout; setting a variable named
    /// `result` returns it (JSON-serialized when possible, repr() otherwise).
    /// Exceptions return ok=false with the full traceback. The code parameter is
    /// base64-encoded in transit because GP's value parsing treats ';' as a
    /// multivalue separator and mangles raw newlines.
    /// </summary>
    internal partial class ProBridgeService
    {
        // Bump when PytSource changes — the deployed file is refreshed when the
        // on-disk copy doesn't contain the current version marker.
        private const string PytVersion = "v3";

        private static readonly string PytDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGisMcpBridge");

        private static readonly string PytPath = Path.Combine(PytDir, "bridge.pyt");

        private const string PytSource = @"# -*- coding: utf-8 -*-
# MCP bridge Python toolbox — " + PytVersion + @"
# Deployed automatically by APBridgeAddIn; safe to delete (it is re-created).
import arcpy, base64, contextlib, io, json, traceback


class Toolbox(object):
    def __init__(self):
        self.label = 'MCP Bridge Python Toolbox'
        self.alias = 'mcpbridge'
        self.tools = [ExecutePython]


class ExecutePython(object):
    def __init__(self):
        self.label = 'ExecutePython'
        self.description = 'Executes base64-encoded Python for the MCP bridge'
        self.canRunInBackground = False

    def getParameterInfo(self):
        code = arcpy.Parameter(
            displayName='Code (base64)', name='code_b64', datatype='GPString',
            parameterType='Required', direction='Input')
        result = arcpy.Parameter(
            displayName='Result JSON', name='result_json', datatype='GPString',
            parameterType='Derived', direction='Output')
        return [code, result]

    def execute(self, parameters, messages):
        out = {'ok': True}
        buf = io.StringIO()
        try:
            code = base64.b64decode(parameters[0].valueAsText).decode('utf-8')
            ns = {'arcpy': arcpy}
            with contextlib.redirect_stdout(buf):
                exec(compile(code, '<mcp>', 'exec'), ns)
            r = ns.get('result')
            if r is not None:
                try:
                    json.dumps(r)
                except Exception:
                    r = repr(r)
            out['result'] = r
        except Exception:
            out['ok'] = False
            out['error'] = traceback.format_exc()
        out['stdout'] = buf.getvalue()
        # The derived output param is the reliable return channel — GP message
        # routing of print() output is host-dependent.
        parameters[1].value = 'MCPRESULT:' + base64.b64encode(
            json.dumps(out).encode('utf-8')).decode('ascii')
";

        /// <summary>Writes bridge.pyt if missing or stale (version marker mismatch).</summary>
        private static void EnsurePytDeployed()
        {
            Directory.CreateDirectory(PytDir);
            bool fresh = false;
            if (File.Exists(PytPath))
            {
                try { fresh = File.ReadAllText(PytPath).Contains($"— {PytVersion}"); }
                catch { }
            }
            if (!fresh)
                File.WriteAllText(PytPath, PytSource, new UTF8Encoding(false));
        }

        private static async Task<IpcResponse> HandleExecutePython(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("code", out string? code) ||
                string.IsNullOrWhiteSpace(code))
                return new(false, "arg 'code' required (Python source; set a variable named 'result' to return a value)", null);

            try { EnsurePytDeployed(); }
            catch (Exception ex)
            {
                return new(false, $"Failed to deploy bridge.pyt: {ex.Message}", null);
            }

            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
            var toolPath = $"{PytPath}\\ExecutePython";
            var valueArray = Geoprocessing.MakeValueArray(b64);

            // GPThread alone (not Default): skip AddOutputsToMap + history noise —
            // this is a programmatic channel, not a user-visible GP run.
            var result = await Geoprocessing.ExecuteToolAsync(
                toolPath, valueArray, null, null, null, GPExecuteToolFlags.GPThread);

            // Locate the MCPRESULT payload in the derived output values.
            string? payload = null;
            try
            {
                payload = result.Values?
                    .Where(v => v != null && v.StartsWith("MCPRESULT:", StringComparison.Ordinal))
                    .Select(v => v.Substring("MCPRESULT:".Length))
                    .LastOrDefault();
            }
            catch { }

            var gpMessages = result.Messages
                .Select(m => new { type = m.Type.ToString(), text = m.Text })
                .ToList();

            if (payload == null)
            {
                // No payload: either the GP framework failed before execute()
                // (syntax error in the .pyt, license issue) or message routing ate
                // the derived output. Surface everything we have.
                var msgText = gpMessages.Any()
                    ? string.Join("; ", gpMessages.Select(m => m.text))
                    : "no GP messages";
                return new(result.IsFailed ? false : true,
                    result.IsFailed ? $"execute_python failed before code ran: {msgText}" : null,
                    result.IsFailed ? null : new { ok = true, note = "no result payload returned", messages = gpMessages });
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

            // ok=false from the .pyt means user code raised — return the traceback
            // as DATA (not an op error): the agent needs to read it to self-correct,
            // and the op itself worked as designed.
            return new(true, null, resultNode);
        }
    }
}
