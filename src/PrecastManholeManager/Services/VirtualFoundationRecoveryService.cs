using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    // Read-only experimental analysis. Never changes the selected foundation or any joins/cuts.
    internal sealed class VirtualFoundationResult
    {
        public bool Accepted { get; set; }
        public string Reason { get; set; }
        public XYZ ActualBoxCenter { get; set; }
        public XYZ VirtualCenter { get; set; }
        public double CenterShiftMm { get; set; }
        public double ClearAlongAFt { get; set; }
        public double ClearAlongBFt { get; set; }
        public double OuterAlongAFt { get; set; }
        public double OuterAlongBFt { get; set; }
        public List<Wall> Walls { get; set; } = new List<Wall>();
        public int TestedCombinations { get; set; }
        public int ValidCombinations { get; set; }
        public string Diagnostic { get; set; }
    }

    internal sealed class VirtualFoundationRecoveryService
    {
        private const double ParallelMin = 0.985;
        private const double PerpendicularMax = 0.12;

        private readonly Document _doc;
        private readonly DiagnosticLogger _log;

        public VirtualFoundationRecoveryService(Document doc, DiagnosticLogger log)
        {
            _doc = doc;
            _log = log;
        }

        public VirtualFoundationResult Analyze(Element foundation)
        {
            if (foundation == null) throw new ArgumentNullException(nameof(foundation));
            BoundingBoxXYZ box = foundation.get_BoundingBox(null);
            if (box == null)
                throw new InvalidOperationException("Foundation has no model bounding box.");

            XYZ baseCenter = new XYZ(
                (box.Min.X + box.Max.X) * 0.5,
                (box.Min.Y + box.Max.Y) * 0.5,
                (box.Min.Z + box.Max.Z) * 0.5);

            var result = new VirtualFoundationResult
            {
                ActualBoxCenter = baseCenter,
                Reason = "No validated four-wall footprint was found."
            };

            _log.WriteHeader("EXPERIMENT: VIRTUAL FOUNDATION - READ ONLY");
            _log.Info("Foundation=" + foundation.Id.IntegerValue +
                      " ActualBBoxCenterFt=" + Fmt(baseCenter));

            // Cut foundations can have a shifted bounding-box center; use a generous
            // 2.5 m search, but require every eventual wall to actually bound one rectangle.
            double margin = UnitUtil.MmToFt(2500);
            var candidates = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Select(w => TryCandidate(w, box, baseCenter, margin))
                .Where(c => c != null)
                .OrderBy(c => c.DistanceFt)
                .ThenBy(c => c.Wall.Id.IntegerValue)
                .Take(24)
                .ToList();

            _log.Info("Nearby straight wall candidates: " + candidates.Count);
            foreach (Candidate c in candidates)
                _log.Info("CANDIDATE Wall=" + c.Wall.Id.IntegerValue +
                          " MidFt=" + Fmt(c.Mid) +
                          " LengthMm=" + Mm(c.Length).ToString("0.#") +
                          " HeightMm=" + Mm(c.TopZ - c.BottomZ).ToString("0.#"));

            // Enumerate pairs of parallel walls, then find a second perpendicular pair.
            // Deduplicate the same four-wall set reached by alternative enumeration.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var valid = new List<Solution>();
            for (int i = 0; i < candidates.Count; i++)
            for (int j = i + 1; j < candidates.Count; j++)
            {
                Candidate a = candidates[i], b = candidates[j];
                if (!Parallel(a, b)) continue;
                for (int k = 0; k < candidates.Count; k++)
                {
                    if (k == i || k == j) continue;
                    Candidate c = candidates[k];
                    if (Math.Abs(a.Direction.DotProduct(c.Direction)) > PerpendicularMax) continue;
                    for (int m = k + 1; m < candidates.Count; m++)
                    {
                        if (m == i || m == j) continue;
                        Candidate d = candidates[m];
                        if (!Parallel(c, d)) continue;

                        var ids = new[] { a.Wall.Id.IntegerValue, b.Wall.Id.IntegerValue,
                            c.Wall.Id.IntegerValue, d.Wall.Id.IntegerValue };
                        Array.Sort(ids);
                        string key = string.Join("|", ids);
                        if (!seen.Add(key)) continue;
                        result.TestedCombinations++;

                        Solution solution = Evaluate(a, b, c, d, box, baseCenter);
                        if (solution != null)
                            valid.Add(solution);
                    }
                }
            }

            result.ValidCombinations = valid.Count;
            Solution best = valid.OrderBy(s => s.Score).FirstOrDefault();
            if (best == null)
            {
                _log.Warn(result.Reason + " Combinations=" + result.TestedCombinations);
                return result;
            }

            // Multiple near-identical candidates can mean two neighboring or stacked
            // manholes. Do not silently choose when the footprint is ambiguous.
            Solution runnerUp = valid
                .Where(s => s.Center.DistanceTo(best.Center) > UnitUtil.MmToFt(75) ||
                            Math.Abs(s.ClearA - best.ClearA) > UnitUtil.MmToFt(75) ||
                            Math.Abs(s.ClearB - best.ClearB) > UnitUtil.MmToFt(75))
                .OrderBy(s => s.Score)
                .FirstOrDefault();

            if (runnerUp != null && runnerUp.Score - best.Score < UnitUtil.MmToFt(125))
            {
                result.Reason = "Ambiguous neighboring four-wall footprints; review the wall IDs.";
                _log.Warn(result.Reason +
                          " Best=" + string.Join(",", best.Walls.Select(w => w.Wall.Id.IntegerValue)) +
                          " Next=" + string.Join(",", runnerUp.Walls.Select(w => w.Wall.Id.IntegerValue)));
                return result;
            }

            result.Accepted = true;
            result.Reason = "Four-wall virtual footprint validated (read-only).";
            result.VirtualCenter = best.Center;
            result.CenterShiftMm = Mm((new XYZ(best.Center.X - baseCenter.X,
                best.Center.Y - baseCenter.Y, 0)).GetLength());
            result.ClearAlongAFt = best.ClearA;
            result.ClearAlongBFt = best.ClearB;
            result.OuterAlongAFt = best.OuterA;
            result.OuterAlongBFt = best.OuterB;
            result.Walls = best.Walls.Select(x => x.Wall).ToList();

            _log.Info("ACCEPTED WALLS=" +
                      string.Join(",", result.Walls.Select(w => w.Id.IntegerValue)));
            _log.Info("VIRTUAL_CENTER_FT=" + Fmt(best.Center) +
                      " SHIFT_MM=" + result.CenterShiftMm.ToString("0.#"));
            _log.Info("CLEAR_MM=" + Mm(best.ClearA).ToString("0.#") +
                      " x " + Mm(best.ClearB).ToString("0.#") +
                      " OUTER_MM=" + Mm(best.OuterA).ToString("0.#") +
                      " x " + Mm(best.OuterB).ToString("0.#") +
                      " VALID_COMBINATIONS=" + valid.Count);
            _log.Info("No Revit elements, void cuts or joins were changed.");
            return result;
        }

        private static Candidate TryCandidate(Wall wall, BoundingBoxXYZ baseBox,
            XYZ baseCenter, double margin)
        {
            var lc = wall.Location as LocationCurve;
            var line = lc?.Curve as Line;
            if (line == null) return null;
            BoundingBoxXYZ b = wall.get_BoundingBox(null);
            if (b == null) return null;
            if (b.Max.X < baseBox.Min.X - margin || b.Min.X > baseBox.Max.X + margin ||
                b.Max.Y < baseBox.Min.Y - margin || b.Min.Y > baseBox.Max.Y + margin)
                return null;
            // Reject walls in a different vertical tier from the base.
            if (b.Max.Z < baseBox.Max.Z - UnitUtil.MmToFt(300) ||
                b.Min.Z > baseBox.Max.Z + UnitUtil.MmToFt(700))
                return null;

            XYZ dir = Flat(line.Direction);
            if (dir.GetLength() < 1e-8) return null;
            dir = dir.Normalize();
            XYZ mid = (line.GetEndPoint(0) + line.GetEndPoint(1)) * 0.5;
            return new Candidate
            {
                Wall = wall, Line = line, Direction = dir, Mid = mid,
                Length = line.Length, BottomZ = b.Min.Z, TopZ = b.Max.Z,
                DistanceFt = Flat(mid - baseCenter).GetLength()
            };
        }

        private static Solution Evaluate(Candidate a, Candidate b,
            Candidate c, Candidate d, BoundingBoxXYZ box, XYZ baseCenter)
        {
            XYZ nA = new XYZ(-a.Direction.Y, a.Direction.X, 0);
            XYZ nB = new XYZ(-c.Direction.Y, c.Direction.X, 0);
            double signedA = Flat(b.Mid - a.Mid).DotProduct(nA);
            double signedB = Flat(d.Mid - c.Mid).DotProduct(nB);
            double spanA = Math.Abs(signedA);
            double spanB = Math.Abs(signedB);
            if (spanA < UnitUtil.MmToFt(450) || spanB < UnitUtil.MmToFt(450) ||
                spanA > UnitUtil.MmToFt(6000) || spanB > UnitUtil.MmToFt(6000))
                return null;

            // Mid-planes of opposite wall axes intersect at the virtual center.
            XYZ middleA = a.Mid + nA * (signedA * 0.5);
            XYZ middleB = c.Mid + nB * (signedB * 0.5);
            XYZ center;
            if (!IntersectLines(middleA, a.Direction, middleB, c.Direction, out center))
                return null;
            center = new XYZ(center.X, center.Y, baseCenter.Z);

            // The selected cropped base must overlap the reconstructed footprint.
            if (center.X < box.Min.X - UnitUtil.MmToFt(1800) ||
                center.X > box.Max.X + UnitUtil.MmToFt(1800) ||
                center.Y < box.Min.Y - UnitUtil.MmToFt(1800) ||
                center.Y > box.Max.Y + UnitUtil.MmToFt(1800))
                return null;

            double faceA = spanA - (a.Wall.Width + b.Wall.Width) * 0.5;
            double faceB = spanB - (c.Wall.Width + d.Wall.Width) * 0.5;
            if (faceA < UnitUtil.MmToFt(300) || faceB < UnitUtil.MmToFt(300))
                return null;

            // Verify the four corners actually land on all four *segments*.
            // Infinite wall axes alone previously picked walls from nearby manholes.
            double cornerTol = UnitUtil.MmToFt(120);
            foreach (Candidate first in new[] { a, b })
            foreach (Candidate second in new[] { c, d })
            {
                XYZ corner;
                if (!IntersectLines(first.Mid, first.Direction, second.Mid,
                        second.Direction, out corner))
                    return null;
                if (!WithinWallSegment(first, corner, cornerTol) ||
                    !WithinWallSegment(second, corner, cornerTol))
                    return null;
            }

            // Bottom/top mismatches indicate walls in different manholes or tiers.
            Candidate[] walls = { a, b, c, d };
            if (walls.Max(w => w.BottomZ) - walls.Min(w => w.BottomZ) >
                    UnitUtil.MmToFt(250) ||
                walls.Max(w => w.TopZ) - walls.Min(w => w.TopZ) >
                    UnitUtil.MmToFt(350))
                return null;

            // Require cropped foundation bounding region to overlap the inferred
            // full rectangle on both local axes, without assuming its bbox is intact.
            XYZ delta = Flat(baseCenter - center);
            double offsetA = Math.Abs(delta.DotProduct(nA));
            double offsetB = Math.Abs(delta.DotProduct(nB));
            if (offsetA > spanA * 0.5 + UnitUtil.MmToFt(250) ||
                offsetB > spanB * 0.5 + UnitUtil.MmToFt(250))
                return null;

            // Shorter, more symmetric local footprints are preferred; never use
            // foundation bbox center as a hard geometry constraint.
            double score = Flat(center - baseCenter).GetLength() +
                Math.Abs(a.Length - b.Length) +
                Math.Abs(c.Length - d.Length) +
                0.05 * (spanA + spanB);

            return new Solution
            {
                Center = center, Score = score, ClearA = faceA, ClearB = faceB,
                OuterA = spanA + (a.Wall.Width + b.Wall.Width) * 0.5,
                OuterB = spanB + (c.Wall.Width + d.Wall.Width) * 0.5,
                Walls = walls.ToList()
            };
        }

        private static bool WithinWallSegment(Candidate wall, XYZ point,
            double tolerance)
        {
            XYZ start = wall.Line.GetEndPoint(0);
            double t = Flat(point - start).DotProduct(wall.Direction);
            return t >= -tolerance && t <= wall.Length + tolerance;
        }

        private static bool IntersectLines(XYZ p, XYZ d, XYZ q, XYZ e,
            out XYZ intersection)
        {
            intersection = null;
            double determinant = d.X * e.Y - d.Y * e.X;
            if (Math.Abs(determinant) < 0.10) return false;
            XYZ delta = q - p;
            double t = (delta.X * e.Y - delta.Y * e.X) / determinant;
            intersection = p + d * t;
            return true;
        }

        private static bool Parallel(Candidate a, Candidate b)
        {
            return Math.Abs(a.Direction.DotProduct(b.Direction)) >= ParallelMin;
        }

        private static XYZ Flat(XYZ p) { return new XYZ(p.X, p.Y, 0); }
        private static double Mm(double ft) { return UnitUtil.FtToMm(ft); }
        private static string Fmt(XYZ v)
        {
            return "(" + v.X.ToString("0.###") + "," +
                   v.Y.ToString("0.###") + "," + v.Z.ToString("0.###") + ")";
        }

        private sealed class Candidate
        {
            public Wall Wall;
            public Line Line;
            public XYZ Direction;
            public XYZ Mid;
            public double Length;
            public double BottomZ;
            public double TopZ;
            public double DistanceFt;
        }

        private sealed class Solution
        {
            public XYZ Center;
            public double Score;
            public double ClearA;
            public double ClearB;
            public double OuterA;
            public double OuterB;
            public List<Candidate> Walls;
        }
    }
}
