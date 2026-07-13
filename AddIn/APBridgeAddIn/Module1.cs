using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Events;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace APBridgeAddIn
{
    internal class Module1 : Module
    {
        private static Module1 _this = null;
        private ProBridgeService _service;
        private int _pid;
        private string _pipeName = "";

        public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("APBridgeAddIn_Module");

        /// <summary>
        /// The shared bridge service instance. Button1 can access this
        /// to avoid creating a duplicate.
        /// </summary>
        internal ProBridgeService BridgeService => _service;

        /// <summary>This Pro process's PID — matches the registry filename and the pipe-name suffix.</summary>
        internal int Pid => _pid;

        /// <summary>The named pipe the bridge listens on ("ArcGisProBridge_&lt;PID&gt;").</summary>
        internal string PipeName => _pipeName;

        /// <summary>Path to this instance's discovery-registry JSON file.</summary>
        internal string RegistryFilePath => BridgeRegistry.FilePath(_pid);

        /// <summary>Best-effort liveness check — see ProBridgeService.IsRunning.</summary>
        internal bool IsServiceAlive => _service != null && _service.IsRunning;

        /// <summary>
        /// Disposes the current service (if any) and starts a fresh one on the
        /// SAME pipe name, then re-registers with the discovery registry.
        /// Used by the ribbon status button's manual-recovery path — the
        /// bridge auto-starts on module load, but a wedged/dead listener has
        /// no other in-Pro way to recover short of restarting Pro itself.
        /// </summary>
        internal (bool Success, string? Error) RestartBridgeService()
        {
            try { _service?.Dispose(); } catch { /* best-effort — proceed to recreate regardless */ }

            try
            {
                _service = new ProBridgeService(_pipeName);
                _service.Start();
                var (path, name) = TryGetProjectInfo();
                BridgeRegistry.Register(_pid, _pipeName, path, name);
                return (true, null);
            }
            catch (Exception ex)
            {
                _service = null;
                return (false, ex.Message);
            }
        }

        protected override bool Initialize()
        {
            // Pipe name is per-Pro-instance (PID-suffixed) so multiple
            // Pro instances can each have their own bridge instead of
            // racing for the single legacy "ArcGisProBridgePipe" name.
            // The MCP server discovers which pipe to talk to via the
            // BridgeRegistry directory.
            _pid = Process.GetCurrentProcess().Id;
            _pipeName = $"ArcGisProBridge_{_pid}";

            _service = new ProBridgeService(_pipeName);
            _service.Start();

            // Capture project info if a project is already loaded (e.g.,
            // Pro launched with an .aprx). For projects opened later,
            // ProjectOpenedAsyncEvent updates the registry.
            var (path, name) = TryGetProjectInfo();
            BridgeRegistry.Register(_pid, _pipeName, path, name);

            ProjectOpenedAsyncEvent.Subscribe(OnProjectOpened);
            ProjectClosedEvent.Subscribe(OnProjectClosed);

            return base.Initialize();
        }

        protected override void Uninitialize()
        {
            try { ProjectOpenedAsyncEvent.Unsubscribe(OnProjectOpened); } catch { }
            try { ProjectClosedEvent.Unsubscribe(OnProjectClosed); } catch { }

            _service?.Dispose();
            _service = null;

            BridgeRegistry.Unregister(_pid);

            base.Uninitialize();
        }

        protected override bool CanUnload() => true;

        private static (string? path, string? name) TryGetProjectInfo()
        {
            try
            {
                var p = Project.Current;
                if (p == null) return (null, null);
                return (p.URI, p.Name);
            }
            catch { return (null, null); }
        }

        private Task OnProjectOpened(ProjectEventArgs args)
        {
            BridgeRegistry.UpdateProject(_pid, args.Project?.URI, args.Project?.Name);
            return Task.CompletedTask;
        }

        private void OnProjectClosed(ProjectEventArgs args)
        {
            BridgeRegistry.UpdateProject(_pid, null, null);
        }
    }
}
