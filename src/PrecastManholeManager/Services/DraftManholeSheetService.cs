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
                XYZ.BasisY, XYZ.BasisZ.Negate(), mainBox);
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
                    XYZ.BasisZ, outward.Negate(), wallBox);
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
                    .Cast<FamilySymbol>()
                    .OrderByDescending(x =>
                        (x.Name ?? "").IndexOf("A0",
                            StringComparison.OrdinalIgnoreCase) >= 0 ? 3 :
                        (x.Name ?? "").IndexOf("A1",
                            StringComparison.OrdinalIgnoreCase) >= 0 ? 2 :
                        (x.Name ?? "").IndexOf("A2",
                            StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                    .FirstOrDefault();
                if (titleBlock == null)
                    throw new InvalidOperationException(
                        "No titleblock family type is loaded. " +
                        "Load an A1/A0 titleblock before creating the draft.");
                sheet = ViewSheet.Create(doc, titleBlock.Id);
                sheet.Name = prefix;
            }
            result.Sheet = sheet;
            doc.Regenerate();

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

            // Create viewports once; measure their ACTUAL Revit box size
            // after regeneration, then choose a sheet-fitting view scale.
            // We deliberately exclude titleblock geometry and viewport labels
            // (GetBoxOutline reports the real view box, not the label).
            bool wasExisting = sheet.GetAllViewports().Count == 5;
            if (wasExisting)
            {
                result.Message = "Existing five-view draft sheet retained: " +
                    sheet.SheetNumber + " / " + sheet.Name +
                    ". Existing manual viewport positions were not changed.";
                log.Info("DRAFT EXISTING SHEET PRESERVED " + sheet.Id.IntegerValue);
                return result;
            }

            double edge = UnitUtil.MmToFt(12);
            double gap = UnitUtil.MmToFt(8);
            double usableWidth = width - 2 * edge;
            double usableHeight = height - 2 * edge;
            if (usableWidth <= 0 || usableHeight <= 0)
                throw new InvalidOperationException("Sheet printable area is invalid.");

            // All five placeable viewports are created near the middle
            // initially. Revit box outlines determine final placement.
            XYZ provisional = new XYZ(
                left + width * 0.5, bottom + height * 0.5, 0);
            var ports = new List<Viewport>();
            foreach (View3D view in result.Views)
            {
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    throw new InvalidOperationException(
                        "Cannot place " + view.Name +
                        " on the draft sheet. It may already be placed elsewhere.");
                ports.Add(Viewport.Create(doc, sheet.Id, view.Id, provisional));
            }

            // 1:50 remains the preferred scale; if too large, reduce
            // automatically in familiar architectural increments.
            int[] scales = { 50, 75, 100, 125, 150, 200, 250, 300, 400 };
            bool fitted = false;
            string measurements = "";
            int fittedScale = 0;
            foreach (int candidateScale in scales)
            {
                foreach (View3D view in result.Views)
                    view.Scale = candidateScale;
                doc.Regenerate();

                double[] w = ports.Select(p =>
                {
                    Outline o = p.GetBoxOutline();
                    return o.MaximumPoint.X - o.MinimumPoint.X;
                }).ToArray();
                double[] h = ports.Select(p =>
                {
                    Outline o = p.GetBoxOutline();
                    return o.MaximumPoint.Y - o.MinimumPoint.Y;
                }).ToArray();

                // Guard against Revit retaining a project-wide crop
                // despite our explicit section/crop alignment. An absurd
                // viewport is a geometry/crop bug, not a small titleblock:
                // bail out at the FIRST scale instead of spending minutes
                // trying 1:400 and misleading the operator.
                if (candidateScale == 50)
                {
                    double maxModelExtent = Math.Max(maxX - minX,
                        Math.Max(maxY - minY, maxZ - minZ));
                    double expectedPaperFt = maxModelExtent / candidateScale;
                    double largestPaperFt = Math.Max(
                        w.Max(), h.Max());
                    log.Info("DRAFT CROP DIAGNOSTIC ModelMaxMm=" +
                        UnitUtil.FtToMm(maxModelExtent).ToString("0.#") +
                        " ExpectedPaperMaxMm=" +
                        UnitUtil.FtToMm(expectedPaperFt).ToString("0.#") +
                        " MeasuredPaperMaxMm=" +
                        UnitUtil.FtToMm(largestPaperFt).ToString("0.#"));
                    if (largestPaperFt > expectedPaperFt * 8.0)
                        throw new InvalidOperationException(
                            "3D viewport crop is still project-sized. " +
                            "Largest paper view is " +
                            UnitUtil.FtToMm(largestPaperFt).ToString("0.#") +
                            " mm while the manhole geometry suggests about " +
                            UnitUtil.FtToMm(expectedPaperFt).ToString("0.#") +
                            " mm at 1:50. This is a 3D crop/orientation " +
                            "issue, NOT a titleblock size problem; " +
                            "all draft changes will be rolled back.");
                }

                // 3 rows: plan; W1 and W2; W3 and W4. Each row uses
                // the tallest view in that row, with a fixed clear gap.
                double planWidth = w[0];
                double topWidth = w[1] + w[2] + gap;
                double bottomWidth = w[3] + w[4] + gap;
                double topRowHeight = Math.Max(h[1], h[2]);
                double bottomRowHeight = Math.Max(h[3], h[4]);
                double requiredWidth = Math.Max(planWidth,
                    Math.Max(topWidth, bottomWidth));
                double requiredHeight = h[0] + topRowHeight +
                    bottomRowHeight + 2 * gap;

                measurements = "Scale=1:" + candidateScale +
                    " AvailableSheetMm=" +
                    UnitUtil.FtToMm(usableWidth).ToString("0.#") + "x" +
                    UnitUtil.FtToMm(usableHeight).ToString("0.#") +
                    " RequiredMm=" +
                    UnitUtil.FtToMm(requiredWidth).ToString("0.#") + "x" +
                    UnitUtil.FtToMm(requiredHeight).ToString("0.#");
                log.Info("DRAFT SHEET FIT " + measurements +
                    " Plan=" + UnitUtil.FtToMm(w[0]).ToString("0.#") +
                    "x" + UnitUtil.FtToMm(h[0]).ToString("0.#") +
                    " W1=" + UnitUtil.FtToMm(w[1]).ToString("0.#") +
                    "x" + UnitUtil.FtToMm(h[1]).ToString("0.#") +
                    " W2=" + UnitUtil.FtToMm(w[2]).ToString("0.#") +
                    "x" + UnitUtil.FtToMm(h[2]).ToString("0.#") +
                    " W3=" + UnitUtil.FtToMm(w[3]).ToString("0.#") +
                    "x" + UnitUtil.FtToMm(h[3]).ToString("0.#") +
                    " W4=" + UnitUtil.FtToMm(w[4]).ToString("0.#") +
                    "x" + UnitUtil.FtToMm(h[4]).ToString("0.#"));

                if (requiredWidth > usableWidth ||
                    requiredHeight > usableHeight)
                    continue;

                // Center the entire 3-row group on the sheet. Each
                // viewport is centered vertically within its row.
                double yStart = bottom + edge +
                    (usableHeight - requiredHeight) * 0.5;
                double[] centersY = {
                    yStart + bottomRowHeight + gap + topRowHeight +
                        gap + h[0] / 2,
                    yStart + bottomRowHeight + gap + topRowHeight / 2,
                    yStart + bottomRowHeight + gap + topRowHeight / 2,
                    yStart + bottomRowHeight / 2,
                    yStart + bottomRowHeight / 2
                };
                double centerX = left + width * 0.5;
                double[] centersX = {
                    centerX,
                    centerX - (w[2] + gap) * 0.5,
                    centerX + (w[1] + gap) * 0.5,
                    centerX - (w[4] + gap) * 0.5,
                    centerX + (w[3] + gap) * 0.5
                };
                for (int i = 0; i < ports.Count; i++)
                    ports[i].SetBoxCenter(new XYZ(
                        centersX[i], centersY[i], 0));
                doc.Regenerate();

                // Verify ACTUAL outlines after setting position (3D
                // camera crop can regenerate viewport extents).
                var boxes = ports.Select(p => p.GetBoxOutline()).ToList();
                bool inside = boxes.All(o =>
                    o.MinimumPoint.X >= left + edge - 1e-5 &&
                    o.MaximumPoint.X <= right - edge + 1e-5 &&
                    o.MinimumPoint.Y >= bottom + edge - 1e-5 &&
                    o.MaximumPoint.Y <= top - edge + 1e-5);
                bool overlap = false;
                for (int i = 0; i < boxes.Count; i++)
                    for (int j = i + 1; j < boxes.Count; j++)
                    {
                        Outline x = boxes[i], y = boxes[j];
                        bool touches =
                            x.MinimumPoint.X < y.MaximumPoint.X + gap &&
                            x.MaximumPoint.X + gap > y.MinimumPoint.X &&
                            x.MinimumPoint.Y < y.MaximumPoint.Y + gap &&
                            x.MaximumPoint.Y + gap > y.MinimumPoint.Y;
                        if (touches) overlap = true;
                    }

                if (!inside || overlap)
                {
                    log.Warn("DRAFT SHEET FIT after positioning: " +
                        "inside=" + inside + " overlap=" + overlap +
                        " at 1:" + candidateScale);
                    continue;
                }

                fitted = true;
                fittedScale = candidateScale;
                break;
            }

            if (!fitted)
                throw new InvalidOperationException(
                    "Could not fit five viewports on selected titleblock " +
                    "at scales 1:50 to 1:400. Last measurement: " +
                    measurements + ". Nothing was changed.");

            log.Info("DRAFT SHEET AUTO-LAYOUT OK at scale 1:" +
                fittedScale + " Sheet=" + sheet.SheetNumber);
            result.Message = "Draft Plan + W1-W4 created and laid out at 1:" +
                fittedScale + " on " + sheet.SheetNumber + " / " +
                sheet.Name;
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
            v.DisplayStyle = DisplayStyle.HLR;
            return v;
        }

        private static void Orient(View3D v, XYZ eye, XYZ up,
            XYZ forward, BoundingBoxXYZ section)
        {
            v.SetOrientation(new ViewOrientation3D(eye, up, forward));
            v.IsSectionBoxActive = true;
            v.SetSectionBox(section);
            // Revit maintains a separate view-aligned CropBox whose
            // initial size can reflect the ENTIRE project. A small
            // SectionBox does NOT automatically shrink the sheet viewport.
            // Convert all EIGHT world-space section-box corners into the
            // current crop-box (view-local) coordinate system.
            v.CropBoxActive = true;
            v.CropBoxVisible = false;
            // Refresh Revit's local crop coordinate system after changing
            // camera orientation and section box.
            v.Document.Regenerate();
            BoundingBoxXYZ current = v.CropBox;
            if (current == null)
                throw new InvalidOperationException(
                    "Revit did not expose a CropBox for " + v.Name);

            Transform inverse = current.Transform.Inverse;
            Transform source = section.Transform;
            double loX = double.MaxValue, loY = double.MaxValue;
            double loZ = double.MaxValue;
            double hiX = double.MinValue, hiY = double.MinValue;
            double hiZ = double.MinValue;
            double[] xs = { section.Min.X, section.Max.X };
            double[] ys = { section.Min.Y, section.Max.Y };
            double[] zs = { section.Min.Z, section.Max.Z };
            foreach (double x in xs)
            foreach (double y in ys)
            foreach (double z in zs)
            {
                XYZ projected = inverse.OfPoint(
                    source.OfPoint(new XYZ(x, y, z)));
                loX = Math.Min(loX, projected.X);
                loY = Math.Min(loY, projected.Y);
                loZ = Math.Min(loZ, projected.Z);
                hiX = Math.Max(hiX, projected.X);
                hiY = Math.Max(hiY, projected.Y);
                hiZ = Math.Max(hiZ, projected.Z);
            }

            // 30 mm paper-independent MODEL padding is already present
            // in the section envelope. This extra 20 mm prevents touching.
            double extra = UnitUtil.MmToFt(20);
            current.Min = new XYZ(loX - extra, loY - extra,
                loZ - extra);
            current.Max = new XYZ(hiX + extra, hiY + extra,
                hiZ + extra);
            // The CropBox setter ignores the supplied Transform and
            // keeps Revit's view orientation. Preserve current.Transform.
            v.CropBox = current;
        }
    }
}
