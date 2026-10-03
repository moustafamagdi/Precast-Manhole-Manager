using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Hatco.PrecastManholeManager.Services
{
    internal static partial class OpeningDimensionService
    {
        internal static List<string> AuditExisting(Document doc, Element foundation, View view,
            IList<Dimension> dimensions, IList<Opening> expectedOpenings, IList<Wall> walls)
        {
            var errors = new List<string>();
            var owned = dimensions.Where(d => d.OwnerViewId == view.Id &&
                (IsOwned(d, foundation.UniqueId) || IsOwned(d, foundation.UniqueId, true))).ToList();
            int body = owned.Count(d => IsOwned(d, foundation.UniqueId, true));
            if (body != (view is ViewPlan ? 6 : 3)) errors.Add("DIM_BODY_COUNT: " + view.Name + " has " + body + " tool body/base strings; expected " + (view is ViewPlan ? 6 : 3) + ". Manual dimensions need separate acceptance.");
            int openings = owned.Count - body;
            int expectedOpeningStrings = expectedOpenings.Count == 0 ? 0 : expectedOpenings.Count + 1;
            if (openings != expectedOpeningStrings) errors.Add("DIM_OPENING_COUNT: " + view.Name + " has " + openings + "; expected " + expectedOpeningStrings + ".");
            if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Dimensions))) errors.Add("DIM_CATEGORY_HIDDEN: " + view.Name);
            var visible = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).ToElementIds());
            foreach (var opening in expectedOpenings)
            {
                var cuts = owned.Where(d => IsOwned(d, foundation.UniqueId)).ToList();
                foreach (var axis in new[] { view.RightDirection, view.UpDirection })
                    if (!cuts.Any(d => d.Curve is Line l && Math.Abs(l.Direction.DotProduct(axis)) > .9999 &&
                        d.References.Cast<Reference>().Count(r => r.ElementId == opening.Id) >= 2))
                        errors.Add("DIM_OPENING_COVERAGE: " + view.Name + " Opening=" + opening.Id + "; missing width/height references.");
            }
            var bodyDimensions = owned.Where(d => IsOwned(d, foundation.UniqueId, true)).ToList();
            var duplicateBodyReferences = bodyDimensions.GroupBy(d => string.Join("|", d.References.Cast<Reference>()
                .Select(r => r.ConvertToStableRepresentation(doc)).OrderBy(x => x, StringComparer.Ordinal)))
                .Any(g => g.Count() > 1);
            if (duplicateBodyReferences) errors.Add("DIM_DUPLICATE_BODY_REFERENCES: " + view.Name + "; repeated strings cannot stand in for missing inner/outer dimensions.");
            var wallIds = new HashSet<ElementId>(walls.Select(w => w.Id));
            var planAxis = (walls[0].Location as LocationCurve)?.Curve as Line;
            XYZ horizontal = view is ViewPlan && planAxis != null ? planAxis.Direction : view.RightDirection;
            XYZ vertical = view is ViewPlan ? XYZ.BasisZ.CrossProduct(horizontal).Normalize() : view.UpDirection;
            Func<Dimension, XYZ, bool, bool> matchesRole = (d, axis, isBase) => d.Curve is Line l &&
                Math.Abs(l.Direction.DotProduct(axis)) > .9999 && d.References.Size >= 2 &&
                d.References.Cast<Reference>().All(r => isBase ? r.ElementId == foundation.Id : wallIds.Contains(r.ElementId));
            if (view is ViewPlan)
            {
                if (bodyDimensions.Count(d => matchesRole(d, horizontal, false)) != 2 || bodyDimensions.Count(d => matchesRole(d, vertical, false)) != 2)
                    errors.Add("DIM_PLAN_BODY_COVERAGE: " + view.Name + "; inner/outer dimensions required in both wall directions.");
            }
            else if (bodyDimensions.Count(d => matchesRole(d, vertical, false)) != 1)
                errors.Add("DIM_BODY_HEIGHT_COVERAGE: " + view.Name);
            if (bodyDimensions.Count(d => matchesRole(d, horizontal, true)) != 1 || bodyDimensions.Count(d => matchesRole(d, vertical, true)) != 1)
                errors.Add("DIM_BASE_COVERAGE: " + view.Name);
            foreach (var dimension in owned)
            {
                string id = dimension.Id.ToString();
                try
                {
                    if (dimension.IsHidden(view) || !visible.Contains(dimension.Id)) errors.Add("DIM_NOT_VISIBLE: " + id);
                    var type = doc.GetElement(dimension.GetTypeId()) as DimensionType;
                    if (type == null || type.Name != "HTC_DIM_1.8mm") errors.Add("DIM_TYPE: " + id);
                    var line = dimension.Curve as Line;
                    if (line == null) throw new InvalidOperationException("Only linear dimensions can be verified.");
                    var coordinates = new List<double>();
                    foreach (Reference reference in dimension.References)
                    {
                        var element = doc.GetElement(reference.ElementId);
                        if (element == null) throw new InvalidOperationException("Missing referenced element.");
                        string stable = reference.ConvertToStableRepresentation(doc);
                        var face = Faces(element, view, line.Direction, view.UpDirection)
                            .FirstOrDefault(f => f.Reference.ConvertToStableRepresentation(doc) == stable);
                        if (face == null || Math.Abs(face.Normal.DotProduct(line.Direction)) < 0.9999)
                            throw new InvalidOperationException("Cannot verify a perpendicular model-face reference.");
                        coordinates.Add(face.X);
                    }
                    var values = dimension.NumberOfSegments == 0 ? new[] { dimension.Value } :
                        dimension.Segments.Cast<DimensionSegment>().Select(x => x.Value).ToArray();
                    // Revit can enumerate references in reverse; compare the geometric chain in ascending order.
                    if (coordinates.Count > 1 && coordinates[0] > coordinates[coordinates.Count - 1]) { coordinates.Reverse(); Array.Reverse(values); }
                    if (!SegmentsMatch(coordinates.ToArray(), values)) errors.Add("DIM_VALUE_MISMATCH: " + id);
                    bool overridden = dimension.NumberOfSegments == 0 ? !string.IsNullOrWhiteSpace(dimension.ValueOverride) :
                        dimension.Segments.Cast<DimensionSegment>().Any(x => !string.IsNullOrWhiteSpace(x.ValueOverride));
                    if (overridden) errors.Add("DIM_VALUE_OVERRIDE: " + id);
                    var box = dimension.get_BoundingBox(view);
                    if (box == null) errors.Add("DIM_VISIBILITY_UNVERIFIED: " + id + " has no bounds; inspect the view. No dimensions were replaced.");
                    else if (view.CropBoxActive && view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.AsInteger() == 1)
                    {
                        var crop = view.CropBox;
                        using (var manager = view.GetCropRegionShapeManager())
                        foreach (var point in DrawingAuditService.Corners(box))
                        {
                            var local = crop.Transform.Inverse.OfPoint(point);
                            if (!WithinAnnotationAxis(local.X, crop.Min.X, crop.Max.X, manager.LeftAnnotationCropOffset, manager.RightAnnotationCropOffset, view.Scale) ||
                                !WithinAnnotationAxis(local.Y, crop.Min.Y, crop.Max.Y, manager.BottomAnnotationCropOffset, manager.TopAnnotationCropOffset, view.Scale))
                            { errors.Add("DIM_ANNOTATION_CLIPPED: " + id); break; }
                        }
                    }
                }
                catch (Exception ex) { errors.Add("DIM_REFERENCE_UNVERIFIED: " + id + " " + ex.Message); }
            }
            return errors;
        }
    }
}
