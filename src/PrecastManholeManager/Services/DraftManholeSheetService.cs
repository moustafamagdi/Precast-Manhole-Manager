using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class DraftSheetResult
    {
        public ViewSheet Sheet { get; set; }
        public List<View3D> Views { get; } = new List<View3D>();
        public string Message { get; set; }
    }

    // Prototype only: orthographic 3D plan + four wall-facing 3D views.
    // NOT final dimensioned shop drawings. Creates/reuses a small named set.
    internal static class DraftManholeSheetService
    {
        public static DraftSheetResult Generate(Document doc, Element foundation,
            VirtualFoundationResult footprint, DiagnosticLogger log)
        {
            if (doc == null || foundation == null || footprint == null ||
                !footprint.Accepted || footprint.Walls.Count != 4)
                throw new InvalidOperationException(
                    "Draft needs a validated four-wall manhole.");
            var result = new DraftSheetResult();
            string prefix = "MH_" + foundation.Id.IntegerValue + "_DRAFT";
            BoundingBoxXYZ foundationBox = foundation.get_BoundingBox(null);
            var wallBounds = footprint.Walls.Select(w => w.get_BoundingBox(null)).ToList();
            if (foundationBox == null || wallBounds.Any(x => x == null))
                throw new InvalidOperationException("Incomplete wall/base bounds.");

            // Section box is based on the four recovered walls, NOT the
            // bounding-box center of a cropped foundation.
            double minX = wallBounds.Min(x => x.Min.X);
            double minY = wallBounds.Min(x => x.Min.Y);
            double minZ = Math.Min(foundationBox.Min.Z,
                wallBounds.Min(x => x.Min.Z));
            double maxX = wallBounds.Max(x => x.Max.X);
            double maxY = wallBounds.Max(x => x.Max.Y);
            double maxZ = Math.Max(foundationBox.Max.Z,
                wallBounds.Max(x => x.Max.Z));
            double pad = UnitUtil.MmToFt(125);
            XYZ center = new XYZ(footprint.VirtualCenter.X,
                footprint.VirtualCenter.Y, (minZ + maxZ) * 0.5);
            ViewFamilyType viewType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily ==
                    ViewFamily.ThreeDimensional);
            if (viewType == null)
                throw new InvalidOperationException("No orthographic 3D view type.");
            var mainBox = Box(minX - pad, minY - pad, minZ - pad,
                maxX + pad, maxY + pad, maxZ + pad);

            View3D plan = GetOrCreate(doc, prefix + "_PLAN", viewType);
            Orient(plan, center + XYZ.BasisZ * UnitUtil.MmToFt(2500),
                XYZ.BasisY, -XYZ.BasisZ, mainBox);
            result.Views.Add(plan);

            // Stable numbering: azimuth clockwise from north; W1-W4
            // follows the same legacy numbering convention (1,2,4,3).
            var ordered = footprint.Walls.Select(w =>
            {
                Line line = (w.Location as LocationCurve)?.Curve as Line;
                if (line == null)
                    throw new InvalidOperationException("Wall is not straight.");
                XYZ midpoint = (line.GetEndPoint(0) + line.GetEndPoint(1)) * 0.5;
                XYZ vector = midpoint - footprint.VirtualCenter;
                double azimuth = Math.Atan2(vector.X, vector.Y);
                if (azimuth < 0) azimuth += 2 * Math.PI;
                return new { Wall = w, Mid = midpoint, Angle = azimuth };
            }).OrderBy(x => x.Angle).ToList();
            int[] nums = { 1, 2, 4, 3 };
            for (int i = 0; i < 4; i++)
            {
                var current = ordered[i];
                Wall wall = current.Wall;
                BoundingBoxXYZ bounds = wallBounds[
                    footprint.Walls.FindIndex(w =>
                        w.Id.IntegerValue == wall.Id.IntegerValue)];
                XYZ outward = new XYZ(
                    current.Mid.X - footprint.VirtualCenter.X,
                    current.Mid.Y - footprint.VirtualCenter.Y, 0);
                if (outward.GetLength() < 1e-8)
                    throw new InvalidOperationException("Invalid wall orientation.");
                outward = outward.Normalize();
                XYZ mid = new XYZ(current.Mid.X, current.Mid.Y,
                    (minZ + maxZ) * 0.5);
                View3D elevation = GetOrCreate(doc,
                    prefix + "_W" + nums[i], viewType);
                // The wall and its physical openings are visible, not the
                // opposite walls that would obscure this elevation.
                BoundingBoxXYZ wallBox = Box(
                    bounds.Min.X - pad, bounds.Min.Y - pad,
                    minZ - pad, bounds.Max.X + pad, bounds.Max.Y + pad,
                    maxZ + pad);
                Orient(elevation,
                    mid + outward * UnitUtil.MmToFt(2500),
                    XYZ.BasisZ, -outward, wallBox);
                result.Views.Add(elevation);
                log.Info("DRAFT ELEVATION W" + nums[i] +
                    " WallId=" + wall.Id.IntegerValue +
                    " ViewId=" + elevation.Id.IntegerValue);
            }

            // Keep the layout order explicit: plan, W1, W2, W3, W4.
            result.Views.Sort((a, b) =>
            {
                int Rank(View3D v)
                {
                    if (v.Name.EndsWith("_PLAN",
                        StringComparison.Ordinal)) return 0;
                    for (int w = 1; w <= 4; w++)
                        if (v.Name.EndsWith("_W" + w,
                            StringComparison.Ordinal)) return w;
                    return 99;
                }
                return Rank(a).CompareTo(Rank(b));
            });

            // A sheet with 5 views should be produced as one atomic result.
            // No suitable title block -> empty sheet with bounded diagnostic.
            ViewSheet sheet = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => s.Name == prefix);
            if (sheet != null && sheet.GetAllViewports().Count != 5)
                throw new InvalidOperationException(
                    "Draft sheet exists but its viewports were edited; " +
                    "leave it unchanged and review manually: " + sheet.Name);

            if (sheet == null)
            {
                // First installed title block family type. The user can
                // replace the title block after checking prototype layout.
                FamilySymbol titleBlock = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>().FirstOrDefault();
                sheet = ViewSheet.Create(doc, titleBlock != null
                    ? titleBlock.Id : ElementId.InvalidElementId);
                sheet.Name = prefix;
            }
            result.Sheet = sheet;

            // Prototype layout is based on actual sheet outline dimensions.
            // Revit uses feet for sheet-space coordinates.
            double left = sheet.Outline.Min.U;
            double right = sheet.Outline.Max.U;
            double bottom = sheet.Outline.Min.V;
            double top = sheet.Outline.Max.V;
            double width = right - left, height = top - bottom;
            if (width < UnitUtil.MmToFt(300) ||
                height < UnitUtil.MmToFt(200))
                throw new InvalidOperationException(
                    "Sheet/titleblock too small or invalid. " +
                    "Choose an A1/A0 titleblock before generating the draft.");

            // Plan across top half, four elevations in a 2x2 grid below.
            XYZ[] positions =
            {
                new XYZ(left + width * 0.5, bottom + height * 0.75, 0),
                new XYZ(left + width * 0.27, bottom + height * 0.43, 0),
                new XYZ(left + width * 0.73, bottom + height * 0.43, 0),
                new XYZ(left + width * 0.27, bottom + height * 0.16, 0),
                new XYZ(left + width * 0.73, bottom + height * 0.16, 0)
            };

            for (int i = 0; i < result.Views.Count; i++)
            {
                View3D view = result.Views[i];
                // One-off prototype; never auto-remove manually placed views.
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                {
                    if (sheet.GetAllViewports().Any(vp =>
                        ((Viewport)doc.GetElement(vp)).ViewId.IntegerValue ==
                        view.Id.IntegerValue))
                        continue;
                    throw new InvalidOperationException(
                        "Cannot place orthographic view on draft sheet: " +
                        view.Name);
                }
                Viewport.Create(doc, sheet.Id, view.Id, positions[i]);
            }

            // Check that viewports fit their assigned cells. Fail as a
            // whole instead of silently producing overlapping fabrication
            // sheets. User may enlarge titleblock or use a larger scale.
            doc.Regenerate();
            double margin = UnitUtil.MmToFt(5);
            var outlines = sheet.GetAllViewports().Select(id =>
                ((Viewport)doc.GetElement(id)).GetBoxOutline()).ToList();
            foreach (Outline o in outlines)
            {
                if (o.MinimumPoint.X < left + margin ||
                    o.MaximumPoint.X > right - margin ||
                    o.MinimumPoint.Y < bottom + margin ||
                    o.MaximumPoint.Y > top - margin)
                    throw new InvalidOperationException(
                        "Draft views exceed sheet boundaries. " +
                        "Use a larger titleblock; transaction rolled back.");
            }
            for (int i = 0; i < outlines.Count; i++)
            for (int j = i + 1; j < outlines.Count; j++)
            {
                var a = outlines[i]; var b = outlines[j];
                bool overlap = a.MinimumPoint.X < b.MaximumPoint.X + margin &&
                    a.MaximumPoint.X + margin > b.MinimumPoint.X &&
                    a.MinimumPoint.Y < b.MaximumPoint.Y + margin &&
                    a.MaximumPoint.Y + margin > b.MinimumPoint.Y;
                if (overlap)
                    throw new InvalidOperationException(
                        "Draft viewports overlap. Enlarge the sheet or " +
                        "adjust scale before making fabrication sheets.");
            }

            result.Message = "Draft Plan + W1-W4 created and laid out on " +
                sheet.SheetNumber + " / " + sheet.Name;
            log.Info("DRAFT SHEET COMPLETE Sheet=" +
                sheet.Id.IntegerValue + " Number=" + sheet.SheetNumber +
                " Views=" + string.Join(",", result.Views.Select(v =>
                    v.Id.IntegerValue)));
            return result;
        }

        private static BoundingBoxXYZ Box(double x0, double y0, double z0,
            double x1, double y1, double z1)
        {
            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(x0, y0, z0),
                Max = new XYZ(x1, y1, z1)
            };
        }

        private static View3D GetOrCreate(Document doc, string name,
            ViewFamilyType viewType)
        {
            View3D v = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name == name);
            if (v == null)
            {
                v = View3D.CreateIsometric(doc, viewType.Id);
                v.Name = name;
            }
            if (v.IsPerspective)
                throw new InvalidOperationException(
                    "Draft view must be orthographic: " + name);
            v.Scale = 50; // first physical prototype, change manually if needed
            v.DisplayStyle = DisplayStyle.HiddenLine;
            return v;
        }

        private static void Orient(View3D v, XYZ eye, XYZ up,
            XYZ forward, BoundingBoxXYZ section)
        {
            v.SetOrientation(new ViewOrientation3D(eye, up, forward));
            v.IsSectionBoxActive = true;
            v.SetSectionBox(section);
        }
    }
}
