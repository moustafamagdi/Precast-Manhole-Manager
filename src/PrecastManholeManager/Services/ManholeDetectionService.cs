using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class ManholeDetectionResult
    {
        public Element Foundation { get; set; }
        public BoundingBoxXYZ FoundationBox { get; set; }
        public XYZ Center { get; set; }
        public double FoundationTopZ { get; set; }
        public double FoundationThicknessFt { get; set; }
        public double ClearW1W4Ft { get; set; }
        public double ClearW2W3Ft { get; set; }
        public double OuterW1W4Ft { get; set; }
        public double OuterW2W3Ft { get; set; }
        public double WallHeightFt { get; set; }
        public List<ManholeWall> Walls { get; set; } = new List<ManholeWall>();
        public List<int> CandidateWallIds { get; set; } = new List<int>();
        public string Warning { get; set; }
        public bool IsValid => Foundation != null && Walls.Count == 4;
    }

    internal sealed class ManholeDetectionService
    {
        private readonly Document _doc;
        private readonly DiagnosticLogger _log;

        public ManholeDetectionService(Document doc, DiagnosticLogger log)
        {
            _doc = doc;
            _log = log;
        }

        public ManholeDetectionResult Detect(Element foundation)
        {
            var result = new ManholeDetectionResult { Foundation = foundation };

            _log.WriteHeader("MANHOLE DETECTION");
            _log.Info($"Foundation Id={foundation.Id.IntegerValue}, Name={foundation.Name}, Category={foundation.Category?.Name}");

            var box = foundation.get_BoundingBox(null);
            if (box == null)
                throw new InvalidOperationException("Selected foundation has no model bounding box.");

            result.FoundationBox = box;
            result.Center = new XYZ(
                (box.Min.X + box.Max.X) / 2.0,
                (box.Min.Y + box.Max.Y) / 2.0,
                (box.Min.Z + box.Max.Z) / 2.0);
            result.FoundationTopZ = box.Max.Z;
            result.FoundationThicknessFt = box.Max.Z - box.Min.Z;

            _log.Info($"Foundation BBox Min={Fmt(box.Min)} Max={Fmt(box.Max)}");
            _log.Info($"Foundation Center={Fmt(result.Center)} TopZ={result.FoundationTopZ:F6} ft ({UnitUtil.FtToMm(result.FoundationTopZ):F1} mm)");

            double searchMargin = UnitUtil.MmToFt(1200);
            var outline = new Outline(
                new XYZ(box.Min.X - searchMargin, box.Min.Y - searchMargin, box.Min.Z - searchMargin),
                new XYZ(box.Max.X + searchMargin, box.Max.Y + searchMargin, box.Max.Z + UnitUtil.MmToFt(6000)));

            var candidates = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w =>
                {
                    var wb = w.get_BoundingBox(null);
                    if (wb == null) return false;
                    return BoxesOverlap(outline.MinimumPoint, outline.MaximumPoint, wb.Min, wb.Max);
                })
                .Select(w => BuildCandidate(w, result.Center))
                .Where(x => x != null)
                .OrderBy(x => GeometryUtil.DistancePointToUnboundedLine2D(result.Center, x.Axis))
                .ThenBy(x => x.Wall.Id.IntegerValue)
                .ToList();

            result.CandidateWallIds = candidates.Select(c => c.Wall.Id.IntegerValue).ToList();
            _log.Info($"Wall candidates in search envelope: {candidates.Count}");
            foreach (var c in candidates)
            {
                double d = GeometryUtil.DistancePointToUnboundedLine2D(result.Center, c.Axis);
                _log.Info($"Candidate WallId={c.Wall.Id.IntegerValue}, Len={c.LengthFt:F4} ft, Mid={Fmt(c.MidPoint)}, Dir={Fmt(c.Direction)}, CenterDistance={UnitUtil.FtToMm(d):F1} mm");
            }

            // Prefer the closest four wall axes surrounding the base center.
            var picked = candidates.Take(4).ToList();
            if (picked.Count < 4)
            {
                result.Warning = $"Only {picked.Count} wall candidates were found.";
                _log.Warn(result.Warning);
                return result;
            }

            if (!FormsTwoOppositePairs(picked))
            {
                result.Warning = "Closest four walls do not form two near-parallel opposite pairs. Results require review.";
                _log.Warn(result.Warning);
            }

            NumberWalls(picked, result.Center);
            result.Walls = picked.OrderBy(w => w.Number).ToList();
            ComputeDimensions(result);

            string geometryWarning = ValidateDetectedGeometry(result);
            if (!string.IsNullOrWhiteSpace(geometryWarning))
            {
                result.Warning = geometryWarning;
                _log.Warn(result.Warning);
            }

            _log.Info("Assigned wall numbers:");
            foreach (var w in result.Walls)
                _log.Info(w.ToString());

            _log.Info(
                $"Manhole dimensions: Clear W1-W4={UnitUtil.FtToMm(result.ClearW1W4Ft):F1} mm, " +
                $"Clear W2-W3={UnitUtil.FtToMm(result.ClearW2W3Ft):F1} mm, " +
                $"Outer W1-W4={UnitUtil.FtToMm(result.OuterW1W4Ft):F1} mm, " +
                $"Outer W2-W3={UnitUtil.FtToMm(result.OuterW2W3Ft):F1} mm, " +
                $"WallHeight={UnitUtil.FtToMm(result.WallHeightFt):F1} mm, " +
                $"BaseThickness={UnitUtil.FtToMm(result.FoundationThicknessFt):F1} mm");

            return result;
        }

        private static void ComputeDimensions(ManholeDetectionResult result)
        {
            ManholeWall w1 = result.Walls.First(x => x.Number == 1);
            ManholeWall w2 = result.Walls.First(x => x.Number == 2);
            ManholeWall w3 = result.Walls.First(x => x.Number == 3);
            ManholeWall w4 = result.Walls.First(x => x.Number == 4);

            result.ClearW1W4Ft = ClearDistanceBetweenParallelWalls(w1.Wall, w4.Wall);
            result.ClearW2W3Ft = ClearDistanceBetweenParallelWalls(w2.Wall, w3.Wall);

            result.OuterW1W4Ft = OuterDistanceBetweenParallelWalls(w1.Wall, w4.Wall);
            result.OuterW2W3Ft = OuterDistanceBetweenParallelWalls(w2.Wall, w3.Wall);

            double minZ = double.MaxValue;
            double maxZ = double.MinValue;

            foreach (ManholeWall mw in result.Walls)
            {
                BoundingBoxXYZ b = mw.Wall.get_BoundingBox(null);
                if (b == null) continue;
                minZ = Math.Min(minZ, b.Min.Z);
                maxZ = Math.Max(maxZ, b.Max.Z);
            }

            result.WallHeightFt =
                minZ < double.MaxValue && maxZ > double.MinValue
                    ? maxZ - minZ
                    : 0;
        }

        private static string ValidateDetectedGeometry(ManholeDetectionResult result)
        {
            ManholeWall w1 = result.Walls.First(x => x.Number == 1);
            ManholeWall w2 = result.Walls.First(x => x.Number == 2);
            ManholeWall w3 = result.Walls.First(x => x.Number == 3);
            ManholeWall w4 = result.Walls.First(x => x.Number == 4);

            const double parallelDotMin = 0.95;
            const double perpendicularDotMax = 0.20;
            const double oppositeVectorDotMax = -0.80;
            double symmetryToleranceFt = UnitUtil.MmToFt(150);

            if (Math.Abs(w1.Direction.DotProduct(w4.Direction)) < parallelDotMin ||
                Math.Abs(w2.Direction.DotProduct(w3.Direction)) < parallelDotMin)
            {
                return "Detected walls do not form the required opposite parallel pairs W1-W4 and W2-W3.";
            }

            if (Math.Abs(w1.Direction.DotProduct(w2.Direction)) > perpendicularDotMax)
            {
                return "Detected wall pairs are not sufficiently perpendicular.";
            }

            XYZ v1 = Flatten(w1.MidPoint - result.Center);
            XYZ v4 = Flatten(w4.MidPoint - result.Center);
            XYZ v2 = Flatten(w2.MidPoint - result.Center);
            XYZ v3 = Flatten(w3.MidPoint - result.Center);

            if (v1.GetLength() < 1e-9 || v2.GetLength() < 1e-9 ||
                v3.GetLength() < 1e-9 || v4.GetLength() < 1e-9)
            {
                return "One or more detected wall midpoints are too close to the foundation center.";
            }

            if (v1.Normalize().DotProduct(v4.Normalize()) > oppositeVectorDotMax ||
                v2.Normalize().DotProduct(v3.Normalize()) > oppositeVectorDotMax)
            {
                return "Detected opposite walls do not lie on opposite sides of the foundation center.";
            }

            double d1 = GeometryUtil.DistancePointToUnboundedLine2D(result.Center, w1.Axis);
            double d4 = GeometryUtil.DistancePointToUnboundedLine2D(result.Center, w4.Axis);
            double d2 = GeometryUtil.DistancePointToUnboundedLine2D(result.Center, w2.Axis);
            double d3 = GeometryUtil.DistancePointToUnboundedLine2D(result.Center, w3.Axis);

            if (Math.Abs(d1 - d4) > symmetryToleranceFt ||
                Math.Abs(d2 - d3) > symmetryToleranceFt)
            {
                return "Detected wall pairs are not symmetric around the selected foundation center.";
            }

            if (result.ClearW1W4Ft <= UnitUtil.MmToFt(300) ||
                result.ClearW2W3Ft <= UnitUtil.MmToFt(300))
            {
                return "Detected manhole clear dimension is implausibly small.";
            }

            double ratio = result.ClearW1W4Ft > result.ClearW2W3Ft
                ? result.ClearW1W4Ft / result.ClearW2W3Ft
                : result.ClearW2W3Ft / result.ClearW1W4Ft;

            if (ratio > 2.0)
            {
                return "Detected manhole clear dimensions are strongly disproportionate and require review.";
            }

            return null;
        }

        private static XYZ Flatten(XYZ p)
        {
            return new XYZ(p.X, p.Y, 0);
        }

        private static double ClearDistanceBetweenParallelWalls(Wall a, Wall b)
        {
            double centerDistance = WallAxisDistance(a, b);
            return Math.Max(0, centerDistance - (a.Width / 2.0) - (b.Width / 2.0));
        }

        private static double OuterDistanceBetweenParallelWalls(Wall a, Wall b)
        {
            double centerDistance = WallAxisDistance(a, b);
            return centerDistance + (a.Width / 2.0) + (b.Width / 2.0);
        }

        private static double WallAxisDistance(Wall a, Wall b)
        {
            Line lineA = ((LocationCurve)a.Location).Curve as Line;
            Line lineB = ((LocationCurve)b.Location).Curve as Line;

            XYZ midA = lineA.Evaluate(0.5, true);
            XYZ midB = lineB.Evaluate(0.5, true);

            XYZ tangent = lineA.Direction;
            tangent = new XYZ(tangent.X, tangent.Y, 0).Normalize();
            XYZ normal = new XYZ(-tangent.Y, tangent.X, 0);

            return Math.Abs((midB - midA).DotProduct(normal));
        }

        private ManholeWall BuildCandidate(Wall wall, XYZ center)
        {
            if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line line))
            {
                _log.Warn($"Wall {wall.Id.IntegerValue} skipped: non-linear location curve.");
                return null;
            }

            XYZ d = line.Direction;
            d = new XYZ(d.X, d.Y, 0);
            if (d.GetLength() < 1e-9) return null;
            d = d.Normalize();

            return new ManholeWall
            {
                Wall = wall,
                Axis = line,
                MidPoint = GeometryUtil.Midpoint(line),
                Direction = d,
                LengthFt = line.Length
            };
        }

        private static bool FormsTwoOppositePairs(IList<ManholeWall> walls)
        {
            // For a rectangular manhole, among six direction-pair dot products,
            // two should be close to 1 (parallel/opposite axis direction).
            var scores = new List<double>();
            for (int i = 0; i < walls.Count; i++)
                for (int j = i + 1; j < walls.Count; j++)
                    scores.Add(Math.Abs(walls[i].Direction.DotProduct(walls[j].Direction)));

            return scores.Count(s => s > 0.95) >= 2;
        }

        private static void NumberWalls(IList<ManholeWall> walls, XYZ center)
        {
            // Stable first-pass convention based on wall midpoint azimuth around the manhole center:
            // most North = W1; then clockwise W2, W4 opposite W1, W3 opposite W2.
            // We sort clockwise from north using atan2(dx, dy).
            var ordered = walls
                .OrderBy(w =>
                {
                    XYZ v = w.MidPoint - center;
                    double a = Math.Atan2(v.X, v.Y);
                    if (a < 0) a += 2 * Math.PI;
                    return a;
                })
                .ToList();

            // Clockwise order: W1, W2, W4, W3 gives the requested opposite pairs 1-4 and 2-3.
            int[] nums = { 1, 2, 4, 3 };
            for (int i = 0; i < ordered.Count && i < nums.Length; i++)
                ordered[i].Number = nums[i];
        }

        private static bool BoxesOverlap(XYZ minA, XYZ maxA, XYZ minB, XYZ maxB)
        {
            return minA.X <= maxB.X && maxA.X >= minB.X &&
                   minA.Y <= maxB.Y && maxA.Y >= minB.Y &&
                   minA.Z <= maxB.Z && maxA.Z >= minB.Z;
        }

        private static string Fmt(XYZ p) => p == null ? "<null>" : $"({p.X:F6},{p.Y:F6},{p.Z:F6})";
    }
}
