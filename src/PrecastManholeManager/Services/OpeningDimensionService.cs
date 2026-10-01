using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    // Model-face references only: no detail-line proxies or overridden dimension values.
    internal static partial class OpeningDimensionService
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
            public List<FaceReference> Faces;
        }

        // Caller owns the transaction, so a failed production update restores these annotations.
        public static void RemoveOwned(Document doc, Element foundation)
        {
            string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D_OUT_W";
            var views = new HashSet<int>(new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>().Where(v => Enumerable.Range(1, 4).Any(n => v.Name == prefix + n) ||
                    v.Name == "MH_" + foundation.Id.IntegerValue + "_PROD_2D_PLAN")
                .Select(v => v.Id.IntegerValue));
            var ids = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>()
                .Where(d => views.Contains(d.OwnerViewId.IntegerValue) &&
                    (IsOwned(d, foundation.UniqueId) || IsOwned(d, foundation.UniqueId, true)))
                .Select(d => d.Id).ToList();
            if (ids.Count > 0) doc.Delete(ids);
        }

        public static string Generate(Document doc, Element foundation, DiagnosticLogger log, Action<bool> completed = null)
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
            if (sections.Count != 4)
                return "Dimensions: generate the manhole's production views first.";
            var type = new FilteredElementCollector(doc).OfClass(typeof(DimensionType))
                .Cast<DimensionType>().FirstOrDefault(x => x.StyleType == DimensionStyleType.Linear &&
                    x.Name.Equals("HTC_DIM_1.8mm", StringComparison.OrdinalIgnoreCase));
            if (type == null) return "Dimensions: load the required linear dimension type 'HTC_DIM_1.8mm' first.";
            ViewFamilyType sectionType = ManholeViewTitleService.RequiredSectionType(doc);
            int created = 0, failed = 0;
            var diagnostics = new List<string>();
            log.WriteHeader("ASSOCIATIVE OPENING DIMENSIONS");
            log.Info("DIMENSION TYPE: " + type.Name);
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
                        if (view.GetTypeId() != sectionType.Id)
                            view.ChangeTypeId(sectionType.Id);
                        ManholeViewTitleService.UpdateTitle(view,
                            ManholeViewTitleService.Name(doc, foundation, log), number,
                            foundation.Id.IntegerValue, log);
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
            bool bodyComplete;
            string bodyStatus = GenerateBody(doc, foundation, footprint, sections, type, log, out bodyComplete);
            completed?.Invoke(failed == 0 && bodyComplete);
            return "Dimensions: " + created + " strings created; " + failed + " wall(s) need review." +
                (diagnostics.Count == 0 ? "" : "\n" + string.Join("\n", diagnostics)) +
                "\n" + bodyStatus;
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
                // Native opening void surfaces expose references that host-wall reveal faces may omit.
                row.Faces = Faces(row.Element, view);
                log.Info("OPENING DIMENSION REFERENCES Opening=" + row.Element.Id.IntegerValue +
                    " Wall=" + wall.Id.IntegerValue + " PlanarReferences=" + row.Faces.Count);
                double midY = (row.Bottom + row.Top) / 2;
                chain.Add(Find(row.Faces, view.RightDirection, true, row.Left, midY));
                chain.Add(Find(row.Faces, view.RightDirection, true, row.Right, midY));
            }
            // A single horizontal chain avoids stacking overlapping wall-origin dimensions.
            double wallBottom = faces.Min(f => f.MinY);
            double gap = UnitUtil.MmToFt(3 * view.Scale);
            CreateString(doc, foundation, view, type, chain, true, wallBottom - gap, log);
            int count = 1;
            foreach (var row in rows.OrderBy(r => r.Left))
            {
                double centerX = (row.Left + row.Right) / 2;
                double baseY = (new XYZ(0, 0, foundation.get_BoundingBox(null).Max.Z) - view.Origin)
                    .DotProduct(view.UpDirection);
                var vertical = new List<Boundary> {
                    BaseReference(faces, foundationFaces, view.UpDirection, baseY, centerX, log),
                    Find(row.Faces, view.UpDirection, false, row.Bottom, centerX),
                    Find(row.Faces, view.UpDirection, false, row.Top, centerX)
                };
                // The reference line is beside each opening, inside the existing crop padding.
                CreateString(doc, foundation, view, type, vertical, false,
                    row.Right + gap, log);
                count++;
            }
            return count;
        }

        private static Boundary BaseReference(List<FaceReference> walls, List<FaceReference> foundation,
            XYZ up, double baseY, double centerX, DiagnosticLogger log)
        {
            // A coincident wall-bottom face is directly referenceable in the exterior section.
            // Only use it when its actual elevation matches the foundation top (0.5 mm).
            try
            {
                var reference = Find(walls, up, false, baseY, centerX);
                log.Info("VERTICAL DATUM: wall face coincident with foundation top.");
                return reference;
            }
            catch (InvalidOperationException)
            {
                log.Info("VERTICAL DATUM: foundation face; wall bottom is not coincident.");
                return Find(foundation, up, false, baseY, centerX);
            }
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

        private static void CreateString(Document doc, Element foundation, View view,
            DimensionType type, List<Boundary> boundaries, bool horizontal, double offset,
            DiagnosticLogger log, bool body = false, XYZ right = null, XYZ up = null)
        {
            right = right ?? view.RightDirection;
            up = up ?? view.UpDirection;
            var sorted = boundaries.OrderBy(b => b.Position).ToList();
            var distinct = new List<Boundary>();
            foreach (var b in sorted)
                if (distinct.Count == 0 || b.Position - distinct.Last().Position > UnitUtil.MmToFt(ToleranceMm))
                    distinct.Add(b);
            if (distinct.Count < 2) throw new InvalidOperationException("Insufficient distinct dimension references.");
            XYZ axis = horizontal ? right : up;
            XYZ cross = horizontal ? up : right;
            XYZ p = view.Origin + axis * distinct.First().Position + cross * offset;
            XYZ q = view.Origin + axis * distinct.Last().Position + cross * offset;
            var references = new ReferenceArray();
            foreach (var b in distinct) references.Append(b.Reference);
            Dimension dimension = doc.Create.NewDimension(view, Line.CreateBound(p, q), references, type);
            if (dimension == null) throw new InvalidOperationException("Revit did not create the dimension.");
            doc.Regenerate();
            if (!dimension.AreReferencesAvailable)
                log.Info("DIMENSION VIEW REFERENCES DEFERRED: verifying geometry and measured values for closed view " + view.Id.IntegerValue);
            foreach (Boundary boundary in distinct)
                if (doc.GetElement(boundary.Reference.ElementId)?.GetGeometryObjectFromReference(boundary.Reference) == null)
                    throw new InvalidOperationException("Dimension reference no longer resolves to model geometry.");
            var measured = dimension.NumberOfSegments == 0 ? new List<double?> { dimension.Value } :
                dimension.Segments.Cast<DimensionSegment>().Select(segment => segment.Value).ToList();
            if (!SegmentsMatch(distinct.Select(b => b.Position).ToArray(), measured.ToArray()))
                throw new InvalidOperationException("Dimension values do not match the measured model faces.");
            doc.Regenerate();
            BoundingBoxXYZ box = dimension.get_BoundingBox(view);
            if (box == null) throw new InvalidOperationException("Dimension has no visible bounds in this view.");
            // Model crop does not bound annotations. Only an enabled annotation crop
            // can clip dimension graphics; its offsets are paper/view units.
            if (view.CropBoxActive && view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.AsInteger() == 1)
            {
                BoundingBoxXYZ crop = view.CropBox;
                Transform toCrop = crop.Transform.Inverse;
                using (var manager = view.GetCropRegionShapeManager())
                {
                    foreach (double x in new[] { box.Min.X, box.Max.X })
                    foreach (double y in new[] { box.Min.Y, box.Max.Y })
                    foreach (double z in new[] { box.Min.Z, box.Max.Z })
                    {
                        XYZ local = toCrop.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z)));
                        if (!WithinAnnotationAxis(local.X, crop.Min.X, crop.Max.X,
                                manager.LeftAnnotationCropOffset, manager.RightAnnotationCropOffset, view.Scale) ||
                            !WithinAnnotationAxis(local.Y, crop.Min.Y, crop.Max.Y,
                                manager.BottomAnnotationCropOffset, manager.TopAnnotationCropOffset, view.Scale))
                            throw new InvalidOperationException("Dimension extends outside the annotation crop. " +
                                "Expand the annotation crop and retry Dimensions - Selected.");
                    }
                }
            }
            Mark(dimension, foundation.UniqueId, body);
            log.Info("ASSOCIATIVE DIMENSION Id=" + dimension.Id.IntegerValue + " View=" + view.Id.IntegerValue +
                " Axis=" + (horizontal ? "H" : "V") + " ValuesMm=" +
                string.Join(",", measured.Select(v => UnitUtil.FtToMm(v.Value).ToString("0.#"))));
        }

        internal static bool WithinAnnotationAxis(double coordinate, double min, double max,
            double lowerPaperOffset, double upperPaperOffset, int scale)
        {
            return scale > 0 && lowerPaperOffset >= 0 && upperPaperOffset >= 0 &&
                coordinate >= min - lowerPaperOffset * scale - 1e-9 &&
                coordinate <= max + upperPaperOffset * scale + 1e-9;
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

        private static List<FaceReference> Faces(Element element, View view, XYZ right = null, XYZ up = null)
        {
            var result = new List<FaceReference>();
            var geometry = element.get_Geometry(new Options { ComputeReferences = true,
                IncludeNonVisibleObjects = element is Opening, DetailLevel = ViewDetailLevel.Fine });
            ReadFaces(geometry, Transform.Identity, view, result, right ?? view.RightDirection, up ?? view.UpDirection);
            return result;
        }
        private static void ReadFaces(GeometryElement geometry, Transform transform, View view,
            List<FaceReference> result, XYZ right, XYZ up)
        {
            if (geometry == null) return;
            foreach (GeometryObject item in geometry)
            {
                var instance = item as GeometryInstance;
                if (instance != null)
                {
                    // Parameterless symbol geometry retains real instance-qualified references.
                    ReadFaces(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), view, result, right, up);
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
                        X = origin.DotProduct(right), Y = origin.DotProduct(up),
                        MinX = points.Min(v => v.DotProduct(right)),
                        MaxX = points.Max(v => v.DotProduct(right)),
                        MinY = points.Min(v => v.DotProduct(up)),
                        MaxY = points.Max(v => v.DotProduct(up)) });
                }
            }
        }
        private static bool IsOwned(Dimension dimension, string foundationId, bool body = false)
        {
            Schema schema = Schema.Lookup(body ? BodySchemaId : SchemaId);
            if (schema == null) return false;
            Entity entity = dimension.GetEntity(schema);
            return entity.IsValid() && entity.Get<string>(schema.GetField("FoundationUniqueId")) == foundationId;
        }
        private static void Mark(Dimension dimension, string foundationId, bool body = false)
        {
            Schema schema = Schema.Lookup(body ? BodySchemaId : SchemaId);
            if (schema == null)
            {
                var builder = new SchemaBuilder(body ? BodySchemaId : SchemaId);
                builder.SetSchemaName(body ? "HatcoManholeBodyDimension" : "HatcoOpeningDimension");
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
