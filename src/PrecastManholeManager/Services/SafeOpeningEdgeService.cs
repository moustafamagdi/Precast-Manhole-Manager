using System;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    // Reduces ONLY excess clearance near a wall edge. Never cuts into the MEP
    // section itself. Fit dimensions/center are applied only to a batch copy.
    internal static class SafeOpeningEdgeService
    {
        public static bool TryTrimClearance(
            Document doc, PenetrationRecord record, DiagnosticLogger log,
            out string reason)
        {
            reason = null;
            Wall wall = doc.GetElement(new ElementId(record.HostWallId)) as Wall;
            Line axis = (wall?.Location as LocationCurve)?.Curve as Line;
            BoundingBoxXYZ box = wall?.get_BoundingBox(null);
            if (wall == null || axis == null || box == null)
            {
                reason = "Missing straight wall or wall bounding box.";
                return false;
            }

            double actualWidth = record.Shape == "Round" ? record.DiameterMm : record.WidthMm;
            double actualHeight = record.Shape == "Round" ? record.DiameterMm : record.HeightMm;
            if (actualWidth <= 0 || actualHeight <= 0)
            {
                reason = "Source MEP section size is unknown. Trimming is forbidden.";
                return false;
            }

            XYZ a = axis.GetEndPoint(0), b = axis.GetEndPoint(1);
            XYZ tangent = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (tangent.GetLength() < 1e-9)
            {
                reason = "Invalid host wall axis.";
                return false;
            }
            tangent = tangent.Normalize();
            XYZ p = new XYZ(UnitUtil.MmToFt(record.Xmm),
                UnitUtil.MmToFt(record.Ymm), UnitUtil.MmToFt(record.Zmm));
            double alongMm = UnitUtil.FtToMm((p - a).DotProduct(tangent));
            double lengthMm = UnitUtil.FtToMm(axis.Length);
            double bottomMm = UnitUtil.FtToMm(box.Min.Z), topMm = UnitUtil.FtToMm(box.Max.Z);
            const double edgeMm = 5;

            double originalWidth = record.Shape == "Round" ?
                record.DiameterMm + 2 * record.ClearanceMm :
                record.WidthMm + 2 * record.ClearanceMm;
            double originalHeight = record.Shape == "Round" ?
                record.DiameterMm + 2 * record.ClearanceMm :
                record.HeightMm + 2 * record.ClearanceMm;

            double targetLeft = alongMm - originalWidth / 2;
            double targetRight = alongMm + originalWidth / 2;
            double targetBottom = record.Zmm - originalHeight / 2;
            double targetTop = record.Zmm + originalHeight / 2;

            double left = Math.Max(edgeMm, targetLeft);
            double right = Math.Min(lengthMm - edgeMm, targetRight);
            double bottom = Math.Max(bottomMm + edgeMm, targetBottom);
            double top = Math.Min(topMm - edgeMm, targetTop);

            if (left > alongMm - actualWidth / 2 + 0.1 ||
                right < alongMm + actualWidth / 2 - 0.1 ||
                bottom > record.Zmm - actualHeight / 2 + 0.1 ||
                top < record.Zmm + actualHeight / 2 - 0.1)
            {
                reason = "MEP section itself would extend beyond the wall-safe opening. " +
                    "Cannot trim source clearance without cutting into the service.";
                return false;
            }
            if (right - left < actualWidth - 0.1 || top - bottom < actualHeight - 0.1)
            {
                reason = "Insufficient opening size after trimming.";
                return false;
            }

            double newAlong = (left + right) / 2;
            double newZ = (bottom + top) / 2;
            XYZ moved = p + tangent * UnitUtil.MmToFt(newAlong - alongMm);

            record.Xmm = UnitUtil.FtToMm(moved.X);
            record.Ymm = UnitUtil.FtToMm(moved.Y);
            record.Zmm = newZ;
            record.CutWidthOverrideMm = right - left;
            record.CutHeightOverrideMm = top - bottom;
            record.OffsetFromWallStartMm += newAlong - alongMm;

            bool valid = OpeningFitValidationService.TryValidate(doc, record, out reason);
            log?.Info("EDGE TRIM source=" + record.LinkedElementId +
                " Wall=" + record.HostWallId +
                " Original=" + originalWidth.ToString("0.#") + "x" +
                originalHeight.ToString("0.#") + " mm Trimmed=" +
                record.CutWidthMm.ToString("0.#") + "x" +
                record.CutHeightMm.ToString("0.#") + " mm Result=" +
                (valid ? "FIT" : ("REVIEW " + reason)));
            return valid;
        }
    }
}
