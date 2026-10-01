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
            return width > 0 && width < length && Math.Abs(along - edge) < width / 2 &&
                along + width / 2 > 1 && along - width / 2 < length - 1;
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
