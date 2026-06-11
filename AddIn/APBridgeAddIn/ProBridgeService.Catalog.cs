using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    /// <summary>
    /// Catalog family: data discovery OUTSIDE the map TOC. run_model intermediates
    /// and freshly geoprocessed outputs land in GDBs without being added to any
    /// map — these ops let the agent see and describe them without map membership.
    /// </summary>
    internal partial class ProBridgeService
    {
        /// <summary>pro.listGdbContents — feature classes / tables / rasters in a file GDB.</summary>
        private static async Task<IpcResponse> HandleListGdbContents(Dictionary<string, string>? args)
        {
            string? gdbPath = null;
            args?.TryGetValue("gdbPath", out gdbPath);
            if (string.IsNullOrWhiteSpace(gdbPath))
            {
                try { gdbPath = ArcGIS.Desktop.Core.Project.Current?.DefaultGeodatabasePath; }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(gdbPath))
                return new(false, "arg 'gdbPath' required (no project default GDB available)", null);
            if (!Directory.Exists(gdbPath))
                return new(false, $"Geodatabase not found: {gdbPath}", null);

            var resolvedPath = gdbPath!;
            return await QueuedTask.Run<IpcResponse>(() =>
            {
                using var gdb = new Geodatabase(
                    new FileGeodatabaseConnectionPath(new Uri(resolvedPath)));

                var featureClasses = gdb.GetDefinitions<FeatureClassDefinition>()
                    .Select(d =>
                    {
                        using (d)
                        {
                            return new
                            {
                                name = d.GetName(),
                                geometryType = d.GetShapeType().ToString(),
                                srWkid = d.GetSpatialReference()?.Wkid ?? 0
                            };
                        }
                    }).ToList();

                var tables = gdb.GetDefinitions<TableDefinition>()
                    .Select(d => { using (d) return new { name = d.GetName() }; })
                    .ToList();

                List<string> rasters = new();
                try
                {
                    rasters = gdb.GetDefinitions<ArcGIS.Core.Data.Raster.RasterDatasetDefinition>()
                        .Select(d => { using (d) return d.GetName(); })
                        .ToList();
                }
                catch { /* raster definitions unavailable in some GDBs */ }

                List<string> featureDatasets = new();
                try
                {
                    featureDatasets = gdb.GetDefinitions<FeatureDatasetDefinition>()
                        .Select(d => { using (d) return d.GetName(); })
                        .ToList();
                }
                catch { }

                return new(true, null, new
                {
                    gdbPath = resolvedPath,
                    featureClasses,
                    tables,
                    rasters,
                    featureDatasets
                });
            });
        }

        /// <summary>
        /// pro.describeDataset — schema of a dataset by full path
        /// (e.g. 'F:\proj\data.gdb\Wetlands'): fields, geometry, SR, row count.
        /// </summary>
        private static async Task<IpcResponse> HandleDescribeDataset(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("path", out string? path) ||
                string.IsNullOrWhiteSpace(path))
                return new(false, "arg 'path' required (e.g. 'F:/proj/data.gdb/Wetlands')", null);

            // Split <...>.gdb\<dataset> — the dataset may be nested under a
            // feature dataset; OpenDataset takes the bare name either way.
            var norm = path.Replace('/', '\\');
            var idx = norm.IndexOf(".gdb", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return new(false,
                    "Only file-geodatabase paths are supported (path must contain '.gdb'). " +
                    "For shapefiles or other formats use execute_python with arcpy.Describe.", null);
            var gdbPath = norm[..(idx + 4)];
            var datasetName = norm[(idx + 4)..].Trim('\\').Split('\\').LastOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(datasetName))
                return new(false, "Path must include a dataset name after the .gdb", null);
            if (!Directory.Exists(gdbPath))
                return new(false, $"Geodatabase not found: {gdbPath}", null);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                using var gdb = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));

                Table? table = null;
                try { table = gdb.OpenDataset<FeatureClass>(datasetName); }
                catch
                {
                    try { table = gdb.OpenDataset<Table>(datasetName); }
                    catch { }
                }

                if (table == null)
                    return new(false,
                        $"Dataset '{datasetName}' not found in {gdbPath} (or it is not a feature class/table). " +
                        "Use list_gdb_contents to see what's there.", null);

                using (table)
                {
                    var def = table.GetDefinition();
                    var fields = def.GetFields().Select(f => new
                    {
                        name = f.Name,
                        alias = f.AliasName,
                        type = f.FieldType.ToString(),
                        length = f.Length
                    }).ToList();

                    object? geometry = null;
                    if (def is FeatureClassDefinition fcd)
                    {
                        var ext = fcd.GetExtent();
                        geometry = new
                        {
                            geometryType = fcd.GetShapeType().ToString(),
                            srWkid = fcd.GetSpatialReference()?.Wkid ?? 0,
                            srName = fcd.GetSpatialReference()?.Name,
                            extent = ext == null ? null : (object)new
                            {
                                xmin = ext.XMin, ymin = ext.YMin, xmax = ext.XMax, ymax = ext.YMax
                            }
                        };
                    }

                    return new(true, null, new
                    {
                        path = $"{gdbPath}\\{datasetName}",
                        rowCount = table.GetCount(),
                        geometry,
                        fields
                    });
                }
            });
        }
    }
}
