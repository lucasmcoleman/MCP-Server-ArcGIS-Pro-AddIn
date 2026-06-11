using APBridgeAddIn.ModelBuilder;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    /// <summary>
    /// GP tool discovery family — backed by SystemToolboxCatalog (parses Pro's
    /// installed system toolbox metadata, no SDK introspection API needed).
    /// Lets the agent self-serve any system tool's exact positional signature,
    /// parameter types, domains, and defaults before calling run_gp_tool —
    /// instead of guessing from training data.
    /// </summary>
    internal partial class ProBridgeService
    {
        /// <summary>pro.describeGpTool — full schema for 'alias.ToolName'.</summary>
        private static IpcResponse HandleDescribeGpTool(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("tool", out string? tool) ||
                string.IsNullOrWhiteSpace(tool))
                return new(false, "arg 'tool' required (e.g. 'analysis.Buffer')", null);

            var schema = SystemToolboxCatalog.GetSchema(tool);
            if (schema == null)
            {
                var hint = tool.Contains('.')
                    ? "Check the alias and spelling with search_gp_tools. Custom script tools aren't in the system catalog — " +
                      "use execute_python with arcpy.Usage('<toolname>') for those."
                    : "Format is 'alias.ToolName', e.g. 'analysis.Buffer' or 'management.AddField'.";
                return new(false, $"Tool '{tool}' not found in the system toolbox catalog. {hint}", null);
            }

            return new(true, null, new
            {
                tool = $"{schema.Alias}.{schema.ToolName}",
                displayName = schema.DisplayName,
                description = schema.Description,
                // Positional order = this array's order (run_gp_tool's parameters
                // array maps 1:1; pass "#" to skip an optional slot).
                parameters = schema.Params.Select((p, i) => new
                {
                    position = i,
                    name = p.Name,
                    displayName = p.DisplayName,
                    dataType = p.DataType,
                    compositeTypes = p.CompositeTypes,
                    direction = p.IsOutput ? "out" : "in",
                    optional = p.Optional,
                    derived = p.Derived,
                    defaultValue = p.DefaultValue,
                    allowedValues = p.DomainValues,
                    dependsOn = p.Depends,
                    description = p.Description
                }).ToList()
            });
        }

        /// <summary>pro.searchGpTools — keyword search over ~1700 system tools.</summary>
        private static IpcResponse HandleSearchGpTools(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("keyword", out string? keyword) ||
                string.IsNullOrWhiteSpace(keyword))
                return new(false, "arg 'keyword' required (matched against tool names, e.g. 'buffer', 'project', 'erase')", null);

            int limit = 25;
            if (args.TryGetValue("limit", out string? limitStr) &&
                int.TryParse(limitStr, out int parsed) && parsed > 0)
                limit = System.Math.Min(parsed, 100);

            var results = SystemToolboxCatalog.SearchTools(keyword, limit);
            if (results.Count == 0)
                return new(true, null, new
                {
                    matches = new List<object>(),
                    hint = "No system tool name contains that keyword. Try a shorter fragment " +
                           "('clip' not 'clipping'), or the tool may be a custom/script tool."
                });

            return new(true, null, new
            {
                matches = results.Select(r => new { tool = r.ToolId, toolbox = r.Toolbox }).ToList(),
                hint = "Use describe_gp_tool on a match to get its exact parameter signature."
            });
        }
    }
}
