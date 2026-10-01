using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class DraftSheetResult
    {
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
            VirtualFoundationResult footprint, DiagnosticLogger log,
            bool forProduction = false)
        {
            if (doc == null || foundation == null || footprint == null ||
                !footprint.Accepted || footprint.Walls.Count != 4)
                throw new InvalidOperationException(
                    "Draft requires a validated four-wall manhole.");

            const double paddingMm = 180;
            double pad = UnitUtil.MmToFt(paddingMm);
            var result = new DraftSheetResult();
            string manholeName = ManholeViewTitleService.Name(
                doc, foundation, log);
            string prefix = "MH_" + foundation.Id.IntegerValue +
                (forProduction ? "_PROD_2D" : "_DRAFT_2D");
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
            ViewFamilyType sectionType = ManholeViewTitleService.RequiredSectionType(doc);
            if (planType == null || sectionType == null)
                throw new InvalidOperationException(
                    "The project needs Floor Plan and Section view types.");

            View planTemplate = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate &&
                    v.Name.Equals("MH_PLAN", StringComparison.OrdinalIgnoreCase));
            View sectionTemplate = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate &&
                    v.Name.Equals("MH_SEC", StringComparison.OrdinalIgnoreCase));
            if (planTemplate == null || sectionTemplate == null)
                throw new InvalidOperationException(
                    "Missing required view templates MH_PLAN / MH_SEC. " +
                    "Load them in this RVT before creating the views.");
            Level level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(x => Math.Abs(x.Elevation - baseBox.Max.Z))
                .FirstOrDefault();
            if (level == null)
                throw new InvalidOperationException("No Level found for draft Floor Plan.");

            string planName = prefix + "_PLAN";
            bool existingPlan = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Any(v => !v.IsTemplate && v.Name == planName);
            ViewPlan plan = GetOrCreatePlan(doc, planName, planType, level, log);
            // Never overwrite the user's scale/crop once views have been
            // generated: subsequent clicks must be idempotent.
            if (!existingPlan)
            {
                // Set geometry before applying the office template; template
                // crop/view-range locks, if any, are preserved thereafter.
                ConfigurePlan(plan, minX - pad, minY - pad, maxX + pad,
                    maxY + pad, minZ - pad, maxZ + pad, centerZ, log);
                plan.Scale = 25;
                PerformanceMeasurement.Call(log, "View.Template.Plan", planName, () => { plan.ViewTemplateId = planTemplate.Id; });
                // Reduce oversized plan viewport bounds caused by
                // section markers/other annotations. This is ONLY for
                // freshly created views; the template may control this
                // setting, in which case the user's template wins.
                try
                {
                    Parameter annotation = plan.get_Parameter(
                        BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                    if (annotation != null && !annotation.IsReadOnly)
                        annotation.Set(1);
                    using (ViewCropRegionShapeManager cropManager =
                        plan.GetCropRegionShapeManager())
                    {
                        if (cropManager.CanHaveAnnotationCrop)
                        {
                            double annotationMargin = UnitUtil.MmToFt(4);
                            cropManager.LeftAnnotationCropOffset =
                                annotationMargin;
                            cropManager.RightAnnotationCropOffset =
                                annotationMargin;
                            cropManager.TopAnnotationCropOffset =
                                annotationMargin;
                            cropManager.BottomAnnotationCropOffset =
                                annotationMargin;
                            log.Info("PLAN ANNOTATION CROP = 4mm (view units) " +
                                "on new plan " + plan.Id.IntegerValue +
                                "; section marks outside may be clipped.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Warn("PLAN annotation crop is controlled by " +
                        "MH_PLAN or is unavailable: " + ex.Message +
                        ". Oversized plans will be skipped rather " +
                        "than changing the approved 1:25 scale.");
                }
            }
            ManholeViewTitleService.UpdateTitle(plan,
                manholeName, 0, foundation.Id.IntegerValue, log);
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

                // A NEW exterior-facing section name prevents silently
                // reusing a previously placed inward-facing section.
                string name = prefix + "_OUT_W" + wallNumber;
                bool alreadyExists = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSection)).Cast<ViewSection>()
                    .Any(v => !v.IsTemplate && v.Name == name);
                ViewSection elevation = GetOrCreateSection(doc, name,
                    sectionType, w.Axis, w.Mid, outward,
                    w.Wall.Width, minZ - pad, maxZ + pad, pad, log);
                if (elevation.GetTypeId() != sectionType.Id)
                    elevation.ChangeTypeId(sectionType.Id);
                if (!alreadyExists)
                {
                    elevation.Scale = 25;
                    PerformanceMeasurement.Call(log, "View.Template.Section", name, () => { elevation.ViewTemplateId = sectionTemplate.Id; });
                }
                ManholeViewTitleService.UpdateTitle(elevation,
                    manholeName, wallNumber,
                    foundation.Id.IntegerValue, log);
                elevations[wallNumber] = elevation;
                log.Info("2D DRAFT EXTERIOR SECTION W" + wallNumber +
                    " WallId=" + w.Wall.Id.IntegerValue +
                    " DirectionInward=" + outward.Negate() +
                    " WallThicknessMm=" +
                        UnitUtil.FtToMm(w.Wall.Width).ToString("0.#") +
                    " ViewId=" + elevation.Id.IntegerValue);
            }
            for (int n = 1; n <= 4; n++)
                result.Views.Add(elevations[n]);

            // DELIBERATE MANUAL SHEET WORKFLOW:
            // Do not create a sheet, viewport or apply automatic layout.
            // Revit will retain the five real, tightly cropped 2D views
            // when the caller commits its single transaction.
            result.Message = "Created/found PLAN (MH_PLAN) and four " +
                "Sections (MH_SEC), default 1:25, for " +
                "Foundation " + foundation.Id.IntegerValue +
                ". No sheet or viewport was created. " +
                "Create your preferred sheet, place the views from the " +
                "Project Browser, and set the layout/scale yourself.";
            log.Info("2D MANUAL LAYOUT READY Foundation=" +
                foundation.Id.IntegerValue + " Views=" +
                string.Join(",", result.Views.Select(x =>
                    x.Name + ":" + x.Id.IntegerValue)));
            return result;
        }

        private static ViewPlan GetOrCreatePlan(Document doc,
            string name, ViewFamilyType type, Level level, DiagnosticLogger log)
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
            ViewPlan plan = PerformanceMeasurement.Call(log, "ViewPlan.Create", name,
                () => ViewPlan.Create(doc, type.Id, level.Id));
            PerformanceMeasurement.Call(log, "ViewPlan.Name", name, () => { plan.Name = name; });
            return plan;
        }

        private static void ConfigurePlan(ViewPlan plan,
            double minX, double minY, double maxX, double maxY,
            double minZ, double maxZ, double centerZ, DiagnosticLogger log)
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
            PerformanceMeasurement.Call(log, "Plan.SetViewRange", plan.Name, () => plan.SetViewRange(range));
            if (!PerformanceMeasurement.CropBeforeActivation)
                PerformanceMeasurement.Call(log, "Plan.CropActive", plan.Name, () => { plan.CropBoxActive = true; });
            PerformanceMeasurement.Call(log, "Plan.CropVisible", plan.Name, () => { plan.CropBoxVisible = false; });

            // XY crop in view-local coordinates; Revit supplies the
            // level-aligned plan transform, typically identity orientation.
            BoundingBoxXYZ crop = PerformanceMeasurement.Call(log, "Plan.GetCrop", plan.Name, () => plan.CropBox);
            Transform inverse = crop.Transform.Inverse;
            XYZ p0 = inverse.OfPoint(new XYZ(minX, minY, cut));
            XYZ p1 = inverse.OfPoint(new XYZ(maxX, maxY, cut));
            crop.Min = new XYZ(Math.Min(p0.X, p1.X),
                Math.Min(p0.Y, p1.Y), crop.Min.Z);
            crop.Max = new XYZ(Math.Max(p0.X, p1.X),
                Math.Max(p0.Y, p1.Y), crop.Max.Z);
            PerformanceMeasurement.Call(log, "Plan.SetCrop", plan.Name, () => { plan.CropBox = crop; });
            if (PerformanceMeasurement.CropBeforeActivation)
                PerformanceMeasurement.Call(log, "Plan.CropActive", plan.Name, () => { plan.CropBoxActive = true; });
            PerformanceMeasurement.Call(log, "Plan.Scale", plan.Name, () => { plan.Scale = 50; });
        }

        private static ViewSection GetOrCreateSection(Document doc,
            string name, ViewFamilyType sectionType,
            Line axis, XYZ midpoint, XYZ outward,
            double wallThicknessFt, double bottomZ, double topZ,
            double pad, DiagnosticLogger log)
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

            // OUTSIDE LOOKING IN:
            // Autodesk ViewSection.CreateSection uses BasisZ as the
            // ACTUAL LOOKING direction (not toward the viewer).
            // 'outward' points from manhole center toward this wall.
            // Thus viewDirection = -outward points from the observer
            // outside the manhole onto its EXTERNAL wall face.
            XYZ up = XYZ.BasisZ;
            XYZ viewDirection = outward.Normalize().Negate();
            XYZ right = up.CrossProduct(viewDirection).Normalize();
            Transform frame = Transform.Identity;
            // Place the cutting plane 40mm OUTSIDE the external face,
            // not at the center of the wall. The view then looks IN.
            double standOff = UnitUtil.MmToFt(40);
            double extensionInside = UnitUtil.MmToFt(60);
            XYZ outerFacePoint = new XYZ(midpoint.X, midpoint.Y,
                (bottomZ + topZ) * 0.5) +
                outward.Normalize() * (wallThicknessFt * 0.5);
            frame.Origin = outerFacePoint +
                outward.Normalize() * standOff;
            frame.BasisX = right;
            frame.BasisY = up;
            frame.BasisZ = viewDirection;

            // Positive view depth runs from the EXTERIOR camera plane
            // into the wall, and stops shortly behind its inner face.
            // This ensures the rear/opposite wall cannot cover the
            // external facade when a manhole is small.
            double halfW = axis.Length * 0.5 + pad;
            double halfH = (topZ - bottomZ) * 0.5;
            var sectionBox = new BoundingBoxXYZ
            {
                Transform = frame,
                Min = new XYZ(-halfW, -halfH, 0),
                Max = new XYZ(halfW, halfH,
                    standOff + wallThicknessFt + extensionInside)
            };
            ViewSection section = PerformanceMeasurement.Call(log, "ViewSection.CreateSection", name,
                () => ViewSection.CreateSection(doc, sectionType.Id, sectionBox));
            section.Name = name;
            section.CropBoxActive = true;
            section.CropBoxVisible = false;
            section.Scale = 50;
            section.DisplayStyle = DisplayStyle.HLR;
            return section;
        }
    }
}
