using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class VirtualMepCandidate
    {
        public int LinkInstanceId { get; set; }
        public string LinkName { get; set; }
        public int SourceElementId { get; set; }
        public string SourceUniqueId { get; set; }
        public string Category { get; set; }
        public string SourceSize { get; set; }
        public string SystemName { get; set; }
        public double SlopePercent { get; set; }
        public int WallId { get; set; }
        public int WallNumber { get; set; }
        public int Endpoint { get; set; }
        public XYZ ProjectedHit { get; set; }
        public double GapToFaceMm { get; set; }
        public double DepthInsideWallMm { get; set; }
        public double ReachToAxisMm { get; set; }
        public double DeviationDeg { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
    }

    internal sealed class VirtualMepScanResult
    {
        public List<VirtualMepCandidate> Candidates { get; } =
            new List<VirtualMepCandidate>();
        public int LoadedLinks { get; set; }
        public int UnavailableLinks { get; set; }
        public int NearbyMep { get; set; }
        public int DiagnosedNonCandidates { get; set; }
        public string CsvPath { get; set; }
        public string DiagnosticCsvPath { get; set; }
    }

    // Experimental diagnostic only. It extends LINE endpoints mathematically;
    // no edits are made to linked MEP elements, host walls or openings.
    internal sealed class VirtualMepExtensionScanner
    {
        private readonly Document _doc;
        private readonly DiagnosticLogger _log;

        public VirtualMepExtensionScanner(Document doc, DiagnosticLogger log)
        {
            _doc = doc;
            _log = log;
        }

        public VirtualMepScanResult Scan(VirtualFoundationResult footprint,
            double maxGapMm, double maxDeviationDeg)
        {
            var result = new VirtualMepScanResult();
            _log.WriteHeader("EXPERIMENT: VIRTUAL MEP EXTENSION - READ ONLY");
            _log.Info("MaxGapFromWallFaceMm=" + maxGapMm +
                      " MaxDeviationDeg=" + maxDeviationDeg);

            if (footprint == null || !footprint.Accepted)
                throw new InvalidOperationException(
                    "Run and validate Virtual Foundation before scanning virtual MEP.");

            List<Wall> walls = footprint.Walls;
            XYZ center = footprint.VirtualCenter;

            // Same stable azimuth convention as the existing W1..W4 scanner.
            var ordered = walls.OrderBy(w =>
            {
                Line line = ((LocationCurve)w.Location).Curve as Line;
                XYZ mid = (line.GetEndPoint(0) + line.GetEndPoint(1)) * 0.5;
                double angle = Math.Atan2(mid.X - center.X, mid.Y - center.Y);
                return angle < 0 ? angle + 2 * Math.PI : angle;
            }).ToList();
            int[] numbers = { 1, 2, 4, 3 };
            var numbered = ordered.Select((w, i) => new { Wall = w, Number = numbers[i] }).ToList();

            // Correct the location-line plane to the actual wall-solid
            // mid-plane, which may be offset for Finish/Core Face walls.
            var solidPlaneOrigins = new Dictionary<int, XYZ>();
            foreach (Wall w in ordered)
            {
                Line axis = ((LocationCurve)w.Location).Curve as Line;
                if (axis == null) continue;
                XYZ original = axis.GetEndPoint(0);
                XYZ actual = OpeningHostPlaneService.MoveToWallSolidMidPlane(w, original);
                solidPlaneOrigins[w.Id.IntegerValue] = actual;
                _log.Info("HOST WALL PLANE Wall=" + w.Id.IntegerValue +
                          " SolidOffsetMm=" + UnitUtil.FtToMm(
                              new XYZ(actual.X - original.X,
                                      actual.Y - original.Y, 0).GetLength()).ToString("0.#"));
            }

            double scanMargin = UnitUtil.MmToFt(maxGapMm + 500);
            double minX = walls.Min(w => w.get_BoundingBox(null).Min.X) - scanMargin;
            double minY = walls.Min(w => w.get_BoundingBox(null).Min.Y) - scanMargin;
            double minZ = walls.Min(w => w.get_BoundingBox(null).Min.Z) - scanMargin;
            double maxX = walls.Max(w => w.get_BoundingBox(null).Max.X) + scanMargin;
            double maxY = walls.Max(w => w.get_BoundingBox(null).Max.Y) + scanMargin;
            double maxZ = walls.Max(w => w.get_BoundingBox(null).Max.Z) + scanMargin;

            BuiltInCategory[] categories =
            {
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_DuctCurves
            };
            double minNormalAlignment = Math.Cos(maxDeviationDeg * Math.PI / 180.0);
            var all = new List<VirtualMepCandidate>();
            var rejectedRows = new List<string>();

            foreach (RevitLinkInstance link in new FilteredElementCollector(_doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null)
                {
                    result.UnavailableLinks++;
                    _log.Warn("Virtual scanner unavailable link " + link.Id.IntegerValue +
                              " '" + link.Name + "'.");
                    continue;
                }

                result.LoadedLinks++;
                Transform transform = link.GetTotalTransform();
                var filter = new ElementMulticategoryFilter(categories);
                foreach (Element e in new FilteredElementCollector(linkDoc)
                    .WherePasses(filter).WhereElementIsNotElementType())
                {
                    var location = e.Location as LocationCurve;
                    Line localLine = location?.Curve as Line;
                    if (localLine == null) continue;
                    Curve transformed = localLine.CreateTransformed(transform);
                    Line line = transformed as Line;
                    if (line == null) continue;

                    XYZ p0 = line.GetEndPoint(0);
                    XYZ p1 = line.GetEndPoint(1);
                    // Avoid full-model endpoint processing when both endpoints are distant.
                    if (!NearEnvelope(p0, minX, minY, minZ, maxX, maxY, maxZ) &&
                        !NearEnvelope(p1, minX, minY, minZ, maxX, maxY, maxZ))
                        continue;

                    result.NearbyMep++;
                    var diagnostic = new List<string>();
                    bool hasActualCrossing = false;
                    foreach (var item in numbered)
                    {
                        Wall wall = item.Wall;
                        Line axis = ((LocationCurve)wall.Location).Curve as Line;
                        if (axis == null) continue;
                        XYZ a = solidPlaneOrigins.ContainsKey(wall.Id.IntegerValue)
                            ? solidPlaneOrigins[wall.Id.IntegerValue]
                            : axis.GetEndPoint(0);
                        XYZ tangent = new XYZ(axis.Direction.X, axis.Direction.Y, 0);
                        if (tangent.GetLength() < 1e-9) continue;
                        tangent = tangent.Normalize();
                        XYZ normal = new XYZ(-tangent.Y, tangent.X, 0);
                        BoundingBoxXYZ box = wall.get_BoundingBox(null);
                        if (box == null) continue;

                        double d0 = (p0 - a).DotProduct(normal);
                        double d1 = (p1 - a).DotProduct(normal);
                        // Already crossed the host mid-plane: this is not a
                        // virtual endpoint. Log it rather than dropping silently.
                        if (d0 * d1 <= 0)
                        {
                            // Crossing an INFINITE wall plane alone is not
                            // proof of a wall penetration. Check the finite
                            // segment and height at the actual intersection.
                            double denominatorCross = d0 - d1;
                            if (Math.Abs(denominatorCross) > 1e-9)
                            {
                                double tCross = d0 / denominatorCross;
                                if (tCross >= 0 && tCross <= 1)
                                {
                                    XYZ cross = p0 + (p1 - p0) * tCross;
                                    double crossAlong = (cross - a).DotProduct(tangent);
                                    if (crossAlong >= 0 && crossAlong <= axis.Length &&
                                        cross.Z >= box.Min.Z && cross.Z <= box.Max.Z)
                                    {
                                        hasActualCrossing = true;
                                        diagnostic.Add("W" + item.Number + ":ACTUAL_WALL_CROSSING");
                                        continue;
                                    }
                                }
                            }
                            diagnostic.Add("W" + item.Number + ":INFINITE_PLANE_ONLY");
                            // The section crosses the infinite plane outside
                            // this wall; its endpoint cannot project to this
                            // wall without reversing direction.
                            continue;
                        }

                        for (int endpoint = 0; endpoint < 2; endpoint++)
                        {
                            XYZ tip = endpoint == 0 ? p0 : p1;
                            XYZ other = endpoint == 0 ? p1 : p0;
                            XYZ extension = tip - other;
                            if (extension.GetLength() < 1e-9) continue;
                            extension = extension.Normalize();

                            double signed = (tip - a).DotProduct(normal);
                            // Approach deviation is PLAN/XY only; vertical duct
                            // slope must not be counted against the wall angle.
                            XYZ horizontal = new XYZ(extension.X, extension.Y, 0);
                            if (horizontal.GetLength() < 1e-9)
                            {
                                diagnostic.Add("W" + item.Number + ":VERTICAL_AXIS");
                                continue;
                            }
                            horizontal = horizontal.Normalize();
                            double inward = -Math.Sign(signed) *
                                horizontal.DotProduct(normal);
                            double deviation = Math.Acos(Math.Min(1,
                                Math.Max(-1, inward))) * 180.0 / Math.PI;
                            if (inward < minNormalAlignment)
                            {
                                if (inward > 0)
                                    diagnostic.Add("W" + item.Number + ":APPROACH_ANGLE_" +
                                        F(deviation) + "_DEG");
                                continue;
                            }

                            double faceGapFt = Math.Abs(signed) - wall.Width * 0.5;
                            // An endpoint inside the wall but not through its
                            // mid-plane is a valid *review-only* projection
                            // scenario. It does NOT count as an actual crossing.
                            bool insideWall = faceGapFt < -UnitUtil.MmToFt(5);
                            double insideDepthMm =
                                UnitUtil.FtToMm(Math.Max(0, -faceGapFt));
                            double gapMm = UnitUtil.FtToMm(Math.Max(0, faceGapFt));
                            if (gapMm > maxGapMm)
                            {
                                diagnostic.Add("W" + item.Number + ":GAP_" +
                                    F(gapMm) + "_MM");
                                continue;
                            }

                            double denominator = extension.DotProduct(normal);
                            if (Math.Abs(denominator) < 1e-8)
                            {
                                diagnostic.Add("W" + item.Number + ":PARALLEL_AXIS");
                                continue;
                            }
                            double reachFt = -signed / denominator;
                            if (reachFt <= 0)
                            {
                                diagnostic.Add("W" + item.Number + ":PROJECTION_BEHIND_ENDPOINT");
                                continue;
                            }
                            XYZ hit = tip + extension * reachFt;
                            double along = (hit - a).DotProduct(tangent);
                            if (along < 0 || along > axis.Length)
                            {
                                diagnostic.Add("W" + item.Number + ":OUTSIDE_WALL_LENGTH");
                                continue;
                            }
                            if (hit.Z < box.Min.Z || hit.Z > box.Max.Z)
                            {
                                diagnostic.Add("W" + item.Number + ":OUTSIDE_WALL_HEIGHT");
                                continue;
                            }

                            all.Add(new VirtualMepCandidate
                            {
                                LinkInstanceId = link.Id.IntegerValue,
                                LinkName = link.Name,
                                SourceElementId = e.Id.IntegerValue,
                                SourceUniqueId = e.UniqueId,
                                Category = e.Category?.Name ?? string.Empty,
                                SourceSize = ParamText(e, "Size"),
                                SystemName = ParamText(e, "System Name"),
                                SlopePercent = SlopePercent(extension),
                                WallId = wall.Id.IntegerValue,
                                WallNumber = item.Number,
                                Endpoint = endpoint,
                                ProjectedHit = hit,
                                GapToFaceMm = gapMm,
                                DepthInsideWallMm = insideWall ? insideDepthMm : 0,
                                ReachToAxisMm = UnitUtil.FtToMm(reachFt),
                                DeviationDeg = deviation,
                                Status = insideWall ? "INSIDE_WALL_REVIEW" : "REVIEW",
                                Reason = insideWall
                                    ? "Endpoint lies inside wall thickness but before its mid-plane. Verify in Revit before cutting."
                                    : "Virtual endpoint extension: approval required before any cut."
                            });
                            diagnostic.Add("W" + item.Number +
                                (insideWall ? ":INSIDE_WALL_REVIEW" : ":VIRTUAL_CANDIDATE"));
                        }
                    }

                    string state = diagnostic.Any(x =>
                        x.Contains("VIRTUAL_CANDIDATE") || x.Contains("INSIDE_WALL_REVIEW"))
                        ? "HAS_VIRTUAL_CANDIDATE"
                        : (hasActualCrossing ? "ACTUAL_CROSSING_REVIEW" : "NOT_ELIGIBLE_REVIEW");
                    string reasons = diagnostic.Count == 0 ? "NO_MATCHING_WALL_OR_DIRECTION"
                        : string.Join(" | ", diagnostic.Distinct());
                    // Every local MEP centerline produces one diagnostic row.
                    rejectedRows.Add(string.Join(",", new[]
                    {
                        Escape(state), Escape(link.Name), link.Id.IntegerValue.ToString(),
                        e.Id.IntegerValue.ToString(), Escape(e.UniqueId),
                        Escape(e.Category?.Name ?? string.Empty), Escape(ParamText(e, "Size")),
                        Escape(ParamText(e, "System Name")), Escape(reasons)
                    }));
                    _log.Info("NEARBY_MEP Link=" + link.Id.IntegerValue +
                              " Element=" + e.Id.IntegerValue +
                              " Size='" + ParamText(e, "Size") + "'" +
                              " Result=" + state + " Details=" + reasons);
                }
            }

            foreach (var group in all.GroupBy(x =>
                x.LinkInstanceId + "|" + x.SourceUniqueId + "|" + x.Endpoint))
            {
                List<VirtualMepCandidate> ranked = group.OrderBy(x => x.GapToFaceMm)
                    .ThenBy(x => x.ReachToAxisMm).ToList();
                VirtualMepCandidate best = ranked[0];
                if (ranked.Count > 1 &&
                    ranked[1].GapToFaceMm - best.GapToFaceMm <= 25)
                {
                    foreach (VirtualMepCandidate candidate in ranked)
                    {
                        candidate.Status = "AMBIGUOUS REVIEW";
                        candidate.Reason = "Several eligible nearby walls: manual confirmation required.";
                        result.Candidates.Add(candidate);
                    }
                }
                else
                {
                    result.Candidates.Add(best);
                }
            }

            foreach (VirtualMepCandidate c in result.Candidates
                .OrderBy(x => x.WallNumber).ThenBy(x => x.GapToFaceMm))
                _log.Warn("VIRTUAL MEP " + c.Status +
                          " Link=" + c.LinkInstanceId +
                          " Source=" + c.SourceElementId +
                          " Wall=W" + c.WallNumber + "(" + c.WallId + ")" +
                          " GapFaceMm=" + F(c.GapToFaceMm) +
                          " DepthInsideWallMm=" + F(c.DepthInsideWallMm) +
                          " ReachAxisMm=" + F(c.ReachToAxisMm) +
                          " DirectionDeg=" + F(c.DeviationDeg) +
                          " SlopePercent=" + F(c.SlopePercent) +
                          " HitFt=" + c.ProjectedHit + " " + c.Reason);

            result.CsvPath = WriteCsv(result.Candidates);
            result.DiagnosticCsvPath = WriteDiagnostics(rejectedRows);
            result.DiagnosedNonCandidates = rejectedRows.Count(row =>
                !row.StartsWith("\"HAS_VIRTUAL_CANDIDATE\"", StringComparison.Ordinal));
            _log.Info("Virtual candidates=" + result.Candidates.Count +
                      " NonCandidateNearbyMEP=" + result.DiagnosedNonCandidates +
                      " DiagnosticCSV=" + result.DiagnosticCsvPath +
                      " LoadedLinks=" + result.LoadedLinks +
                      " UnavailableLinks=" + result.UnavailableLinks +
                      " NearbyMEP=" + result.NearbyMep +
                      " CSV=" + result.CsvPath);
            _log.Info("No linked elements, walls or openings were changed.");
            return result;
        }

        private static bool NearEnvelope(XYZ p, double x0, double y0,
            double z0, double x1, double y1, double z1)
        {
            return p.X >= x0 && p.X <= x1 &&
                   p.Y >= y0 && p.Y <= y1 &&
                   p.Z >= z0 && p.Z <= z1;
        }

        private static string WriteCsv(IEnumerable<VirtualMepCandidate> records)
        {
            string folder = OutputPathService.GetLogsFolder();
            string path = Path.Combine(folder,
                "VirtualMepCandidates_" + DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture) + "_" +
                Guid.NewGuid().ToString("N").Substring(0, 6) + ".csv");
            var csv = new StringBuilder();
            csv.AppendLine("Status,Link,LinkId,ElementId,UniqueId,Category,SourceSize,System,Wall,WallId,Endpoint,GapToFace_mm,DepthInsideWall_mm,ReachToAxis_mm,Deviation_deg,Slope_percent,HitX_mm,HitY_mm,HitZ_mm,Reason");
            foreach (VirtualMepCandidate r in records)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    Escape(r.Status), Escape(r.LinkName),
                    r.LinkInstanceId.ToString(), r.SourceElementId.ToString(),
                    Escape(r.SourceUniqueId), Escape(r.Category),
                    Escape(r.SourceSize), Escape(r.SystemName),
                    "W" + r.WallNumber, r.WallId.ToString(),
                    r.Endpoint.ToString(), F(r.GapToFaceMm), F(r.DepthInsideWallMm), F(r.ReachToAxisMm),
                    F(r.DeviationDeg), F(r.SlopePercent),
                    F(UnitUtil.FtToMm(r.ProjectedHit.X)),
                    F(UnitUtil.FtToMm(r.ProjectedHit.Y)),
                    F(UnitUtil.FtToMm(r.ProjectedHit.Z)), Escape(r.Reason)
                }));
            }
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string ParamText(Element element, string name)
        {
            Parameter p = element.LookupParameter(name);
            if (p == null) return string.Empty;
            return p.AsString() ?? p.AsValueString() ?? string.Empty;
        }

        private static double SlopePercent(XYZ direction)
        {
            double horizontal = Math.Sqrt(direction.X * direction.X +
                                          direction.Y * direction.Y);
            return horizontal > 1e-9 ? direction.Z * 100.0 / horizontal : 0;
        }

        private static string WriteDiagnostics(IEnumerable<string> rows)
        {
            string folder = OutputPathService.GetLogsFolder();
            string path = Path.Combine(folder,
                "VirtualMepDiagnostics_" + DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture) + "_" +
                Guid.NewGuid().ToString("N").Substring(0, 6) + ".csv");
            var csv = new StringBuilder();
            csv.AppendLine("Classification,Link,LinkId,ElementId,UniqueId,Category,Size,System,PerWallReasons");
            foreach (string row in rows) csv.AppendLine(row);
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string F(double n)
        {
            return n.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Escape(string text)
        {
            char quote = (char)34;
            return quote + (text ?? string.Empty)
                .Replace(quote.ToString(), new string(quote, 2)) + quote;
        }
    }
}
