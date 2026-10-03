using System;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static partial class DraftManholeSheetService
    {
        // Explicit selected-only documentation exception. These envelope sides are NOT
        // recovered walls and must never enter opening, dimension or repair algorithms.
        public static DraftSheetResult GenerateIncomplete(Document doc, Element foundation, DiagnosticLogger log)
        {
            var box = foundation.get_BoundingBox(null)
                ?? throw new InvalidOperationException("The selected base has no bounds.");
            double pad = UnitUtil.MmToFt(180);
            // Nearby walls supply height only; no ownership/four-wall claim is made.
            // Reject walls starting far above/below the selected foundation.
            double tolerance = UnitUtil.MmToFt(100);
            var heights = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Select(w => new { Wall = w, Box = w.get_BoundingBox(null) })
                .Where(x => x.Box != null &&
                    x.Box.Max.X > box.Min.X && x.Box.Min.X < box.Max.X &&
                    x.Box.Max.Y > box.Min.Y && x.Box.Min.Y < box.Max.Y &&
                    x.Box.Min.Z >= box.Min.Z - tolerance && x.Box.Min.Z <= box.Max.Z + tolerance &&
                    x.Box.Max.Z > box.Max.Z + tolerance).ToList();
            if (heights.Count == 0)
                throw new InvalidOperationException("Manual views need at least one wall over the base to determine height. No views were created.");
            double bottom = Math.Min(box.Min.Z, heights.Min(x => x.Box.Min.Z)) - pad;
            double top = heights.Max(x => x.Box.Max.Z) + pad;
            double width = box.Max.X - box.Min.X, depth = box.Max.Y - box.Min.Y;
            if (width <= tolerance || depth <= tolerance || top <= bottom)
                throw new InvalidOperationException("The base envelope is too small for manual views.");
            var center = (box.Min + box.Max) * .5;
            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var planTemplate = allViews.SingleOrDefault(v => v.IsTemplate && v.Name.Equals("MH_PLAN", StringComparison.OrdinalIgnoreCase));
            var sectionTemplate = allViews.SingleOrDefault(v => v.IsTemplate && v.Name.Equals("MH_SEC", StringComparison.OrdinalIgnoreCase));
            if (planTemplate == null || sectionTemplate == null)
                throw new InvalidOperationException("Load MH_PLAN and MH_SEC templates first.");
            var planType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan)
                ?? throw new InvalidOperationException("No Floor Plan view type.");
            var sectionType = ManholeViewTitleService.RequiredSectionType(doc);
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - box.Max.Z)).FirstOrDefault()
                ?? throw new InvalidOperationException("No Level found.");
            string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D";
            string title = ManholeViewTitleService.Name(doc, foundation, log);
            var result = new DraftSheetResult();
            var plan = GetOrCreatePlan(doc, prefix + "_PLAN", planType, level, log);
            if (!allViews.Any(v => v.Id == plan.Id))
            {
                ConfigurePlan(plan, box.Min.X - pad, box.Min.Y - pad, box.Max.X + pad,
                    box.Max.Y + pad, bottom, top, (bottom + top) * .5, log);
                plan.Scale = 25;
                plan.ViewTemplateId = planTemplate.Id;
            }
            ManholeViewTitleService.UpdateTitle(plan, title, 0, foundation.Id.IntegerValue, log);
            result.Views.Add(plan);
            // Project-axis envelope: north/east/west/south = W1/W2/W3/W4.
            // Full-depth exterior overviews intentionally include the surviving body.
            var normals = new[] { XYZ.BasisY, XYZ.BasisX, XYZ.BasisX.Negate(), XYZ.BasisY.Negate() };
            for (int i = 0; i < 4; i++)
            {
                var normal = normals[i];
                bool eastWest = Math.Abs(normal.X) > .5;
                double along = eastWest ? depth : width;
                double into = eastWest ? width : depth;
                var midpoint = center + normal * (into * .5);
                var right = XYZ.BasisZ.CrossProduct(normal);
                var axis = Line.CreateBound(midpoint - right * (along * .5), midpoint + right * (along * .5));
                string name = prefix + "_OUT_W" + (i + 1);
                var view = GetOrCreateSection(doc, name, sectionType, axis, midpoint, normal,
                    0, bottom, top, pad, log, overviewDepthFt: into + pad);
                if (!allViews.Any(v => v.Id == view.Id))
                {
                    view.Scale = 25;
                    view.ViewTemplateId = sectionTemplate.Id;
                }
                ManholeViewTitleService.UpdateTitle(view, title, i + 1, foundation.Id.IntegerValue, log);
                result.Views.Add(view);
            }
            ManholeViewPresentationService.CleanPlan(doc, plan, log);
            log.Warn("MANUAL INCOMPLETE-WALL VIEWS Foundation=" + foundation.Id.IntegerValue +
                " HeightCandidateWalls=" + string.Join(",", heights.Select(x => x.Wall.Id.IntegerValue)) +
                "; project-axis base envelope, full-depth exterior overviews; verify crop/visibility manually. No validated footprint, openings or dimensions.");
            return result;
        }
    }
}
