using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    // Model-face references only: no detail-line proxies or overridden dimension values.
    internal static class OpeningDimensionService
    {
        private static readonly Guid SchemaId = new Guid("34DFA2A7-01AC-43A2-91B2-709CAB52D53E");
        private const double ToleranceMm = 0.5;
        private sealed class FaceReference
        {
            public Reference Reference;
            public XYZ Normal;
            public double X, Y, MinX, MaxX, MinY, MaxY;
        }
        private sealed class Boundary
        {
            public double Position;
            public Reference Reference;
        }
        private sealed class OpeningInfo
        {
            public Opening Element;
            public ManagedOpeningData Data;
            public double Left, Right, Bottom, Top;
            public string Code;
        }

        // Caller owns the transaction, so a failed production update restores these annotations.
        public static void RemoveOwned(Document doc, Element foundation)
        {
            string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D_OUT_W";
            var views = new HashSet<int>(new FilteredElementCollector(doc).OfClass(typeof(ViewSection))
                .Cast<ViewSection>().Where(v => Enumerable.Range(1, 4).Any(n => v.Name == prefix + n))
                .Select(v => v.Id.IntegerValue));
            var ids = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>()
                .Where(d => views.Contains(d.OwnerViewId.IntegerValue) && IsOwned(d, foundation.UniqueId))
                .Select(d => d.Id).ToList();
            if (ids.Count > 0) doc.Delete(ids);
        }

        public static string Generate(Document doc, Element foundation, DiagnosticLogger log)
        {
            string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D_OUT_W";
            var sections = new FilteredElementCollector(doc).OfClass(typeof(ViewSection))
                .Cast<ViewSection>().Where(v => !v.IsTemplate &&
                    Enumerable.Range(1, 4).Any(n => v.Name == prefix + n)).ToList();
            var all = new List<OpeningInfo>();
            foreach (Opening opening in new FilteredElementCollector(doc).OfClass(typeof(Opening)))
            {
                ManagedOpeningData data;
                if (OpeningStorageService.TryRead(opening, out data))
                    all.Add(new OpeningInfo { Element = opening, Data = data });
            }
            // Recover the selected manhole footprint; never trust wall numbering alone across manholes.
            var footprint = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
            if (!footprint.Accepted) return "Dimensions: footprint requires review. " + footprint.Reason;
            var wallIds = new HashSet<int>(footprint.Walls.Select(w => w.Id.IntegerValue));
            all = all.Where(x => wallIds.Contains(x.Data.HostWallId)).OrderBy(x => x.Data.WallNumber)
                .ThenBy(x => OpeningOffset(doc, x)).ToList();
            for (int i = 0; i < all.Count; i++) all[i].Code = "O" + (i + 1).ToString("00");
            if (sections.Count != 4 || all.Count == 0)
                return "Dimensions: generate the manhole's production views and openings first.";
            var type = new FilteredElementCollector(doc).OfClass(typeof(DimensionType))
                .Cast<DimensionType>().FirstOrDefault(x => x.StyleType == DimensionStyleType.Linear);
            if (type == null) return "Dimensions: load a linear dimension type first.";
            int created = 0, failed = 0;
            var diagnostics = new List<string>();
            log.WriteHeader("ASSOCIATIVE OPENING DIMENSIONS");
            foreach (ViewSection view in sections.OrderBy(v => v.Name))
            {
                int number = int.Parse(view.Name.Substring(prefix.Length));
                var rows = all.Where(x => x.Data.WallNumber == number).ToList();
                using (var tx = new Transaction(doc, "HATCO - Opening Dimensions W" + number))
                {
                    tx.Start();
                    var options = tx.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new OpeningFailurePreprocessor(log));
                    options.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(options);
                    try
                    {
                        var old = new FilteredElementCollector(doc).OfClass(typeof(Dimension))
                            .Cast<Dimension>().Where(d => d.OwnerViewId == view.Id &&
                                IsOwned(d, foundation.UniqueId)).Select(d => d.Id).ToList();
                        if (old.Count > 0) doc.Delete(old);
                        int count = rows.Count == 0 ? 0 : CreateForWall(doc, foundation, view, rows, type, log);
                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit rejected the dimension transaction.");
                        created += count;
                        log.Info("DIMENSIONS COMMITTED W" + number + " Count=" + count);
                    }
                    catch (Exception ex)
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        failed++;
                        diagnostics.Add("W" + number + ": " + ex.Message);
                        log.Error("DIMENSIONS W" + number + " rolled back; production geometry retained.", ex);
                    }
                }
            }
            return "Dimensions: " + created + " strings created; " + failed + " wall(s) need review." +
                (diagnostics.Count == 0 ? "" : "\n" + string.Join("\n", diagnostics));
        }

        private static int CreateForWall(Document doc, Element foundation, ViewSection view,
            List<OpeningInfo> rows, DimensionType type, DiagnosticLogger log)
        {
            if (Math.Abs(view.UpDirection.DotProduct(XYZ.BasisZ)) < 0.9999)
                throw new InvalidOperationException("Only vertical exterior sections are supported.");
            if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Dimensions)))
                throw new InvalidOperationException("Enable Dimensions in the section template.");
            Wall wall = doc.GetElement(new ElementId(rows[0].Data.HostWallId)) as Wall;
            if (wall == null || rows.Any(r => r.Data.HostWallId != wall.Id.IntegerValue))
                throw new InvalidOperationException("Ambiguous host wall for this section.");
            var faces = Faces(wall, view);
            var foundationFaces = Faces(foundation, view);
            var horizontalFaces = faces.Where(f => Math.Abs(f.Normal.DotProduct(view.RightDirection)) > 0.9999).ToList();
            if (horizontalFaces.Count < 2) throw new InvalidOperationException("Wall end face references unavailable.");
            var first = horizontalFaces.OrderBy(f => f.X).First();
            var last = horizontalFaces.OrderByDescending(f => f.X).First();
            var chain = new List<Boundary> {
                new Boundary { Position = first.X, Reference = first.Reference },
                new Boundary { Position = last.X, Reference = last.Reference }
            };
            foreach (var row in rows)
            {
                if (!row.Element.IsRectBoundary || row.Element.BoundaryRect.Count != 2)
                    throw new InvalidOperationException("Only rectangular native openings are supported.");
                XYZ p = row.Element.BoundaryRect[0] - view.Origin;
                XYZ q = row.Element.BoundaryRect[1] - view.Origin;
                row.Left = Math.Min(p.DotProduct(view.RightDirection), q.DotProduct(view.RightDirection));
                row.Right = Math.Max(p.DotProduct(view.RightDirection), q.DotProduct(view.RightDirection));
                row.Bottom = Math.Min(p.DotProduct(view.UpDirection), q.DotProduct(view.UpDirection));
                row.Top = Math.Max(p.DotProduct(view.UpDirection), q.DotProduct(view.UpDirection));
                double midY = (row.Bottom + row.Top) / 2;
                chain.Add(Find(faces, view.RightDirection, true, row.Left, midY));
                chain.Add(Find(faces, view.RightDirection, true, row.Right, midY));
            }
            // A single horizontal chain avoids stacking overlapping wall-origin dimensions.
            double wallBottom = faces.Min(f => f.MinY);
            double gap = UnitUtil.MmToFt(3 * view.Scale);
            CreateString(doc, foundation, view, type, chain, true, wallBottom - gap, null, log);
            int count = 1;
            foreach (var row in rows.OrderBy(r => r.Left))
            {
                double centerX = (row.Left + row.Right) / 2;
                double baseY = (new XYZ(0, 0, foundation.get_BoundingBox(null).Max.Z) - view.Origin)
                    .DotProduct(view.UpDirection);
                var vertical = new List<Boundary> {
                    Find(foundationFaces, view.UpDirection, false, baseY, centerX),
                    Find(faces, view.UpDirection, false, row.Bottom, centerX),
                    Find(faces, view.UpDirection, false, row.Top, centerX)
                };
                // The reference line is beside each opening, inside the existing crop padding.
                CreateString(doc, foundation, view, type, vertical, false,
                    row.Right + gap, row.Code, log);
                count++;
            }
            return count;
        }

        private static Boundary Find(List<FaceReference> faces, XYZ axis, bool horizontal,
            double position, double crossPosition)
        {
            double tolerance = UnitUtil.MmToFt(ToleranceMm);
            var face = faces.Where(f => Math.Abs(f.Normal.DotProduct(axis)) > 0.9999 &&
                Math.Abs((horizontal ? f.X : f.Y) - position) <= tolerance &&
                crossPosition >= (horizontal ? f.MinY : f.MinX) - tolerance &&
                crossPosition <= (horizontal ? f.MaxY : f.MaxX) + tolerance)
                .OrderBy(f => Math.Abs((horizontal ? f.X : f.Y) - position)).FirstOrDefault();
            if (face == null) throw new InvalidOperationException("Missing model face reference at " +
                UnitUtil.FtToMm(position).ToString("0.#") + " mm in section coordinates.");
            return new Boundary { Position = horizontal ? face.X : face.Y, Reference = face.Reference };
        }

        private static void CreateString(Document doc, Element foundation, ViewSection view,
            DimensionType type, List<Boundary> boundaries, bool horizontal, double offset,
            string label, DiagnosticLogger log)
        {
            var sorted = boundaries.OrderBy(b => b.Position).ToList();
            var distinct = new List<Boundary>();
            foreach (var b in sorted)
                if (distinct.Count == 0 || b.Position - distinct.Last().Position > UnitUtil.MmToFt(ToleranceMm))
                    distinct.Add(b);
            if (distinct.Count < 2) throw new InvalidOperationException("Insufficient distinct dimension references.");
            XYZ axis = horizontal ? view.RightDirection : view.UpDirection;
            XYZ cross = horizontal ? view.UpDirection : view.RightDirection;
            XYZ p = view.Origin + axis * distinct.First().Position + cross * offset;
            XYZ q = view.Origin + axis * distinct.Last().Position + cross * offset;
            var references = new ReferenceArray();
            foreach (var b in distinct) references.Append(b.Reference);
            Dimension dimension = doc.Create.NewDimension(view, Line.CreateBound(p, q), references, type);
            if (dimension == null) throw new InvalidOperationException("Revit did not create the dimension.");
            doc.Regenerate();
            if (!dimension.AreReferencesAvailable)
                throw new InvalidOperationException("Dimension references cannot be resolved.");
            var measured = dimension.NumberOfSegments == 0 ? new List<double?> { dimension.Value } :
                dimension.Segments.Cast<DimensionSegment>().Select(segment => segment.Value).ToList();
            if (!SegmentsMatch(distinct.Select(b => b.Position).ToArray(), measured.ToArray()))
                throw new InvalidOperationException("Dimension values do not match the measured model faces.");
            // Label the source without replacing the measured dimension value.
            if (!string.IsNullOrEmpty(label))
            {
                if (dimension.NumberOfSegments == 0) dimension.Above = label;
                else dimension.Segments.Cast<DimensionSegment>().Last().Above = label;
            }
            doc.Regenerate();
            if (view.CropBoxActive)
            {
                BoundingBoxXYZ box = dimension.get_BoundingBox(view);
                BoundingBoxXYZ crop = view.CropBox;
                if (box == null) throw new InvalidOperationException("Dimension has no visible bounds in this section.");
                Transform toCrop = crop.Transform.Inverse;
                foreach (double x in new[] { box.Min.X, box.Max.X })
                foreach (double y in new[] { box.Min.Y, box.Max.Y })
                foreach (double z in new[] { box.Min.Z, box.Max.Z })
                {
                    XYZ local = toCrop.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z)));
                    if (local.X < crop.Min.X || local.X > crop.Max.X ||
                        local.Y < crop.Min.Y || local.Y > crop.Max.Y)
                        throw new InvalidOperationException("Dimension extends outside the section crop. " +
                            "Expand the section crop and retry Update Opening Dimensions.");
                }
            }
            Mark(dimension, foundation.UniqueId);
            log.Info("ASSOCIATIVE DIMENSION Id=" + dimension.Id.IntegerValue + " View=" + view.Id.IntegerValue +
                " Axis=" + (horizontal ? "H" : "V") + " ValuesMm=" +
                string.Join(",", measured.Select(v => UnitUtil.FtToMm(v.Value).ToString("0.#"))));
        }

        internal static bool SegmentsMatch(double[] orderedCoordinatesFt, double?[] measuredFt)
        {
            if (orderedCoordinatesFt == null || measuredFt == null || orderedCoordinatesFt.Length < 2 ||
                measuredFt.Length != orderedCoordinatesFt.Length - 1) return false;
            for (int i = 0; i < orderedCoordinatesFt.Length; i++)
                if (double.IsNaN(orderedCoordinatesFt[i]) || double.IsInfinity(orderedCoordinatesFt[i])) return false;
            for (int i = 0; i < measuredFt.Length; i++)
            {
                double expected = orderedCoordinatesFt[i + 1] - orderedCoordinatesFt[i];
                if (expected <= 0 || !measuredFt[i].HasValue || double.IsNaN(measuredFt[i].Value) ||
                    double.IsInfinity(measuredFt[i].Value) ||
                    Math.Abs(measuredFt[i].Value - expected) > ToleranceMm / 304.8) return false;
            }
            return true;
        }

        private static double OpeningOffset(Document doc, OpeningInfo row)
        {
            Wall wall = doc.GetElement(new ElementId(row.Data.HostWallId)) as Wall;
            Line axis = (wall?.Location as LocationCurve)?.Curve as Line;
            if (axis == null || !row.Element.IsRectBoundary) return 0;
            XYZ center = (row.Element.BoundaryRect[0] + row.Element.BoundaryRect[1]) * 0.5;
            return (center - axis.GetEndPoint(0)).DotProduct(axis.Direction);
        }

        private static List<FaceReference> Faces(Element element, View view)
        {
            var result = new List<FaceReference>();
            var geometry = element.get_Geometry(new Options { ComputeReferences = true,
                IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine });
            ReadFaces(geometry, Transform.Identity, view, result);
            return result;
        }
        private static void ReadFaces(GeometryElement geometry, Transform transform, View view,
            List<FaceReference> result)
        {
            if (geometry == null) return;
            foreach (GeometryObject item in geometry)
            {
                var instance = item as GeometryInstance;
                if (instance != null)
                {
                    // Parameterless symbol geometry retains real instance-qualified references.
                    ReadFaces(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), view, result);
                    continue;
                }
                var solid = item as Solid;
                if (solid == null || solid.Volume <= 1e-9) continue;
                foreach (Face raw in solid.Faces)
                {
                    var face = raw as PlanarFace;
                    if (face?.Reference == null) continue;
                    XYZ origin = transform.OfPoint(face.Origin) - view.Origin;
                    var points = face.Triangulate().Vertices.Select(v => transform.OfPoint(v) - view.Origin).ToList();
                    if (points.Count == 0) continue;
                    result.Add(new FaceReference { Reference = face.Reference,
                        Normal = transform.OfVector(face.FaceNormal).Normalize(),
                        X = origin.DotProduct(view.RightDirection), Y = origin.DotProduct(view.UpDirection),
                        MinX = points.Min(v => v.DotProduct(view.RightDirection)),
                        MaxX = points.Max(v => v.DotProduct(view.RightDirection)),
                        MinY = points.Min(v => v.DotProduct(view.UpDirection)),
                        MaxY = points.Max(v => v.DotProduct(view.UpDirection)) });
                }
            }
        }
        private static bool IsOwned(Dimension dimension, string foundationId)
        {
            Schema schema = Schema.Lookup(SchemaId);
            if (schema == null) return false;
            Entity entity = dimension.GetEntity(schema);
            return entity.IsValid() && entity.Get<string>(schema.GetField("FoundationUniqueId")) == foundationId;
        }
        private static void Mark(Dimension dimension, string foundationId)
        {
            Schema schema = Schema.Lookup(SchemaId);
            if (schema == null)
            {
                var builder = new SchemaBuilder(SchemaId);
                builder.SetSchemaName("HatcoOpeningDimension");
                builder.SetReadAccessLevel(AccessLevel.Public);
                builder.SetWriteAccessLevel(AccessLevel.Public);
                builder.AddSimpleField("FoundationUniqueId", typeof(string));
                schema = builder.Finish();
            }
            var entity = new Entity(schema);
            entity.Set<string>(schema.GetField("FoundationUniqueId"), foundationId);
            dimension.SetEntity(entity);
        }
    }
}
