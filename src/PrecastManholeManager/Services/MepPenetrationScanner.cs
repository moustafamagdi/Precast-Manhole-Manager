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

            var links = new FilteredElementCollector(_host)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            _log.Info($"Revit link instances found: {links.Count}");

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
            }

            _log.Info($"Total detected penetration intersections: {records.Count}");
            return records;
        }

        private IEnumerable<XYZ> IntersectionsWithWall(Wall wall, Curve curve)
        {
            var seen = new List<XYZ>();
            foreach (Solid solid in GeometryUtil.GetSolids(wall, _log))
            {
                SolidCurveIntersection sci = null;
                try
                {
                    sci = solid.IntersectWithCurve(curve, new SolidCurveIntersectionOptions());
                }
                catch (Exception ex)
                {
                    _log.Error($"Solid/curve intersection failed for wall {wall.Id.IntegerValue}.", ex);
                }

                if (sci == null) continue;

                for (int i = 0; i < sci.SegmentCount; i++)
                {
                    Curve seg = sci.GetCurveSegment(i);
                    XYZ p = seg.Evaluate(0.5, true);
                    if (seen.All(x => x.DistanceTo(p) > UnitUtil.MmToFt(5)))
                    {
                        seen.Add(p);
                        yield return p;
                    }
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

            double invertFt = point.Z - verticalHalfSizeFt;
            double offsetFt = OffsetFromWallStart(wall, point);

            return new PenetrationRecord
            {
                LinkName = link.Name,
                LinkedElementId = element.Id.IntegerValue,
                Category = category,
                FamilyType = GetTypeText(element),
                SystemName = GetParameterText(element, "System Name", "System Type", "System Classification"),
                Size = size,
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

        private static double GetDouble(Element e, BuiltInParameter bip)
        {
            var p = e.get_Parameter(bip);
            return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : 0;
        }

        private static string Fmt(XYZ p) => p == null ? "<null>" : $"({p.X:F6},{p.Y:F6},{p.Z:F6})";
    }
}
