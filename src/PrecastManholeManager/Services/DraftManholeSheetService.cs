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
        public List<View> Views { get; } = new List<View>();
        public string Message { get; set; }
    }

    // One-time prototype: actual Revit Floor Plan and 4 ViewSections,
    // not five orthographic 3D views. Does not modify model geometry.
    // Caller owns ONE Transaction, so any layout error rolls back the
    // entire sheet and its dependent views.
    internal static class DraftManholeSheetService
    {
        public static DraftSheetResult Generate(Document doc, Element foundation,
            VirtualFoundationResult footprint, DiagnosticLogger log)
        {
            if (doc == null || foundation == null || footprint == null ||
                !footprint.Accepted || footprint.Walls.Count != 4)
                throw new InvalidOperationException(
                    "Draft requires a validated four-wall manhole.");

            const double paddingMm = 180;
            double pad = UnitUtil.MmToFt(paddingMm);
            var result = new DraftSheetResult();
            string prefix = "MH_" + foundation.Id.IntegerValue + "_DRAFT_2D";
            BoundingBoxXYZ baseBox = foundation.get_BoundingBox(null);
            List<BoundingBoxXYZ> wallBoxes = footprint.Walls
                .Select(w => w.get_BoundingBox(null)).ToList();
            if (baseBox == null || wallBoxes.Any(b => b == null))
                throw new InvalidOperationException(
                    "Missing foundation or wall bounding boxes.");

            // Cropped foundations may have an inaccurate box center. Use
            // wall bounds and the previously validated virtual footprint.
            double minX = wallBoxes.Min(b => b.Min.X);
            double minY = wallBoxes.Min(b => b.Min.Y);
            double maxX = wallBoxes.Max(b => b.Max.X);
            double maxY = wallBoxes.Max(b => b.Max.Y);
            double minZ = Math.Min(baseBox.Min.Z, wallBoxes.Min(b => b.Min.Z));
            double maxZ = Math.Max(baseBox.Max.Z, wallBoxes.Max(b => b.Max.Z));
            double centerZ = (minZ + maxZ) * 0.5;

            ViewFamilyType planType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
            ViewFamilyType sectionType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.Section);
            if (planType == null || sectionType == null)
                throw new InvalidOperationException(
                    "The project needs Floor Plan and Section view types.");

            Level level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(x => Math.Abs(x.Elevation - baseBox.Max.Z))
                .FirstOrDefault();
            if (level == null)
                throw new InvalidOperationException("No Level found for draft Floor Plan.");

            ViewPlan plan = GetOrCreatePlan(doc, prefix + "_PLAN", planType, level);
            ConfigurePlan(plan, minX - pad, minY - pad, maxX + pad,
                maxY + pad, minZ - pad, maxZ + pad, centerZ);
            result.Views.Add(plan);
            log.Info("2D DRAFT PLAN ViewId=" + plan.Id.IntegerValue +
                " Level=" + level.Name + " CropWmm=" +
                UnitUtil.FtToMm(maxX - minX + 2 * pad).ToString("0.#"));

            var ordered = footprint.Walls.Select(w =>
            {
                Line axis = (w.Location as LocationCurve)?.Curve as Line;
                if (axis == null)
                    throw new InvalidOperationException(
                        "One wall has no straight axis.");
                XYZ mid = (axis.GetEndPoint(0) + axis.GetEndPoint(1)) * 0.5;
                XYZ delta = mid - footprint.VirtualCenter;
                double angle = Math.Atan2(delta.X, delta.Y);
                return new { Wall = w, Axis = axis, Mid = mid,
                    Angle = angle < 0 ? angle + Math.PI * 2 : angle };
            }).OrderBy(x => x.Angle).ToList();

            // Keep existing W1/W2/W4/W3 convention used by the MEP
            // penetration scanner and the manufacturer Excel report.
            int[] numbering = { 1, 2, 4, 3 };
            var elevations = new Dictionary<int, ViewSection>();
            for (int i = 0; i < ordered.Count; i++)
            {
                int wallNumber = numbering[i];
                var w = ordered[i];
                XYZ outward = new XYZ(
                    w.Mid.X - footprint.VirtualCenter.X,
                    w.Mid.Y - footprint.VirtualCenter.Y, 0);
                if (outward.GetLength() < 1e-8)
                    throw new InvalidOperationException(
                        "Cannot determine outside wall direction.");
                outward = outward.Normalize();

                string name = prefix + "_W" + wallNumber;
                ViewSection elevation = GetOrCreateSection(doc, name,
                    sectionType, w.Axis, w.Mid, outward,
                    minZ - pad, maxZ + pad, pad);
                elevations[wallNumber] = elevation;
                log.Info("2D DRAFT SECTION W" + wallNumber +
                    " WallId=" + w.Wall.Id.IntegerValue +
                    " ViewId=" + elevation.Id.IntegerValue);
            }
            for (int n = 1; n <= 4; n++)
                result.Views.Add(elevations[n]);

            ViewSheet sheet = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => s.Name == prefix);

            if (sheet != null)
            {
                // Reusing five-view sheets is safe. Never reset positions
                // on a sheet a person has already laid out manually.
                HashSet<int> expected = new HashSet<int>(result.Views
                    .Select(v => v.Id.IntegerValue));
                HashSet<int> existing = new HashSet<int>(
                    sheet.GetAllViewports().Select(id =>
                        (doc.GetElement(id) as Viewport)?.ViewId.IntegerValue ?? -1));
                if (!expected.SetEquals(existing))
                    throw new InvalidOperationException(
                        "The existing 2D draft sheet was changed manually. " +
                        "Preserve it and review its viewports: " + prefix);
                result.Sheet = sheet;
                result.Message = "Existing 2D Draft sheet preserved: " +
                    sheet.SheetNumber + " / " + sheet.Name;
                return result;
            }

            FamilySymbol titleBlock = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .OrderByDescending(x =>
                    (x.Name ?? "").IndexOf("A0",
                        StringComparison.OrdinalIgnoreCase) >= 0 ? 3 :
                    (x.Name ?? "").IndexOf("A1",
                        StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 0)
                .FirstOrDefault();
            if (titleBlock == null)
                throw new InvalidOperationException(
                    "Load an A0 or A1 titleblock before generating a draft.");

            sheet = ViewSheet.Create(doc, titleBlock.Id);
            sheet.Name = prefix;
            result.Sheet = sheet;
            doc.Regenerate();

            double left = sheet.Outline.Min.U;
            double bottom = sheet.Outline.Min.V;
            double width = sheet.Outline.Max.U - left;
            double height = sheet.Outline.Max.V - bottom;
            if (width < UnitUtil.MmToFt(500) ||
                height < UnitUtil.MmToFt(350))
                throw new InvalidOperationException(
                    "Selected sheet is too small for the five-view draft.");

            var ports = new List<Viewport>();
            XYZ initial = new XYZ(left + width * 0.5,
                bottom + height * 0.5, 0);
            foreach (View view in result.Views)
            {
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    throw new InvalidOperationException(
                        "Cannot place view " + view.Name +
                        " (it may already be on another sheet).");
                ports.Add(Viewport.Create(doc, sheet.Id, view.Id, initial));
            }

            // Use real 2D crop extents measured by Revit. This avoids
            // the whole-project camera crop that broke the old 3D draft.
            int[] scales = { 25, 50, 75, 100, 125, 150, 200 };
            double edge = UnitUtil.MmToFt(22);
            double gap = UnitUtil.MmToFt(12);
            double usableW = width - 2 * edge;
            double usableH = height - 2 * edge;
            int fittedScale = 0;
            string lastMeasurement = "";

            foreach (int scale in scales)
            {
                foreach (View view in result.Views)
                    view.Scale = scale;
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

                double row2 = Math.Max(h[1], h[2]);
                double row3 = Math.Max(h[3], h[4]);
                double needW = Math.Max(w[0], Math.Max(
                    w[1] + w[2] + gap, w[3] + w[4] + gap));
                double needH = h[0] + row2 + row3 + 2 * gap;
                lastMeasurement = "1:" + scale +
                    " Required=" + UnitUtil.FtToMm(needW).ToString("0.#") +
                    "x" + UnitUtil.FtToMm(needH).ToString("0.#") +
                    " mm Available=" + UnitUtil.FtToMm(usableW).ToString("0.#") +
                    "x" + UnitUtil.FtToMm(usableH).ToString("0.#") + " mm";
                log.Info("2D DRAFT FIT " + lastMeasurement);
                if (needW > usableW || needH > usableH) continue;

                double y = bottom + edge + (usableH - needH) * 0.5;
                double cx = left + width * 0.5;
                double[] cy = {
                    y + row3 + gap + row2 + gap + h[0] * 0.5,
                    y + row3 + gap + row2 * 0.5,
                    y + row3 + gap + row2 * 0.5,
                    y + row3 * 0.5,
                    y + row3 * 0.5
                };
                double[] xx = {
                    cx,
                    cx - (w[2] + gap) * 0.5,
                    cx + (w[1] + gap) * 0.5,
                    cx - (w[4] + gap) * 0.5,
                    cx + (w[3] + gap) * 0.5
                };
                for (int i = 0; i < ports.Count; i++)
                    ports[i].SetBoxCenter(new XYZ(xx[i], cy[i], 0));
                doc.Regenerate();

                var boxes = ports.Select(p => p.GetBoxOutline()).ToList();
                bool within = boxes.All(o =>
                    o.MinimumPoint.X >= left + edge - 1e-5 &&
                    o.MaximumPoint.X <= left + width - edge + 1e-5 &&
                    o.MinimumPoint.Y >= bottom + edge - 1e-5 &&
                    o.MaximumPoint.Y <= bottom + height - edge + 1e-5);
                bool overlap = false;
                for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    Outline a = boxes[i], b = boxes[j];
                    if (a.MinimumPoint.X < b.MaximumPoint.X + gap &&
                        a.MaximumPoint.X + gap > b.MinimumPoint.X &&
                        a.MinimumPoint.Y < b.MaximumPoint.Y + gap &&
                        a.MaximumPoint.Y + gap > b.MinimumPoint.Y)
                        overlap = true;
                }

                if (within && !overlap)
                {
                    fittedScale = scale;
                    break;
                }
                log.Warn("2D DRAFT layout rejected at 1:" + scale +
                    " Within=" + within + " Overlap=" + overlap);
            }

            if (fittedScale == 0)
                throw new InvalidOperationException(
                    "Five 2D views could not fit this sheet. " +
                    lastMeasurement + ". The draft transaction was rolled back.");

            result.Message = "2D Plan + 4 true Sections placed at 1:" +
                fittedScale + " on " + sheet.SheetNumber +
                " / " + sheet.Name;
            log.Info("2D DRAFT SUCCESS SheetId=" +
                sheet.Id.IntegerValue + " Views=" +
                string.Join(",", result.Views.Select(x => x.Id.IntegerValue)) +
                " Scale=1:" + fittedScale);
            return result;
        }

        private static ViewPlan GetOrCreatePlan(Document doc,
            string name, ViewFamilyType type, Level level)
        {
            View existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (existing != null)
            {
                ViewPlan reuse = existing as ViewPlan;
                if (reuse == null)
                    throw new InvalidOperationException(
                        name + " exists but is not a Floor Plan.");
                return reuse;
            }
            ViewPlan plan = ViewPlan.Create(doc, type.Id, level.Id);
            plan.Name = name;
            return plan;
        }

        private static void ConfigurePlan(ViewPlan plan,
            double minX, double minY, double maxX, double maxY,
            double minZ, double maxZ, double centerZ)
        {
            // PlanViewRange offsets are relative to the associated level,
            // not absolute project coordinates.
            double elevation = plan.GenLevel.Elevation;
            double cut = centerZ;
            if (cut <= minZ + UnitUtil.MmToFt(100) ||
                cut >= maxZ - UnitUtil.MmToFt(100))
                cut = minZ + (maxZ - minZ) * 0.55;
            var range = plan.GetViewRange();
            range.SetLevelId(PlanViewPlane.TopClipPlane,
                plan.GenLevel.Id);
            range.SetLevelId(PlanViewPlane.CutPlane,
                plan.GenLevel.Id);
            range.SetLevelId(PlanViewPlane.BottomClipPlane,
                plan.GenLevel.Id);
            range.SetLevelId(PlanViewPlane.ViewDepthPlane,
                plan.GenLevel.Id);
            range.SetOffset(PlanViewPlane.TopClipPlane,
                maxZ - elevation);
            range.SetOffset(PlanViewPlane.CutPlane,
                cut - elevation);
            range.SetOffset(PlanViewPlane.BottomClipPlane,
                minZ - elevation);
            range.SetOffset(PlanViewPlane.ViewDepthPlane,
                minZ - elevation - UnitUtil.MmToFt(50));
            plan.SetViewRange(range);
            plan.CropBoxActive = true;
            plan.CropBoxVisible = false;

            // XY crop in view-local coordinates; Revit supplies the
            // level-aligned plan transform, typically identity orientation.
            BoundingBoxXYZ crop = plan.CropBox;
            Transform inverse = crop.Transform.Inverse;
            XYZ p0 = inverse.OfPoint(new XYZ(minX, minY, cut));
            XYZ p1 = inverse.OfPoint(new XYZ(maxX, maxY, cut));
            crop.Min = new XYZ(Math.Min(p0.X, p1.X),
                Math.Min(p0.Y, p1.Y), crop.Min.Z);
            crop.Max = new XYZ(Math.Max(p0.X, p1.X),
                Math.Max(p0.Y, p1.Y), crop.Max.Z);
            plan.CropBox = crop;
            plan.Scale = 50;
        }

        private static ViewSection GetOrCreateSection(Document doc,
            string name, ViewFamilyType sectionType,
            Line axis, XYZ midpoint, XYZ outward,
            double bottomZ, double topZ, double pad)
        {
            View existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (existing != null)
            {
                ViewSection reuse = existing as ViewSection;
                if (reuse == null)
                    throw new InvalidOperationException(
                        name + " exists but is not a Section.");
                return reuse;
            }

            // Transform of a section: BasisX points RIGHT on paper;
            // BasisY points UP; BasisZ points OUT toward the viewer.
            XYZ up = XYZ.BasisZ;
            XYZ viewOut = outward.Normalize();
            XYZ right = up.CrossProduct(viewOut).Normalize();
            if (right.GetLength() < 1e-9)
                throw new InvalidOperationException("Invalid section axes.");
            Transform frame = Transform.Identity;
            frame.Origin = new XYZ(midpoint.X, midpoint.Y,
                (bottomZ + topZ) * 0.5);
            frame.BasisX = right;
            frame.BasisY = up;
            frame.BasisZ = viewOut;

            // Tight section with small depth toward the manhole.
            // Depth behind the front wall is intentionally limited to
            // avoid capturing the opposite wall through an opening.
            double halfW = axis.Length * 0.5 + pad;
            double halfH = (topZ - bottomZ) * 0.5;
            var sectionBox = new BoundingBoxXYZ
            {
                Transform = frame,
                Min = new XYZ(-halfW, -halfH,
                    -UnitUtil.MmToFt(350)),
                Max = new XYZ(halfW, halfH,
                    UnitUtil.MmToFt(200))
            };
            ViewSection section = ViewSection.CreateSection(doc,
                sectionType.Id, sectionBox);
            section.Name = name;
            section.CropBoxActive = true;
            section.CropBoxVisible = false;
            section.Scale = 50;
            section.DisplayStyle = DisplayStyle.HLR;
            return section;
        }
    }
}
