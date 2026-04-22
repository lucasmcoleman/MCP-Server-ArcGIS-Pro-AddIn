using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
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

namespace APBridgeAddIn
{
    internal class Module1 : Module
    {
        private static Module1 _this = null;
        private ProBridgeService _service;

        public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("APBridgeAddIn_Module");

        /// <summary>
        /// The shared bridge service instance. Button1 can access this
        /// to avoid creating a duplicate.
        /// </summary>
        internal ProBridgeService BridgeService => _service;

        protected override bool Initialize()
        {
            _service = new ProBridgeService("ArcGisProBridgePipe");
            _service.Start();
            return base.Initialize();
        }

        protected override void Uninitialize()
        {
            _service?.Dispose();
            _service = null;
            base.Uninitialize();
        }

        protected override bool CanUnload() => true;
    }
}
