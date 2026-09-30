using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static partial class OpeningDimensionService
    {
        private static readonly Guid BodySchemaId = new Guid("BD5DC873-5B5E-4680-B307-479761242958");

        private static string GenerateBody(Document doc, Element foundation,
            VirtualFoundationResult footprint, List<ViewSection> sections,
            DimensionType type, DiagnosticLogger log)
        {
            string planName = "MH_" + foundation.Id.IntegerValue + "_PROD_2D_PLAN";
            ViewPlan plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate && v.Name == planName);
            var views = sections.Cast<View>().ToList();
            if (plan != null) views.Insert(0, plan);
            int created = 0;
            var errors = new List<string>();
            if (plan == null) errors.Add("Production plan is missing.");
            foreach (View view in views)
            {
                using (var tx = new Transaction(doc, "HATCO - Body and Base Dimensions"))
                {
                    tx.Start();
                    var options = tx.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new OpeningFailurePreprocessor(log));
                    options.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(options);
                    try
                    {
                        if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Dimensions)))
                            throw new InvalidOperationException("Enable Dimensions in the view template.");
                        var old = new FilteredElementCollector(doc).OfClass(typeof(Dimension))
                            .Cast<Dimension>().Where(d => d.OwnerViewId == view.Id &&
                                IsOwned(d, foundation.UniqueId, true)).Select(d => d.Id).ToList();
                        if (old.Count > 0) doc.Delete(old);
                        int count = view is ViewPlan
                            ? CreatePlanBody(doc, foundation, footprint, view, type, log)
                            : CreateSectionBody(doc, foundation, footprint, view, type, log);
                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit rejected the body dimensions.");
                        created += count;
                        log.Info("BODY DIMENSIONS COMMITTED View=" + view.Name + " Count=" + count);
                    }
                    catch (Exception ex)
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        errors.Add(view.Name + ": " + ex.Message);
                        log.Error("BODY DIMENSIONS rolled back for " + view.Name, ex);
                    }
                }
            }
            return "Body/base dimensions: " + created + " strings created; " + errors.Count +
                " view(s) need review." + (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors));
        }

        private static List<Boundary> Planes(List<FaceReference> faces, XYZ axis, bool horizontal)
        {
            var ordered = faces.Where(f => Math.Abs(f.Normal.DotProduct(axis)) > 0.9999)
                .OrderBy(f => horizontal ? f.X : f.Y).ToList();
            var result = new List<Boundary>();
            foreach (var face in ordered)
            {
                double position = horizontal ? face.X : face.Y;
                if (result.Count == 0 || position - result.Last().Position > UnitUtil.MmToFt(ToleranceMm))
                    result.Add(new Boundary { Position = position, Reference = face.Reference });
            }
            return result;
        }

        private static List<Boundary> Extents(List<FaceReference> faces, XYZ axis, bool horizontal)
        {
            var planes = Planes(faces, axis, horizontal);
            if (planes.Count < 2)
                throw new InvalidOperationException("Missing opposite model faces for body/base dimension.");
            return new List<Boundary> { planes.First(), planes.Last() };
        }

        private static int CreatePlanBody(Document doc, Element foundation,
            VirtualFoundationResult footprint, View view, DimensionType type, DiagnosticLogger log)
        {
            // Use wall-aligned coordinates so rotated manholes retain their true sizes.
            Line line = (footprint.Walls[0].Location as LocationCurve)?.Curve as Line;
            if (line == null) throw new InvalidOperationException("Straight wall axes required.");
            XYZ right = new XYZ(line.Direction.X, line.Direction.Y, 0).Normalize();
            XYZ up = XYZ.BasisZ.CrossProduct(right).Normalize();
            var sides = new List<FaceReference>();
            foreach (Wall wall in footprint.Walls)
            {
                // Major side faces only: exclude opening reveals and joined wall ends.
                var references = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Interior)
                    .Concat(HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior));
                foreach (Reference reference in references)
                {
                    var face = wall.GetGeometryObjectFromReference(reference) as PlanarFace;
                    if (face == null) continue;
                    XYZ point = face.Origin - view.Origin;
                    sides.Add(new FaceReference { Reference = reference, Normal = face.FaceNormal,
                        X = point.DotProduct(right), Y = point.DotProduct(up) });
                }
            }
            var x = Planes(sides, right, true);
            var y = Planes(sides, up, false);
            if (x.Count != 4 || y.Count != 4)
                throw new InvalidOperationException("Expected four distinct inner/outer wall planes in each plan direction.");
            var baseFaces = Faces(foundation, view, right, up);
            var bx = Extents(baseFaces, right, true);
            var by = Extents(baseFaces, up, false);
            double gap = UnitUtil.MmToFt(3 * view.Scale);
            double top = Math.Max(y.Last().Position, by.Last().Position);
            double edge = Math.Max(x.Last().Position, bx.Last().Position);
            CreateString(doc, foundation, view, type, new List<Boundary> { x[1], x[2] }, true, top + gap, log, true, right, up);
            CreateString(doc, foundation, view, type, new List<Boundary> { x[0], x[3] }, true, top + 2 * gap, log, true, right, up);
            CreateString(doc, foundation, view, type, new List<Boundary> { y[1], y[2] }, false, edge + gap, log, true, right, up);
            CreateString(doc, foundation, view, type, new List<Boundary> { y[0], y[3] }, false, edge + 2 * gap, log, true, right, up);
            CreateString(doc, foundation, view, type, bx, true, Math.Min(y[0].Position, by[0].Position) - gap, log, true, right, up);
            CreateString(doc, foundation, view, type, by, false, Math.Min(x[0].Position, bx[0].Position) - gap, log, true, right, up);
            return 6;
        }

        private static int CreateSectionBody(Document doc, Element foundation,
            VirtualFoundationResult footprint, View view, DimensionType type, DiagnosticLogger log)
        {
            if (Math.Abs(view.UpDirection.DotProduct(XYZ.BasisZ)) < 0.9999)
                throw new InvalidOperationException("Vertical section required for body height.");
            // Match the same angular wall numbering used when creating the production sections.
            var ordered = footprint.Walls.OrderBy(w =>
            {
                var axis = (w.Location as LocationCurve)?.Curve as Line;
                if (axis == null) throw new InvalidOperationException("Straight wall axis required.");
                XYZ delta = (axis.GetEndPoint(0) + axis.GetEndPoint(1)) * 0.5 - footprint.VirtualCenter;
                double angle = Math.Atan2(delta.X, delta.Y);
                return angle < 0 ? angle + Math.PI * 2 : angle;
            }).ToList();
            int number = int.Parse(view.Name.Substring(view.Name.LastIndexOf('W') + 1));
            Wall wall = ordered[Array.IndexOf(new[] { 1, 2, 4, 3 }, number)];
            var faces = Faces(wall, view);
            var baseFaces = Faces(foundation, view);
            var height = Extents(faces, view.UpDirection, false);
            var thickness = Extents(baseFaces, view.UpDirection, false);
            var width = Extents(baseFaces, view.RightDirection, true);
            double gap = UnitUtil.MmToFt(3 * view.Scale);
            double left = Math.Min(faces.Min(f => f.MinX), width[0].Position);
            CreateString(doc, foundation, view, type, height, false, left - gap, log, true);
            CreateString(doc, foundation, view, type, thickness, false, left - 2 * gap, log, true);
            CreateString(doc, foundation, view, type, width, true, thickness[0].Position - gap, log, true);
            return 3;
        }
    }
}
