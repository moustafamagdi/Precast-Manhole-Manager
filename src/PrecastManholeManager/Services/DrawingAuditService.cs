using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class DrawingAuditResult
    {
        public List<string> Issues { get; } = new List<string>();
        public int ViewsChecked { get; set; }
        public int ManagedOpenings { get; set; }
        public List<int> WallIds { get; } = new List<int>();
        public string Summary => Issues.Count == 0 ? "AUTOMATED DRAWING CHECKS PASSED; visual acceptance and service coverage remain separate." : string.Join("\n", Issues.Distinct());
    }

    // A command-scoped, read-only snapshot. No transactions, regeneration, view activation or repairs.
    // Construct AFTER model changes; never reuse this index across an editing phase.
    internal sealed class DrawingAuditService
    {
        private readonly Document _doc;
        private readonly DiagnosticLogger _log;
        private readonly Dictionary<string, List<View>> _views;
        private readonly List<Viewport> _ports;
        private readonly List<Dimension> _dimensions;
        private readonly List<Element> _markers;
        private readonly List<Opening> _openings;
        private readonly Dictionary<int, double[]> _portBounds = new Dictionary<int, double[]>();
        private readonly Dictionary<int, double[]> _sheetBounds = new Dictionary<int, double[]>();
        private readonly Dictionary<int, string> _boundErrors = new Dictionary<int, string>();

        public DrawingAuditService(Document doc, DiagnosticLogger log)
        {
            _doc = doc; _log = log;
            _views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate)
                .GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _ports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList();
            _dimensions = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().ToList();
            _markers = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Viewers).WhereElementIsNotElementType().ToList();
            _openings = new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>().ToList();
        }

        public DrawingAuditResult Check(Element foundation)
        {
            var result = new DrawingAuditResult();
            var footprint = new VirtualFoundationRecoveryService(_doc, _log).Analyze(foundation);
            if (!footprint.Accepted) { result.Issues.Add("BODY_UNVERIFIED: " + footprint.Reason); return result; }
            var slot = BatchSheetLayoutService.Find(_doc, foundation);
            if (slot?.Sheet == null) result.Issues.Add("ROW_MISSING: no valid six-row sheet reservation.");
            string prefix = "MH_" + foundation.Id + "_PROD_2D";
            var walls = UnifiedOpeningReviewService.BuildManhole(foundation, footprint).Walls;
            var wallIds = new HashSet<ElementId>(footprint.Walls.Select(w => w.Id));
            result.WallIds.AddRange(footprint.Walls.Select(w => w.Id.IntegerValue));
            result.ManagedOpenings = _openings.Count(o => o.Host != null && wallIds.Contains(o.Host.Id) && OpeningStorageService.TryRead(o, out var data));
            var baseBox = foundation.get_BoundingBox(null);
            if (baseBox == null) result.Issues.Add("BASE_BOUNDS_UNVERIFIED");
            for (int number = 0; number <= 4; number++)
            {
                string name = prefix + (number == 0 ? "_PLAN" : "_OUT_W" + number);
                try
                {
                    List<View> candidates;
                    if (!_views.TryGetValue(name, out candidates) || candidates.Count != 1)
                    { result.Issues.Add("VIEW_MISSING_OR_AMBIGUOUS: " + name); continue; }
                    View view = candidates[0];
                    result.ViewsChecked++;
                    if (number == 0 ? !(view is ViewPlan) : !(view is ViewSection))
                    { result.Issues.Add("VIEW_KIND: " + name); continue; }
                    if (view.Scale != 25) result.Issues.Add("VIEW_SCALE: " + name + " expected 1:25.");
                    if (view.ViewTemplateId == ElementId.InvalidElementId) result.Issues.Add("VIEW_TEMPLATE_MISSING: " + name);
                    if (!view.CropBoxActive) result.Issues.Add("VIEW_CROP_DISABLED: " + name);
                    using (var manager = view.GetCropRegionShapeManager())
                        if (manager.ShapeSet || manager.NumberOfSplitRegions > 1)
                            result.Issues.Add("VIEW_CUSTOM_CROP_UNVERIFIED: " + name);
                    var visible = new HashSet<int>(new FilteredElementCollector(_doc, view.Id)
                        .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation }))
                        .WhereElementIsNotElementType().Select(e => e.Id.IntegerValue));
                    if (number == 0)
                    {
                        foreach (var wall in footprint.Walls)
                            CheckBody(view, wall, false, visible, result);
                        if (baseBox != null && !FitsCrop(view, Corners(baseBox), false)) result.Issues.Add("PLAN_BASE_OUTSIDE_CROP: " + name);
                        CheckMarkers(view, result);
                    }
                    else
                    {
                        var wall = walls.Single(w => w.Number == number).Wall;
                        var axis = (wall.Location as LocationCurve)?.Curve as Line;
                        if (axis == null || Math.Abs(view.RightDirection.DotProduct(axis.Direction)) < .999 || Math.Abs(view.UpDirection.Z - 1) > .001)
                            result.Issues.Add("SECTION_ORIENTATION: " + name + " no longer aligns with W" + number + ".");
                        CheckBody(view, wall, true, visible, result);
                        if (baseBox != null && !FitsCrop(view, Corners(baseBox), false)) result.Issues.Add("SECTION_BASE_OUTSIDE_CROP: " + name);
                        if (!visible.Contains(foundation.Id.IntegerValue)) result.Issues.Add("BASE_NOT_VISIBLE: " + name + "; check filters/worksets/template.");
                    }
                    var cuts = number == 0 ? new List<Opening>() : _openings.Where(o => o.Host?.Id == walls.Single(w => w.Number == number).Wall.Id &&
                        OpeningStorageService.TryRead(o, out var data)).ToList();
                    try
                    {
                        result.Issues.AddRange(OpeningDimensionService.AuditExisting(_doc, foundation, view, _dimensions, cuts,
                            number == 0 ? footprint.Walls : new List<Wall> { walls.Single(w => w.Number == number).Wall }));
                    }
                    catch (Exception ex) { result.Issues.Add("DIM_AUDIT_UNVERIFIED: " + name + " " + ex.Message); }
                    CheckPlacement(foundation, view, slot, number, result);
                }
                catch (Exception ex) { result.Issues.Add("VIEW_CHECK_UNVERIFIED: " + name + " " + ex.Message); }
            }
            return result;
        }

        private void CheckBody(View view, Wall wall, bool depth, HashSet<int> visible, DrawingAuditResult result)
        {
            if (!visible.Contains(wall.Id.IntegerValue) || wall.IsHidden(view)) result.Issues.Add("WALL_NOT_VISIBLE: " + view.Name + " Wall=" + wall.Id + "; check worksets/filters/template.");
            var axis = (wall.Location as LocationCurve)?.Curve as Line;
            var box = wall.get_BoundingBox(null);
            if (axis == null || box == null) { result.Issues.Add("WALL_BOUNDS_UNVERIFIED: " + wall.Id); return; }
            var normal = XYZ.BasisZ.CrossProduct(axis.Direction).Normalize();
            var points = new List<XYZ>();
            foreach (int end in new[] { 0, 1 })
            foreach (double side in new[] { -.5, .5 })
            foreach (double z in new[] { box.Min.Z, box.Max.Z })
            {
                var p = axis.GetEndPoint(end) + normal * (wall.Width * side);
                points.Add(new XYZ(p.X, p.Y, z));
            }
            if (!FitsCrop(view, points, depth)) result.Issues.Add("BODY_OUTSIDE_VIEW: " + view.Name + " Wall=" + wall.Id + "; check moved body, crop and section depth.");
        }

        internal static bool FitsCrop(View view, IEnumerable<XYZ> points, bool depth)
        {
            if (!view.CropBoxActive) return false;
            var crop = view.CropBox;
            double tolerance = UnitUtil.MmToFt(2);
            var local = points.Select(crop.Transform.Inverse.OfPoint).Select(p => new[] { p.X, p.Y, p.Z }).ToArray();
            return DrawingGeometry.ContainsPoints(new[] { crop.Min.X, crop.Min.Y, crop.Min.Z, crop.Max.X, crop.Max.Y, crop.Max.Z }, local, tolerance, depth);
        }

        private void CheckMarkers(View plan, DrawingAuditResult result)
        {
            var visibleIds = new HashSet<int>(new FilteredElementCollector(_doc, plan.Id).OfCategory(BuiltInCategory.OST_Viewers)
                .WhereElementIsNotElementType().Select(e => e.Id.IntegerValue));
            var shown = _markers.Where(e => visibleIds.Contains(e.Id.IntegerValue) && !e.IsHidden(plan)).ToList();
            for (int n = 1; n <= 4; n++)
                if (!shown.Any(e => e.Name == plan.Name.Substring(0, plan.Name.Length - 5) + "_OUT_W" + n))
                    result.Issues.Add("SECTION_MARK_MISSING: " + plan.Name + " W" + n + ". Check category and annotation crop visually.");
            if (shown.Any(e => !ManholeViewPresentationService.IsOwnSection(plan.Name, e.Name))) result.Issues.Add("FOREIGN_SECTION_MARK: " + plan.Name);
        }

        private void CheckPlacement(Element foundation, View view, BatchSheetSlot slot, int number, DrawingAuditResult result)
        {
            var ports = _ports.Where(p => p.ViewId == view.Id).ToList();
            if (ports.Count != 1) { result.Issues.Add("VIEW_NOT_PLACED_ONCE: " + view.Name); return; }
            var port = ports[0];
            if (slot?.Sheet != null && port.SheetId != slot.Sheet.Id) result.Issues.Add("WRONG_RESERVED_SHEET: " + view.Name);
            if (_doc.GetElement(port.GetTypeId())?.Name != "NO BUBBLE NTS") result.Issues.Add("VIEWPORT_TYPE: " + view.Name);
            string detail = port.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString();
            if (number > 0 && detail != "W" + number && detail != ManholeIdentityStore.Read(foundation) + "-W" + number)
                result.Issues.Add("DETAIL_NUMBER: " + view.Name + " expected W" + number + ".");
            var bounds = PortBounds(port);
            if (!DrawingGeometry.Valid(bounds)) { result.Issues.Add("VIEWPORT_BOUNDS_UNVERIFIED: " + view.Name + " " + _boundErrors[port.Id.IntegerValue]); return; }
            var sheet = _doc.GetElement(port.SheetId) as ViewSheet;
            var paper = PaperBounds(sheet);
            if (!DrawingGeometry.Valid(paper)) result.Issues.Add("SHEET_BOUNDS_UNVERIFIED: " + sheet?.SheetNumber + "; expected one titleblock.");
            else
            {
                double tolerance = UnitUtil.MmToFt(1);
                if (!DrawingGeometry.Contains(paper, bounds, tolerance)) result.Issues.Add("OUTSIDE_SHEET: " + view.Name);
                if (slot?.Sheet != null && port.SheetId == slot.Sheet.Id && !DrawingGeometry.Contains(DrawingGeometry.Row(paper, slot.Row), bounds, tolerance))
                    result.Issues.Add("OUTSIDE_RESERVED_ROW: " + view.Name + " row " + (slot.Row + 1));
                foreach (var other in _ports.Where(p => p.SheetId == port.SheetId && p.Id != port.Id))
                {
                    var otherBounds = PortBounds(other);
                    if (!DrawingGeometry.Valid(otherBounds)) result.Issues.Add("NEIGHBOR_BOUNDS_UNVERIFIED: viewport " + other.Id);
                    else if (DrawingGeometry.Overlaps(bounds, otherBounds, tolerance)) result.Issues.Add("VIEWPORT_OVERLAP: " + view.Name + " / " + _doc.GetElement(other.ViewId)?.Name);
                }
            }
        }

        private double[] PaperBounds(ViewSheet sheet)
        {
            if (sheet == null) return null;
            if (_sheetBounds.TryGetValue(sheet.Id.IntegerValue, out var bounds)) return bounds;
            var blocks = new FilteredElementCollector(_doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().ToList();
            bounds = blocks.Count == 1 ? ProjectBox(blocks[0].get_BoundingBox(sheet)) : null;
            _sheetBounds[sheet.Id.IntegerValue] = bounds;
            return bounds;
        }

        private double[] PortBounds(Viewport port)
        {
            if (_portBounds.TryGetValue(port.Id.IntegerValue, out var bounds)) return bounds;
            try
            {
                var box = port.GetBoxOutline();
                var label = port.GetLabelOutline();
                bounds = DrawingGeometry.Union(Rect(box), Rect(label));
                if (!DrawingGeometry.Valid(bounds)) _boundErrors[port.Id.IntegerValue] = "Invalid or empty bounds.";
            }
            catch (Exception ex) { bounds = null; _boundErrors[port.Id.IntegerValue] = ex.Message; }
            _portBounds[port.Id.IntegerValue] = bounds;
            return bounds;
        }

        private static double[] Rect(Outline outline) => outline == null ? null : new[] { outline.MinimumPoint.X, outline.MinimumPoint.Y, outline.MaximumPoint.X, outline.MaximumPoint.Y };
        private static double[] ProjectBox(BoundingBoxXYZ box)
        {
            if (box == null) return null;
            var points = Corners(box).ToList();
            return new[] { points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y) };
        }

        internal static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
            foreach (double z in new[] { box.Min.Z, box.Max.Z })
                yield return box.Transform.OfPoint(new XYZ(x, y, z));
        }
    }
}
