using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Extensions;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.KnowledgeGraph;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
// MessageBoxButton/MessageBoxImage/MessageBoxResult live in System.Windows —
// referenced fully-qualified below rather than via `using System.Windows;`
// because that namespace ALSO defines a MessageBox class, which would
// collide with ArcGIS.Desktop.Framework.Dialogs.MessageBox (used here).

namespace APBridgeAddIn
{
    internal class Button1 : Button
    {
        protected override void OnClick()
        {
            // Report the bridge's ACTUAL state (pipe name, PID, registry
            // file, and whether the listener is alive) instead of the old
            // hardcoded "MCP Bridge is running" claim — that message showed
            // even when the service had died. Offer a manual restart either
            // way: dead needs it to recover without closing Pro; alive lets
            // the user force one anyway if something looks wrong upstream.
            var mod = Module1.Current;
            bool alive = mod.IsServiceAlive;

            var status =
                $"Pipe name:      {mod.PipeName}\n" +
                $"Process ID:     {mod.Pid}\n" +
                $"Registry file:  {mod.RegistryFilePath}\n" +
                $"Server status:  {(alive ? "ALIVE (listener running)" : "NOT RUNNING")}";

            var prompt = alive
                ? "MCP Bridge status:\n\n" + status + "\n\nRestart the named-pipe server anyway?"
                : "MCP Bridge status:\n\n" + status +
                  "\n\nThe bridge server is not running — MCP clients can't reach this Pro " +
                  "instance. Restart it now?";

            var result = MessageBox.Show(prompt, "MCP Bridge Status",
                System.Windows.MessageBoxButton.YesNo,
                alive ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes)
                return;

            var (success, error) = mod.RestartBridgeService();
            MessageBox.Show(
                success
                    ? $"Bridge server restarted on pipe '{mod.PipeName}' (PID {mod.Pid})."
                    : $"Failed to restart the bridge server: {error}",
                "MCP Bridge Status",
                System.Windows.MessageBoxButton.OK,
                success ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Error);
        }
    }
}
