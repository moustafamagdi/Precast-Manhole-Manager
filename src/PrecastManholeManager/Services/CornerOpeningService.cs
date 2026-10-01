using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class CornerOpeningService
    {
        internal static void Qualify(Document doc, VirtualFoundationResult footprint,
            IList<UnifiedOpeningReviewRow> rows, DiagnosticLogger log)
        {
            var candidates = rows.Where(r => !r.IsVirtual || r.EndpointQualified).ToList();
            foreach (var source in candidates.GroupBy(r => r.Source.LinkInstanceId + "|" + (r.Source.LinkedUniqueId ?? r.Source.LinkedElementId.ToString())))
            {
                var pairRows = source.ToList();
                for (int i = 0; i < pairRows.Count; i++)
                for (int j = i + 1; j < pairRows.Count; j++)
                {
                    var a = pairRows[i].Source; var b = pairRows[j].Source;
                    if (a.HostWallId == b.HostWallId) continue;
                    var wa = footprint.Walls.FirstOrDefault(w => w.Id.IntegerValue == a.HostWallId);
                    var wb = footprint.Walls.FirstOrDefault(w => w.Id.IntegerValue == b.HostWallId);
                    var la = (wa?.Location as LocationCurve)?.Curve as Line;
                    var lb = (wb?.Location as LocationCurve)?.Curve as Line;
                    if (la == null || lb == null || Math.Abs(la.Direction.DotProduct(lb.Direction)) > 0.1) continue;
                    int ea = -1, eb = -1;
                    double nearest = double.MaxValue;
                    for (int x = 0; x < 2; x++)
                    for (int y = 0; y < 2; y++)
                    {
                        var delta = la.GetEndPoint(x) - lb.GetEndPoint(y);
                        double distance = new XYZ(delta.X, delta.Y, 0).GetLength();
                        if (distance < nearest) { nearest = distance; ea = x; eb = y; }
                    }
                    // Connected wall-end geometry is required, not merely the same
                    // source crossing two unrelated or opposite wall planes.
                    if (nearest > (wa.Width + wb.Width) / 2 + UnitUtil.MmToFt(10)) continue;
                    if (!Project(doc, a, wa, la, log) || !Project(doc, b, wb, lb, log)) continue;
                    double alongA = Along(a, la), alongB = Along(b, lb);
                    if (!NearEnd(alongA, a.CutWidthMm, UnitUtil.FtToMm(la.Length), ea) ||
                        !NearEnd(alongB, b.CutWidthMm, UnitUtil.FtToMm(lb.Length), eb)) continue;
                    if (ea == 0) a.CornerStartAllowed = true; else a.CornerEndAllowed = true;
                    if (eb == 0) b.CornerStartAllowed = true; else b.CornerEndAllowed = true;
                    log.Info("CORNER OPENING QUALIFIED Source=" + a.LinkedElementId + " Walls=" + a.HostWallId + "/" + b.HostWallId +
                        " Envelopes=" + a.CutWidthMm.ToString("0.#") + "x" + a.CutHeightMm.ToString("0.#") + ";" +
                        b.CutWidthMm.ToString("0.#") + "x" + b.CutHeightMm.ToString("0.#"));
                }
            }
        }

        internal static bool NearEnd(double along, double width, double length, int end)
        {
            double edge = end == 0 ? 0 : length;
            return OpeningFitValidationService.HorizontalFits(along, width, length, end == 0, end == 1) &&
                Math.Abs(along - edge) < width / 2;
        }

        // Clamp only a verified shared-corner envelope, without moving the source
        // or reducing its clearance inside the actual host wall.
        internal static double[] Clip(double center, double width, double length, bool start, bool end)
        {
            if (!OpeningFitValidationService.HorizontalFits(center, width, length, start, end)) return null;
            double left = Math.Max(0, center - width / 2), right = Math.Min(length, center + width / 2);
            return new[] { (left + right) / 2, right - left };
        }

        internal static void Fit(Document doc, PenetrationRecord r)
        {
            if (!r.CornerStartAllowed && !r.CornerEndAllowed) return;
            var wall = doc.GetElement(new ElementId(r.HostWallId)) as Wall;
            var axis = (wall?.Location as LocationCurve)?.Curve as Line;
            if (axis == null) return;
            double along = Along(r, axis);
            var clipped = Clip(along, r.CutWidthMm, UnitUtil.FtToMm(axis.Length), r.CornerStartAllowed, r.CornerEndAllowed);
            if (clipped == null) return;
            r.CutWidthOverrideMm = clipped[1];
            r.FittedCenterXmm = r.Xmm + (clipped[0] - along) * axis.Direction.X;
            r.FittedCenterYmm = r.Ymm + (clipped[0] - along) * axis.Direction.Y;
        }

        // A finite source must traverse the entire wall thickness; do not create
        // a corner candidate from an arbitrarily extended pipe axis.
        internal static bool TraversesSlab(double station, double sourceLength, double normalDot, double thickness)
        {
            if (double.IsNaN(station) || double.IsInfinity(station) || sourceLength <= 0 ||
                double.IsNaN(sourceLength) || double.IsInfinity(sourceLength) ||
                double.IsNaN(normalDot) || double.IsInfinity(normalDot) || Math.Abs(normalDot) < 1e-6 ||
                double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= 0) return false;
            double travel = thickness / (2 * Math.Abs(normalDot));
            return station - travel >= -1e-6 && station + travel <= sourceLength + 1e-6;
        }

        internal static void AddAdjacentPipeRows(Document doc, VirtualFoundationResult footprint,
            IList<UnifiedOpeningReviewRow> rows, IDictionary<int, int> wallNumbers, DiagnosticLogger log)
        {
            var additions = new List<UnifiedOpeningReviewRow>();
            var keys = new HashSet<string>(rows.Select(r => r.Source.SourceKey));
            foreach (var seed in rows.Where(r => !r.IsVirtual).ToList())
            {
                try
                {
                    var original = seed.Source;
                    var link = doc.GetElement(new ElementId(original.LinkInstanceId)) as RevitLinkInstance;
                    var pipe = link?.GetLinkDocument()?.GetElement(new ElementId(original.LinkedElementId)) as MEPCurve;
                    if (pipe?.Category?.Id.IntegerValue != (int)BuiltInCategory.OST_PipeCurves || original.Shape != "Round") continue;
                    var line = (pipe.Location as LocationCurve)?.Curve as Line;
                    if (line == null) continue;
                    var transform = link.GetTotalTransform();
                    var p0 = transform.OfPoint(line.GetEndPoint(0));
                    var delta = transform.OfPoint(line.GetEndPoint(1)) - p0;
                    var direction = delta.Normalize();
                    foreach (var wall in footprint.Walls)
                    {
                        int id = wall.Id.IntegerValue;
                        string key = original.LinkInstanceId + "|" + (original.LinkedUniqueId ?? original.LinkedElementId.ToString()) + "|" + id;
                        if (keys.Contains(key)) continue;
                        var axis = (wall.Location as LocationCurve)?.Curve as Line;
                        if (axis == null) continue;
                        var tangent = new XYZ(axis.Direction.X, axis.Direction.Y, 0).Normalize();
                        var normal = new XYZ(-tangent.Y, tangent.X, 0);
                        double dn = direction.DotProduct(normal);
                        if (Math.Abs(dn) < 1e-6) continue;
                        double station = (axis.GetEndPoint(0) - p0).DotProduct(normal) / dn;
                        if (!TraversesSlab(station, delta.GetLength(), dn, wall.Width)) continue;
                        var hit = p0 + direction * station;
                        var r = new PenetrationRecord {
                            LinkName = original.LinkName, LinkInstanceId = original.LinkInstanceId,
                            LinkedElementId = original.LinkedElementId, LinkedUniqueId = original.LinkedUniqueId,
                            Category = original.Category, FamilyType = original.FamilyType, SystemName = original.SystemName,
                            Size = original.Size, Shape = original.Shape, DiameterMm = original.DiameterMm,
                            ClearanceMm = original.ClearanceMm, HostWallId = id, WallNumber = wallNumbers[id],
                            Xmm = UnitUtil.FtToMm(hit.X), Ymm = UnitUtil.FtToMm(hit.Y), Zmm = UnitUtil.FtToMm(hit.Z)
                        };
                        if (!Project(doc, r, wall, axis, log)) continue;
                        double along = Along(r, axis), length = UnitUtil.FtToMm(axis.Length);
                        // Require physical pipe overlap, not just an overlap of clearance.
                        if (!NearEnd(along, r.ProjectedWidthMm, length, 0) && !NearEnd(along, r.ProjectedWidthMm, length, 1)) continue;
                        r.OffsetFromWallStartMm = along;
                        r.InvertMm = r.Zmm - r.ProjectedHeightMm / 2;
                        r.InvertAboveBaseMm = r.InvertMm - seed.BaseTopZmm;
                        additions.Add(new UnifiedOpeningReviewRow {
                            Source = r, Detection = "CORNER ENVELOPE", Wall = "W" + r.WallNumber, WallId = id,
                            SourceId = r.LinkedElementId, LinkName = r.LinkName, Service = seed.Service,
                            SourceSize = r.Size, BaseTopZmm = seed.BaseTopZmm
                        });
                        keys.Add(key);
                    }
                }
                catch (Exception ex) { log.Warn("CORNER ADJACENT REVIEW Source=" + seed.SourceId + ": " + ex.Message); }
            }
            foreach (var row in additions) rows.Add(row);
            Qualify(doc, footprint, rows, log);
            foreach (var row in additions)
            {
                if (!row.Source.CornerStartAllowed && !row.Source.CornerEndAllowed) rows.Remove(row);
                else log.Info("CORNER ADJACENT PIPE Source=" + row.SourceId + " Wall=" + row.Wall + " finite pipe envelope verified");
            }
        }

        private static double Along(PenetrationRecord r, Line axis) =>
            UnitUtil.FtToMm((new XYZ(UnitUtil.MmToFt(r.Xmm), UnitUtil.MmToFt(r.Ymm), UnitUtil.MmToFt(r.Zmm)) -
                axis.GetEndPoint(0)).DotProduct(axis.Direction));

        private static bool Project(Document doc, PenetrationRecord r, Wall wall, Line axis, DiagnosticLogger log)
        {
            try
            {
                var link = doc.GetElement(new ElementId(r.LinkInstanceId)) as RevitLinkInstance;
                var element = link?.GetLinkDocument()?.GetElement(r.LinkedUniqueId) as MEPCurve;
                var sourceLine = (element?.Location as LocationCurve)?.Curve as Line;
                if (sourceLine == null) return false;
                var tr = link.GetTotalTransform();
                var direction = tr.OfVector(sourceLine.Direction).Normalize();
                var tangent = new XYZ(axis.Direction.X, axis.Direction.Y, 0).Normalize();
                var normal = new XYZ(-tangent.Y, tangent.X, 0);
                double width, height;
                if (!new VirtualMepExtensionScanner(doc, log).TryConnectorEnvelope(element, tr,
                    tr.OfPoint(sourceLine.GetEndPoint(0)), direction, normal, tangent, wall.Width, out width, out height)) return false;
                r.ProjectedWidthMm = width; r.ProjectedHeightMm = height;
                return true;
            }
            catch (Exception ex) { log.Warn("CORNER PROJECTION REVIEW Source=" + r.LinkedElementId + ": " + ex.Message); return false; }
        }
    }
}
