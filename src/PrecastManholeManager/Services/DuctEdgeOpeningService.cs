using System;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class DuctEdgeOpeningService
    {
        // Retain the complete opening width and the opposite end's normal margin.
        // NaN means this cannot be solved by shifting a cut along the wall.
        internal static double AdjustedCenter(double center, double width, double length)
        {
            if (double.IsNaN(center) || double.IsInfinity(center) || double.IsNaN(width) || double.IsInfinity(width) ||
                double.IsNaN(length) || double.IsInfinity(length) || width <= 0 || width > length - 5 ||
                center + width / 2 <= 0 || center - width / 2 >= length) return double.NaN;
            if (center - width / 2 < 5) return width / 2;
            if (center + width / 2 > length - 5) return length - width / 2;
            return center;
        }

        internal static void Apply(Document doc, PenetrationRecord r)
        {
            r.EdgeAligned = false; r.EdgeShiftMm = 0;
            var link = doc.GetElement(new ElementId(r.LinkInstanceId)) as RevitLinkInstance;
            var source = link?.GetLinkDocument()?.GetElement(new ElementId(r.LinkedElementId));
            if (source?.Category?.Id.IntegerValue != (int)BuiltInCategory.OST_DuctCurves) return;
            var wall = doc.GetElement(new ElementId(r.HostWallId)) as Wall;
            var axis = (wall?.Location as LocationCurve)?.Curve as Line;
            if (axis == null) return;
            var tangent = new XYZ(axis.Direction.X, axis.Direction.Y, 0).Normalize();
            var center = new XYZ(UnitUtil.MmToFt(r.Xmm), UnitUtil.MmToFt(r.Ymm), UnitUtil.MmToFt(r.Zmm));
            double along = UnitUtil.FtToMm((center - axis.GetEndPoint(0)).DotProduct(tangent));
            double length = UnitUtil.FtToMm(axis.Length);
            double adjusted = AdjustedCenter(along, r.CutWidthMm, length);
            if (double.IsNaN(adjusted) || (along - r.CutWidthMm / 2 >= 5 && along + r.CutWidthMm / 2 <= length - 5)) return;
            r.EdgeShiftMm = adjusted - along;
            r.FittedCenterXmm = r.Xmm + r.EdgeShiftMm * tangent.X;
            r.FittedCenterYmm = r.Ymm + r.EdgeShiftMm * tangent.Y;
            r.EdgeAligned = true;
            // A shifted duct opening must fit entirely on its own host wall.
            r.CornerStartAllowed = r.CornerEndAllowed = false;
        }
    }
}
