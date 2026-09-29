using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class MepPenetrationScanner
    {
        private readonly Document _host;
        private readonly DiagnosticLogger _log;

        private static readonly BuiltInCategory[] Categories =
        {
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_Conduit
        };

        public MepPenetrationScanner(Document host, DiagnosticLogger log)
        {
            _host = host;
            _log = log;
        }

        public List<PenetrationRecord> Scan(ManholeDetectionResult manhole)
        {
            var records = new List<PenetrationRecord>();
            _log.WriteHeader("MEP LINK SCAN");
            _log.Info("Detection mode: WallCenterPlane (independent of existing wall openings).");

            var links = new FilteredElementCollector(_host)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            _log.Info($"Revit link instances found: {links.Count}");

            XYZ scanMin;
            XYZ scanMax;
            BuildScanBounds(manhole, out scanMin, out scanMax);
            _log.Info($"Local scan envelope Min={Fmt(scanMin)} Max={Fmt(scanMax)}");

            foreach (var link in links)
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null)
                {
                    _log.Warn($"Link {link.Id.IntegerValue} '{link.Name}' skipped: unloaded or unavailable.");
                    continue;
                }

                Transform tr = link.GetTotalTransform();
                _log.Info($"Scanning Link Id={link.Id.IntegerValue}, Name='{link.Name}', Doc='{linkDoc.Title}'");
                _log.Info($"Transform Origin={Fmt(tr.Origin)}, BasisX={Fmt(tr.BasisX)}, BasisY={Fmt(tr.BasisY)}, BasisZ={Fmt(tr.BasisZ)}");

                var filter = new ElementMulticategoryFilter(Categories);
                var elements = new FilteredElementCollector(linkDoc)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .ToElements();

                _log.Info($"MEP curve candidates in link: {elements.Count}");
                int localCandidates = 0;

                foreach (Element e in elements)
                {
                    try
                    {
                        if (!(e.Location is LocationCurve lc) || lc.Curve == null)
                        {
                            _log.Warn($"Linked element {e.Id.IntegerValue} ({e.Category?.Name}) skipped: no LocationCurve.");
                            continue;
                        }

                        Curve hostCurve = lc.Curve.CreateTransformed(tr);
                        if (hostCurve == null) continue;

                        if (!CurveTouchesBox(hostCurve, scanMin, scanMax))
                            continue;

                        localCandidates++;

                        foreach (var wall in manhole.Walls)
                        {
                            var points = IntersectionsWithWall(wall.Wall, hostCurve).ToList();
                            if (points.Count == 0) continue;

                            foreach (XYZ p in points)
                            {
                                var record = BuildRecord(link, e, wall, p, hostCurve, manhole.FoundationTopZ);
                                records.Add(record);

                                _log.Info(
                                    $"PENETRATION Link='{record.LinkName}' Elem={record.LinkedElementId} Cat='{record.Category}' " +
                                    $"Wall=W{record.WallNumber} HostWall={record.HostWallId} Point={Fmt(p)} " +
                                    $"Size='{record.Size}' Invert={record.InvertMm:F1}mm AboveBase={record.InvertAboveBaseMm:F1}mm " +
                                    $"Offset={record.OffsetFromWallStartMm:F1}mm");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"Error scanning linked element {e.Id.IntegerValue} in '{link.Name}'.", ex);
                    }
                }

                _log.Info($"MEP curves inside local manhole scan envelope: {localCandidates}");
            }

            MarkPossibleDuplicates(records);
            _log.Info($"Total detected penetration intersections: {records.Count}");
            return records;
        }

        private void BuildScanBounds(ManholeDetectionResult manhole, out XYZ min, out XYZ max)
        {
            double margin = UnitUtil.MmToFt(1000);
            double minX = manhole.FoundationBox.Min.X;
            double minY = manhole.FoundationBox.Min.Y;
            double minZ = manhole.FoundationBox.Min.Z;
            double maxX = manhole.FoundationBox.Max.X;
            double maxY = manhole.FoundationBox.Max.Y;
            double maxZ = manhole.FoundationBox.Max.Z;

            foreach (var mw in manhole.Walls)
            {
                BoundingBoxXYZ b = mw.Wall.get_BoundingBox(null);
                if (b == null) continue;

                minX = Math.Min(minX, b.Min.X);
                minY = Math.Min(minY, b.Min.Y);
                minZ = Math.Min(minZ, b.Min.Z);
                maxX = Math.Max(maxX, b.Max.X);
                maxY = Math.Max(maxY, b.Max.Y);
                maxZ = Math.Max(maxZ, b.Max.Z);
            }

            min = new XYZ(minX - margin, minY - margin, minZ - margin);
            max = new XYZ(maxX + margin, maxY + margin, maxZ + margin);
        }

        private static bool CurveTouchesBox(Curve curve, XYZ min, XYZ max)
        {
            IList<XYZ> points;
            try
            {
                points = curve.Tessellate();
            }
            catch
            {
                points = new List<XYZ>
                {
                    curve.GetEndPoint(0),
                    curve.GetEndPoint(1)
                };
            }

            if (points == null || points.Count == 0)
                return false;

            double cMinX = points.Min(p => p.X);
            double cMinY = points.Min(p => p.Y);
            double cMinZ = points.Min(p => p.Z);
            double cMaxX = points.Max(p => p.X);
            double cMaxY = points.Max(p => p.Y);
            double cMaxZ = points.Max(p => p.Z);

            return cMinX <= max.X && cMaxX >= min.X &&
                   cMinY <= max.Y && cMaxY >= min.Y &&
                   cMinZ <= max.Z && cMaxZ >= min.Z;
        }

        private void MarkPossibleDuplicates(List<PenetrationRecord> records)
        {
            const double offsetToleranceMm = 75.0;
            const double invertToleranceMm = 75.0;

            for (int i = 0; i < records.Count; i++)
            {
                for (int j = i + 1; j < records.Count; j++)
                {
                    PenetrationRecord a = records[i];
                    PenetrationRecord b = records[j];

                    if (a.WallNumber != b.WallNumber) continue;
                    if (!string.Equals(a.Category, b.Category, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(NormalizeSize(a.Size), NormalizeSize(b.Size), StringComparison.OrdinalIgnoreCase)) continue;
                    if (Math.Abs(a.OffsetFromWallStartMm - b.OffsetFromWallStartMm) > offsetToleranceMm) continue;
                    if (Math.Abs(a.InvertMm - b.InvertMm) > invertToleranceMm) continue;
                    if (string.Equals(a.LinkName, b.LinkName, StringComparison.OrdinalIgnoreCase)) continue;

                    string noteA = $"POSSIBLE_DUPLICATE with {b.LinkName} / Element {b.LinkedElementId}";
                    string noteB = $"POSSIBLE_DUPLICATE with {a.LinkName} / Element {a.LinkedElementId}";

                    a.Notes = AppendNote(a.Notes, noteA);
                    b.Notes = AppendNote(b.Notes, noteB);

                    _log.Warn(
                        $"Possible duplicate penetration across links: W{a.WallNumber}, Size='{a.Size}', " +
                        $"A='{a.LinkName}' Elem={a.LinkedElementId} Invert={a.InvertMm:F1}mm, " +
                        $"B='{b.LinkName}' Elem={b.LinkedElementId} Invert={b.InvertMm:F1}mm");
                }
            }
        }

        private static string NormalizeSize(string size)
        {
            if (string.IsNullOrWhiteSpace(size)) return string.Empty;
            return new string(size.Where(char.IsDigit).ToArray());
        }

        private static string AppendNote(string existing, string addition)
        {
            if (string.IsNullOrWhiteSpace(existing)) return addition;
            return existing + " | " + addition;
        }

        private IEnumerable<XYZ> IntersectionsWithWall(Wall wall, Curve curve)
        {
            // IMPORTANT:
            // Do not intersect against the current wall Solid here.
            // Once Phase 3 creates an opening, the MEP centerline passes through empty space
            // and a solid/curve test returns no hit. Instead, intersect against the wall's
            // original location plane and then validate that the point lies inside the wall extents.
            if (!(wall.Location is LocationCurve wallLocation) || !(wallLocation.Curve is Line wallAxis))
                yield break;

            XYZ a = wallAxis.GetEndPoint(0);
            XYZ b = wallAxis.GetEndPoint(1);

            XYZ tangent = b - a;
            tangent = new XYZ(tangent.X, tangent.Y, 0.0);
            if (tangent.GetLength() < 1e-9)
                yield break;

            tangent = tangent.Normalize();
            XYZ normal = new XYZ(-tangent.Y, tangent.X, 0.0);

            BoundingBoxXYZ wallBox = wall.get_BoundingBox(null);
            if (wallBox == null)
                yield break;

            double xyTolerance = UnitUtil.MmToFt(10.0);
            double zTolerance = UnitUtil.MmToFt(10.0);
            double wallLength = wallAxis.Length;

            IList<XYZ> points;
            try
            {
                points = curve.Tessellate();
            }
            catch
            {
                points = new List<XYZ>
                {
                    curve.GetEndPoint(0),
                    curve.GetEndPoint(1)
                };
            }

            if (points == null || points.Count < 2)
                yield break;

            var seen = new List<XYZ>();

            for (int i = 0; i < points.Count - 1; i++)
            {
                XYZ p0 = points[i];
                XYZ p1 = points[i + 1];

                double d0 = (p0 - a).DotProduct(normal);
                double d1 = (p1 - a).DotProduct(normal);

                // Segment does not cross the wall center plane.
                if ((d0 > xyTolerance && d1 > xyTolerance) ||
                    (d0 < -xyTolerance && d1 < -xyTolerance))
                    continue;

                double denominator = d0 - d1;
                XYZ hit;

                if (Math.Abs(denominator) < 1e-9)
                {
                    // Segment lies effectively on the wall plane. This is not a through-wall penetration.
                    continue;
                }
                else
                {
                    double t = d0 / denominator;
                    if (t < -1e-6 || t > 1.000001)
                        continue;

                    hit = p0 + (p1 - p0) * t;
                }

                double along = (hit - a).DotProduct(tangent);
                if (along < -xyTolerance || along > wallLength + xyTolerance)
                    continue;

                if (hit.Z < wallBox.Min.Z - zTolerance || hit.Z > wallBox.Max.Z + zTolerance)
                    continue;

                if (seen.All(x => x.DistanceTo(hit) > UnitUtil.MmToFt(5.0)))
                {
                    seen.Add(hit);
                    yield return hit;
                }
            }
        }

        private PenetrationRecord BuildRecord(
            RevitLinkInstance link,
            Element element,
            ManholeWall wall,
            XYZ point,
            Curve transformedCurve,
            double baseTopZ)
        {
            string category = element.Category?.Name ?? string.Empty;
            string size;
            double verticalHalfSizeFt;
            GetSizeAndHalfHeight(element, out size, out verticalHalfSizeFt);

            string shape;
            double diameterMm;
            double widthMm;
            double heightMm;
            GetOpeningGeometry(element, out shape, out diameterMm, out widthMm, out heightMm);

            double invertFt = point.Z - verticalHalfSizeFt;
            double offsetFt = OffsetFromWallStart(wall, point);

            return new PenetrationRecord
            {
                LinkName = link.Name,
                LinkInstanceId = link.Id.IntegerValue,
                LinkedElementId = element.Id.IntegerValue,
                LinkedUniqueId = element.UniqueId,
                Category = category,
                FamilyType = GetTypeText(element),
                SystemName = GetParameterText(element, "System Name", "System Type", "System Classification"),
                Size = size,
                Shape = shape,
                DiameterMm = diameterMm,
                WidthMm = widthMm,
                HeightMm = heightMm,
                ClearanceMm = 50.0,
                WallNumber = wall.Number,
                HostWallId = wall.Wall.Id.IntegerValue,
                Xmm = UnitUtil.FtToMm(point.X),
                Ymm = UnitUtil.FtToMm(point.Y),
                Zmm = UnitUtil.FtToMm(point.Z),
                InvertMm = UnitUtil.FtToMm(invertFt),
                InvertAboveBaseMm = UnitUtil.FtToMm(invertFt - baseTopZ),
                OffsetFromWallStartMm = UnitUtil.FtToMm(offsetFt),
                Notes = category.IndexOf("Cable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        category.IndexOf("Conduit", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Invert uses centerline because vertical size could not be resolved."
                    : string.Empty
            };
        }

        private static double OffsetFromWallStart(ManholeWall wall, XYZ p)
        {
            XYZ a = wall.Axis.GetEndPoint(0);
            XYZ b = wall.Axis.GetEndPoint(1);

            // Stable origin: lexicographically smaller XY endpoint.
            XYZ start = (a.X < b.X || (Math.Abs(a.X - b.X) < 1e-9 && a.Y <= b.Y)) ? a : b;
            XYZ dir = (ReferenceEquals(start, a) ? b - a : a - b).Normalize();
            return Math.Abs((p - start).DotProduct(dir));
        }

        private static string GetTypeText(Element e)
        {
            var type = e.Document.GetElement(e.GetTypeId());
            return type?.Name ?? e.Name ?? string.Empty;
        }

        private static string GetParameterText(Element e, params string[] names)
        {
            foreach (string n in names)
            {
                var p = e.LookupParameter(n);
                if (p != null)
                {
                    string s = p.AsString() ?? p.AsValueString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return string.Empty;
        }

        private static void GetSizeAndHalfHeight(Element e, out string size, out double halfHeightFt)
        {
            size = GetParameterText(e, "Size");
            halfHeightFt = 0;

            double diameter = GetDouble(e, BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            if (diameter <= 0)
                diameter = GetDouble(e, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

            if (diameter > 0)
            {
                halfHeightFt = diameter / 2.0;
                if (string.IsNullOrWhiteSpace(size))
                    size = $"Ø{UnitUtil.FtToMm(diameter):0.#} mm";
                return;
            }

            double h = GetDouble(e, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            double w = GetDouble(e, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);

            if (h > 0)
            {
                halfHeightFt = h / 2.0;
                if (string.IsNullOrWhiteSpace(size))
                    size = $"{UnitUtil.FtToMm(w):0.#}x{UnitUtil.FtToMm(h):0.#} mm";
                return;
            }

            // Cable tray / conduit fallback via common parameters.
            var hp = e.LookupParameter("Height");
            if (hp != null && hp.StorageType == StorageType.Double)
                halfHeightFt = hp.AsDouble() / 2.0;

            if (string.IsNullOrWhiteSpace(size))
                size = GetParameterText(e, "Diameter", "Width", "Height");
        }

        private static void GetOpeningGeometry(
            Element e,
            out string shape,
            out double diameterMm,
            out double widthMm,
            out double heightMm)
        {
            shape = "Review";
            diameterMm = 0;
            widthMm = 0;
            heightMm = 0;

            double diameterFt = GetDouble(e, BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            if (diameterFt <= 0)
                diameterFt = GetDouble(e, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

            if (diameterFt <= 0)
                diameterFt = GetNamedDouble(e, "Diameter");

            if (diameterFt > 0)
            {
                shape = "Round";
                diameterMm = UnitUtil.FtToMm(diameterFt);
                return;
            }

            double widthFt = GetDouble(e, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            double heightFt = GetDouble(e, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

            if (widthFt <= 0)
                widthFt = GetNamedDouble(e, "Width");
            if (heightFt <= 0)
                heightFt = GetNamedDouble(e, "Height");

            if (widthFt > 0 && heightFt > 0)
            {
                shape = "Rectangular";
                widthMm = UnitUtil.FtToMm(widthFt);
                heightMm = UnitUtil.FtToMm(heightFt);
            }
        }

        private static double GetNamedDouble(Element e, string parameterName)
        {
            Parameter p = e.LookupParameter(parameterName);
            return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : 0;
        }

        private static double GetDouble(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : 0;
        }

        private static string Fmt(XYZ p) => p == null ? "<null>" : $"({p.X:F6},{p.Y:F6},{p.Z:F6})";
    }
}
