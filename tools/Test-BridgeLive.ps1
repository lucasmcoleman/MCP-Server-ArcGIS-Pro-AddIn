# Test-BridgeLive.ps1 — live smoke battery for the bridge's op surface.
#
# Drives the Add-In directly over the named pipe (no MCP server needed) and
# exercises every capability family added in the 2026-06 deep dive, plus the
# core ops they depend on. Creates its own scratch data (McpEditTest_* FCs, a
# scratch layout + map + bookmark) and cleans up after itself.
#
# Run with ArcGIS Pro open on any project that has a default GDB, AFTER the
# Python warm-up window (~3 min post-launch). Exit code 0 = all pass.

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Bridge = Join-Path $here 'Invoke-BridgeOp.ps1'

$script:pass = 0; $script:fail = 0; $script:failures = @()

function Op([string]$op, [hashtable]$opArgs = $null, [int]$timeout = 240000) {
    $raw = & $Bridge -Op $op -Args $opArgs -ReadTimeoutMs $timeout 2>$null
    if (-not $raw) { return $null }
    try { return $raw | ConvertFrom-Json } catch { return $null }
}

function Check([bool]$cond, [string]$name, [string]$detail = '') {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        $script:fail++; $script:failures += $name
        Write-Host "  FAIL  $name $detail" -ForegroundColor Red
    }
}

Write-Host "== sanity =="
$info = Op pro.getProjectInfo
Check ($null -ne $info -and $info.ok) 'getProjectInfo' ($info | ConvertTo-Json -Compress -Depth 3)
$gdb = $info.data.defaultGeodatabase
$homeFolder = $info.data.homeFolder
$activeMap = $info.data.activeMap.name

Write-Host "== GP discovery =="
$desc = Op pro.describeGpTool @{ tool = 'analysis.Buffer' }
Check ($desc.ok -and $desc.data.parameters[0].name -eq 'in_features' -and $desc.data.parameters[7].name -eq 'method') 'describe_gp_tool Buffer positional order'
$search = Op pro.searchGpTools @{ keyword = 'PairwiseClip' }
Check ($search.ok -and $search.data.matches.Count -ge 1) 'search_gp_tools'

Write-Host "== catalog =="
$contents = Op pro.listGdbContents
Check ($contents.ok -and $null -ne $contents.data.featureClasses) 'list_gdb_contents (default gdb)'
if ($contents.data.featureClasses.Count -gt 0) {
    $fcName = $contents.data.featureClasses[0].name
    $dd = Op pro.describeDataset @{ path = "$gdb\$fcName" }
    Check ($dd.ok -and $dd.data.fields.Count -gt 0) "describe_dataset $fcName"
}

