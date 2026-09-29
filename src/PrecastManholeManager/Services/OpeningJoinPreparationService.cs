using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class OpeningJoinPreparationService
    {
        public static List<ElementId> UnjoinConflictingGeometry(
            Document doc,
            Wall wall,
            PenetrationRecord record,
            DiagnosticLogger log)
        {
            var unjoined = new List<ElementId>();
            if (doc == null || wall == null || record == null)
                return unjoined;

            ICollection<ElementId> joinedIds;
            try
            {
                joinedIds = JoinGeometryUtils.GetJoinedElements(doc, wall);
            }
            catch
            {
                return unjoined;
            }

            if (joinedIds == null || joinedIds.Count == 0)
                return unjoined;

            BoundingBoxXYZ openingBox = BuildOpeningBounds(wall, record);
            if (openingBox == null)
                return unjoined;

            double margin = UnitUtil.MmToFt(25.0);
            XYZ min = new XYZ(
                openingBox.Min.X - margin,
                openingBox.Min.Y - margin,
                openingBox.Min.Z - margin);
            XYZ max = new XYZ(
                openingBox.Max.X + margin,
                openingBox.Max.Y + margin,
                openingBox.Max.Z + margin);

            foreach (ElementId id in joinedIds)
            {
                Element other = doc.GetElement(id);
                if (other == null)
                    continue;

                BoundingBoxXYZ otherBox = other.get_BoundingBox(null);
                if (otherBox == null)
                    continue;

                if (!BoxesOverlap(min, max, otherBox.Min, otherBox.Max))
                    continue;

                try
                {
                    if (!JoinGeometryUtils.AreElementsJoined(doc, wall, other))
                        continue;

                    JoinGeometryUtils.UnjoinGeometry(doc, wall, other);
                    unjoined.Add(id);

                    log?.Warn(
                        "UNJOIN FOR OPENING Wall=" + wall.Id.IntegerValue +
                        " Other=" + id.IntegerValue +
                        " OtherCategory='" + (other.Category?.Name ?? string.Empty) +
                        "' Source=" + record.LinkedElementId);
                }
                catch (Exception ex)
                {
                    log?.Warn(
                        "Could not unjoin Wall=" + wall.Id.IntegerValue +
                        " from Element=" + id.IntegerValue +
                        " before opening creation: " + ex.Message);
                }
            }

            return unjoined;
        }

        private static BoundingBoxXYZ BuildOpeningBounds(
            Wall wall,
            PenetrationRecord record)
        {
            LocationCurve location = wall.Location as LocationCurve;
            Curve curve = location?.Curve;
            if (curve == null)
                return null;

            XYZ tangent = curve.GetEndPoint(1) - curve.GetEndPoint(0);
            tangent = new XYZ(tangent.X, tangent.Y, 0);
            if (tangent.GetLength() < 1e-9)
                return null;

            tangent = tangent.Normalize();
            XYZ normal = new XYZ(-tangent.Y, tangent.X, 0);

            XYZ center = new XYZ(
                UnitUtil.MmToFt(record.Xmm),
                UnitUtil.MmToFt(record.Ymm),
                UnitUtil.MmToFt(record.Zmm));

            double halfWidth = UnitUtil.MmToFt(record.CutWidthMm) / 2.0;
            double halfHeight = UnitUtil.MmToFt(record.CutHeightMm) / 2.0;
            double halfDepth = Math.Max(wall.Width / 2.0, UnitUtil.MmToFt(50));

            XYZ[] points =
            {
                center + tangent * halfWidth + normal * halfDepth + XYZ.BasisZ * halfHeight,
                center + tangent * halfWidth + normal * halfDepth - XYZ.BasisZ * halfHeight,
                center + tangent * halfWidth - normal * halfDepth + XYZ.BasisZ * halfHeight,
                center + tangent * halfWidth - normal * halfDepth - XYZ.BasisZ * halfHeight,
                center - tangent * halfWidth + normal * halfDepth + XYZ.BasisZ * halfHeight,
                center - tangent * halfWidth + normal * halfDepth - XYZ.BasisZ * halfHeight,
                center - tangent * halfWidth - normal * halfDepth + XYZ.BasisZ * halfHeight,
                center - tangent * halfWidth - normal * halfDepth - XYZ.BasisZ * halfHeight
            };

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double minZ = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            double maxZ = double.MinValue;

            foreach (XYZ p in points)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
                maxZ = Math.Max(maxZ, p.Z);
            }

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private static bool BoxesOverlap(
            XYZ minA,
            XYZ maxA,
            XYZ minB,
            XYZ maxB)
        {
            return minA.X <= maxB.X && maxA.X >= minB.X &&
                   minA.Y <= maxB.Y && maxA.Y >= minB.Y &&
                   minA.Z <= maxB.Z && maxA.Z >= minB.Z;
        }
    }
}
