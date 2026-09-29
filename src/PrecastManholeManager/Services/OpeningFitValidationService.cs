using System;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class OpeningFitValidationService
    {
        public static bool TryValidate(
            Document doc,
            PenetrationRecord record,
            out string reason)
        {
            reason = null;

            if (doc == null)
            {
                reason = "Document is null.";
                return false;
            }

            if (record == null)
            {
                reason = "Penetration record is null.";
                return false;
            }

            if (record.CutWidthMm <= 0 || record.CutHeightMm <= 0)
            {
                reason = "Opening size is unresolved.";
                return false;
            }

            Wall wall = doc.GetElement(new ElementId(record.HostWallId)) as Wall;
            if (wall == null)
            {
                reason = "Host wall " + record.HostWallId + " was not found.";
                return false;
            }

            LocationCurve location = wall.Location as LocationCurve;
            Line line = location?.Curve as Line;
            if (line == null)
            {
                reason = "Host wall has no straight usable location line.";
                return false;
            }

            BoundingBoxXYZ box = wall.get_BoundingBox(null);
            if (box == null)
            {
                reason = "Host wall has no model bounding box.";
                return false;
            }

            XYZ a = line.GetEndPoint(0);
            XYZ b = line.GetEndPoint(1);

            XYZ tangent = b - a;
            tangent = new XYZ(tangent.X, tangent.Y, 0);
            if (tangent.GetLength() < 1e-9)
            {
                reason = "Host wall has invalid horizontal direction.";
                return false;
            }

            tangent = tangent.Normalize();

            XYZ center = new XYZ(
                UnitUtil.MmToFt(record.Xmm),
                UnitUtil.MmToFt(record.Ymm),
                UnitUtil.MmToFt(record.Zmm));

            double halfWidthFt = UnitUtil.MmToFt(record.CutWidthMm) / 2.0;
            double halfHeightFt = UnitUtil.MmToFt(record.CutHeightMm) / 2.0;
            double edgeMarginFt = UnitUtil.MmToFt(5.0);

            double wallLengthFt = line.Length;
            double alongFt = (center - a).DotProduct(tangent);

            if (alongFt - halfWidthFt < edgeMarginFt ||
                alongFt + halfWidthFt > wallLengthFt - edgeMarginFt)
            {
                reason =
                    "Opening extends beyond wall horizontal limits. " +
                    "WallLength=" + UnitUtil.FtToMm(wallLengthFt).ToString("0.#") + " mm, " +
                    "CenterAlong=" + UnitUtil.FtToMm(alongFt).ToString("0.#") + " mm, " +
                    "OpeningWidth=" + record.CutWidthMm.ToString("0.#") + " mm.";
                return false;
            }

            double bottomZ = center.Z - halfHeightFt;
            double topZ = center.Z + halfHeightFt;

            if (bottomZ < box.Min.Z + edgeMarginFt ||
                topZ > box.Max.Z - edgeMarginFt)
            {
                reason =
                    "Opening extends beyond wall vertical limits. " +
                    "WallBottom=" + UnitUtil.FtToMm(box.Min.Z).ToString("0.#") + " mm, " +
                    "WallTop=" + UnitUtil.FtToMm(box.Max.Z).ToString("0.#") + " mm, " +
                    "OpeningBottom=" + UnitUtil.FtToMm(bottomZ).ToString("0.#") + " mm, " +
                    "OpeningTop=" + UnitUtil.FtToMm(topZ).ToString("0.#") + " mm.";
                return false;
            }

            return true;
        }
    }
}