Write-Host "== execute_python (CalculateValue channel) =="
$py = Op pro.executePython @{ code = @"
import arcpy
aprx = arcpy.mp.ArcGISProject('CURRENT')
print('stdout ok')
result = {'maps': [m.name for m in aprx.listMaps()], 'version': arcpy.GetInstallInfo()['Version']}
"@ }
Check ($py.ok -and $py.data.ok -and $py.data.stdout -match 'stdout ok' -and $py.data.result.maps.Count -ge 1) 'execute_python CURRENT access + stdout' ($py | ConvertTo-Json -Compress -Depth 4)

$pyErr = Op pro.executePython @{ code = 'raise ValueError("intentional")' }
Check ($pyErr.ok -and -not $pyErr.data.ok -and $pyErr.data.error -match 'intentional') 'execute_python traceback surfaced'

Write-Host "== scratch data via python =="
$mk = Op pro.executePython @{ code = @"
import arcpy
gdb = r'$gdb'
for name, gtype in [('McpEditTestPt', 'POINT'), ('McpEditTestLn', 'POLYLINE')]:
    if arcpy.Exists(gdb + '\\' + name):
        arcpy.management.Delete(gdb + '\\' + name)
    arcpy.management.CreateFeatureclass(gdb, name, gtype, spatial_reference=arcpy.SpatialReference(4326))
    arcpy.management.AddField(gdb + '\\' + name, 'NAME', 'TEXT', field_length=64)
result = 'created'
"@ }
Check ($mk.ok -and $mk.data.ok -and $mk.data.result -eq 'created') 'scratch FCs created via python' ($mk | ConvertTo-Json -Compress -Depth 4)

$addPt = Op pro.addLayerFromFile @{ path = "$gdb\McpEditTestPt" }
Check ($addPt.ok) 'add_layer_from_file (point FC)'
$addLn = Op pro.addLayerFromFile @{ path = "$gdb\McpEditTestLn" }
Check ($addLn.ok) 'add_layer_from_file (line FC)'

Write-Host "== editing CRUD =="
$ins = Op pro.addPointFeatures @{ layer = 'McpEditTestPt'; features = '[{"x":-115.1,"y":36.1,"attributes":{"NAME":"alpha"}},{"x":-115.2,"y":36.2,"attributes":{"NAME":"beta"}}]' }
Check ($ins.ok -and $ins.data.added -eq 2) 'add_point_features x2'

$insLn = Op pro.addPolylineFeatures @{ layer = 'McpEditTestLn'; features = '[{"vertices":[[-115.1,36.1],[-115.2,36.2],[-115.3,36.15]],"attributes":{"NAME":"route"}}]' }
Check ($insLn.ok -and $insLn.data.added -eq 1) 'add_polyline_features'

$upd = Op pro.updateFeatures @{ layer = 'McpEditTestPt'; where = "NAME = 'alpha'"; attributes = '{"NAME":"alpha-updated"}' }
Check ($upd.ok -and $upd.data.modified -eq 1) 'update_features by where' ($upd | ConvertTo-Json -Compress -Depth 3)

$read = Op pro.readLayerAttributes @{ layer = 'McpEditTestPt'; where = "NAME = 'alpha-updated'" }
Check ($read.ok -and $read.data.returned -eq 1) 'update verified via read'

$noop = Op pro.updateFeatures @{ layer = 'McpEditTestPt'; where = "NAME = 'alpha-updated'"; attributes = '{"NAME":"alpha-updated"}' }
Check ($noop.ok) 'no-op update tolerated' ($noop | ConvertTo-Json -Compress -Depth 3)

$del = Op pro.deleteFeatures @{ layer = 'McpEditTestPt'; where = "NAME = 'beta'" }
Check ($del.ok -and $del.data.deleted -eq 1) 'delete_features by where'

$cnt = Op pro.countFeatures @{ layer = 'McpEditTestPt' }
Check ($cnt.ok -and $cnt.data.count -eq 1) 'count after delete == 1'

$he = Op pro.hasEdits
Check ($he.ok) 'has_edits'
$se = Op pro.saveEdits
Check ($se.ok -and $se.data.savedEdits) 'save_edits'

Write-Host "== analysis =="
$fs = Op pro.getFieldStatistics @{ layer = 'McpEditTestPt'; field = 'NAME' }
Check ($fs.ok -and $fs.data.totalRows -ge 1 -and $fs.data.topValues.Count -ge 1) 'get_field_statistics' ($fs | ConvertTo-Json -Compress -Depth 4)

# alpha point sits exactly on a vertex of the scratch line — intersect selects it
$sbl = Op pro.selectByLocation @{ layer = 'McpEditTestPt'; selectFeatures = 'McpEditTestLn' }
Check ($sbl.ok -and $sbl.data.selectedCount -ge 1) 'select_by_location' ($sbl | ConvertTo-Json -Compress -Depth 3)
Op pro.clearSelection | Out-Null

Write-Host "== display properties =="
$dq = Op pro.setDefinitionQuery @{ layer = 'McpEditTestPt'; where = 'OBJECTID > 0' }
Check ($dq.ok -and -not $dq.data.cleared) 'set_definition_query'
$dqc = Op pro.setDefinitionQuery @{ layer = 'McpEditTestPt' }
Check ($dqc.ok -and $dqc.data.cleared) 'clear definition query'

$tr = Op pro.setLayerTransparency @{ layer = 'McpEditTestPt'; transparency = '35' }
Check ($tr.ok -and $tr.data.transparency -eq 35) 'set_layer_transparency'

$lbl = Op pro.setLabeling @{ layer = 'McpEditTestPt'; visible = 'true'; field = 'NAME' }
Check ($lbl.ok -and $lbl.data.labelsVisible) 'set_labeling on with field'

Write-Host "== symbology =="
$rend = Op pro.setLayerRenderer @{ layer = 'McpEditTestPt'; rendererType = 'simple'; fillR = '230'; fillG = '80'; fillB = '20'; size = '12' }
Check ($rend.ok -and $rend.data.renderer -eq 'simple') 'set_layer_renderer simple'
$sym = Op pro.getLayerSymbology @{ layer = 'McpEditTestPt' }
Check ($sym.ok -and $sym.data.renderer.type -eq 'simple') 'get_layer_symbology'

Write-Host "== camera + bookmarks =="
$ze = Op pro.zoomToExtent @{ xmin = '-115.4'; ymin = '36.0'; xmax = '-115.0'; ymax = '36.3'; wkid = '4326' }
Check ($ze.ok -and $ze.data.zoomed) 'zoom_to_extent'
$zs = Op pro.zoomToScale @{ scale = '150000' }
Check ($zs.ok) 'zoom_to_scale'
$bm = Op pro.createBookmark @{ name = 'mcp-test-bm' }
Check ($bm.ok) 'create_bookmark'
$bml = Op pro.listBookmarks
Check ($bml.ok -and ($bml.data | Where-Object { $_.name -eq 'mcp-test-bm' })) 'list_bookmarks contains new'
$bmz = Op pro.zoomToBookmark @{ name = 'mcp-test-bm' }
Check ($bmz.ok) 'zoom_to_bookmark'

Write-Host "== vision =="
$cap = Op pro.captureMapView @{ width = '800'; height = '600' }
Check ($cap.ok -and (Test-Path $cap.data.output)) 'capture_map_view writes PNG' ($cap | ConvertTo-Json -Compress -Depth 3)
if ($cap.ok) { $script:capturePath = $cap.data.output }

Write-Host "== layout furniture =="
$lo = Op pro.createLayout @{ name = 'McpLayoutTest' }
Check ($lo.ok) 'create_layout'
$mf = Op pro.addMapFrameToLayout @{ layoutName = 'McpLayoutTest'; mapName = $activeMap }
Check ($mf.ok) 'add_map_frame_to_layout'
$txt = Op pro.addLayoutText @{ layoutName = 'McpLayoutTest'; text = 'MCP Verification Map'; fontSize = '20' }
Check ($txt.ok) 'add_layout_text'
$leg = Op pro.addLegend @{ layoutName = 'McpLayoutTest' }
Check ($leg.ok) 'add_legend' ($leg | ConvertTo-Json -Compress -Depth 3)
$na = Op pro.addNorthArrow @{ layoutName = 'McpLayoutTest' }
Check ($na.ok) 'add_north_arrow' ($na | ConvertTo-Json -Compress -Depth 3)
$sb = Op pro.addScaleBar @{ layoutName = 'McpLayoutTest' }
Check ($sb.ok) 'add_scale_bar' ($sb | ConvertTo-Json -Compress -Depth 3)
$sfe = Op pro.setMapFrameExtent @{ layoutName = 'McpLayoutTest'; layer = 'McpEditTestPt' }
Check ($sfe.ok) 'set_map_frame_extent to layer'
$exp = Op pro.exportLayout @{ name = 'McpLayoutTest'; output = "$homeFolder\mcp-captures\layout_test.png"; format = 'png'; resolution = '96' }
Check ($exp.ok -and (Test-Path "$homeFolder\mcp-captures\layout_test.png")) 'export_layout png'

Write-Host "== map admin =="
$cm = Op pro.createMap @{ name = 'McpScratchMap'; open = 'false' }
Check ($cm.ok) 'create_map'
$lm = Op pro.listMaps
Check ($lm.ok -and ($lm.data | Where-Object { $_.name -eq 'McpScratchMap' })) 'list_maps contains scratch map'

Write-Host "== cleanup =="
Op pro.removeLayer @{ layer = 'McpEditTestPt' } | Out-Null
Op pro.removeLayer @{ layer = 'McpEditTestLn' } | Out-Null
$clean = Op pro.executePython @{ code = @"
import arcpy
aprx = arcpy.mp.ArcGISProject('CURRENT')
for item in list(aprx.listLayouts('McpLayoutTest')) + list(aprx.listMaps('McpScratchMap')):
    aprx.deleteItem(item)
gdb = r'$gdb'
for name in ['McpEditTestPt', 'McpEditTestLn']:
    if arcpy.Exists(gdb + '\\' + name):
        arcpy.management.Delete(gdb + '\\' + name)
m = aprx.listMaps('$activeMap')[0]
for bm in m.listBookmarks('mcp-test-bm'):
    m.removeBookmark(bm)
result = 'cleaned'
"@ }
Check ($clean.ok -and $clean.data.ok) 'scratch artifacts cleaned' ($clean | ConvertTo-Json -Compress -Depth 4)
Op pro.saveProject | Out-Null

Write-Host ""
Write-Host "$($script:pass) passed, $($script:fail) failed"
if ($script:capturePath) { Write-Host "Map capture for visual check: $($script:capturePath)" }
if ($script:fail -gt 0) {
    Write-Host ("Failures: " + ($script:failures -join '; ')) -ForegroundColor Red
    exit 1
}
