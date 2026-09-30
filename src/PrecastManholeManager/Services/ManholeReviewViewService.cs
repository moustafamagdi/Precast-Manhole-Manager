using System;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ManholeReviewViewService
    {
        public static View3D CreateOrUpdate(Document doc,
            Element foundation, VirtualFoundationResult footprint,
            ManholeReviewIssue issue, double marginMm, DiagnosticLogger log)
        {
            if (doc == null || foundation == null || issue == null)
                throw new ArgumentNullException("Review 3D needs a foundation and issue.");
            if (marginMm < 50 || marginMm > 2000)
                throw new ArgumentOutOfRangeException(nameof(marginMm));

            BoundingBoxXYZ fb = foundation.get_BoundingBox(null);
            if (fb == null)
                throw new InvalidOperationException(
                    "Foundation has no bounding box.");

            // XY footprint from the validated four walls (cropped foundation
            // may have an incorrect center); Z includes footing and wall tops.
            bool validated = footprint != null && footprint.Accepted &&
                footprint.Walls.Count == 4;
            double minX, minY, maxX, maxY, minZ, maxZ;
            if (validated)
            {
                var bounds = footprint.Walls
                    .Select(w => w.get_BoundingBox(null))
                    .Where(x => x != null).ToList();
                if (bounds.Count != 4) validated = false;
                if (validated)
                {
                    minX = bounds.Min(x => x.Min.X);
                    minY = bounds.Min(x => x.Min.Y);
                    maxX = bounds.Max(x => x.Max.X);
                    maxY = bounds.Max(x => x.Max.Y);
                    minZ = Math.Min(fb.Min.Z, bounds.Min(x => x.Min.Z));
                    maxZ = Math.Max(fb.Max.Z, bounds.Max(x => x.Max.Z));
                }
                else
                {
                    minX = fb.Min.X; minY = fb.Min.Y;
                    maxX = fb.Max.X; maxY = fb.Max.Y;
                    minZ = fb.Min.Z; maxZ = fb.Max.Z;
                }
            }
            else
            {
                // When the footprint itself is ambiguous, a fallback view
                // still gives the user a reviewable starting point.
                minX = fb.Min.X; minY = fb.Min.Y;
                maxX = fb.Max.X; maxY = fb.Max.Y;
                minZ = fb.Min.Z; maxZ = fb.Max.Z;
            }

            double margin = UnitUtil.MmToFt(marginMm);
            // On a shifted cropped foundation, do NOT include the entire
            // nearby foundation horizontal envelope; stick to wall footprint.
            var box = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(minX - margin, minY - margin,
                    minZ - margin),
                Max = new XYZ(maxX + margin, maxY + margin,
                    maxZ + margin)
            };

            View3D view = null;
            if (issue.ViewId > 0)
                view = doc.GetElement(new ElementId(issue.ViewId))
                    as View3D;
            if (view != null && view.IsTemplate)
                view = null;
            if (view == null)
            {
                ViewFamilyType type = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                if (type == null)
                    throw new InvalidOperationException(
                        "No 3D ViewFamilyType available.");
                view = View3D.CreateIsometric(doc, type.Id);
            }

            string name = "MH_REVIEW_" +
                foundation.Id.IntegerValue.ToString("D7");
            if (view.Name != name)
            {
                // Preserve another project's view names; Revit rejects
                // duplicate view names, so append this view's own ID if needed.
                bool collision = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D)).Cast<View3D>()
                    .Any(x => !x.IsTemplate && x.Id != view.Id &&
                        x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                view.Name = collision
                    ? name + "_" + view.Id.IntegerValue : name;
            }
            view.IsSectionBoxActive = true;
            view.SetSectionBox(box);
            issue.ViewId = view.Id.IntegerValue;
            issue.ViewName = view.Name;
            log?.Info("REVIEW 3D VIEW Foundation=" +
                foundation.Id.IntegerValue + " ViewId=" +
                view.Id.IntegerValue + " Name=" + view.Name +
                " MarginMm=" + marginMm +
                " VirtualFootprint=" + validated);
            return view;
        }
    }
}
