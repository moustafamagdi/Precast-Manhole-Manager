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

        internal static double AdjustedCenterInSpan(double center, double width, double length, double start, double end)
        {
            if (double.IsNaN(start) || double.IsInfinity(start) || double.IsNaN(end) || double.IsInfinity(end) ||
                start < 0 || end > length || end <= start || center + width / 2 <= 0 || center - width / 2 >= length) return double.NaN;
            // The source must overlap the actual wall, but may be inside its joined end zone.
            if (width <= 0 || width > end - start - 5 || double.IsNaN(center) || double.IsInfinity(center) ||
                double.IsNaN(width) || double.IsInfinity(width) || double.IsNaN(length) || double.IsInfinity(length)) return double.NaN;
            if (center - width / 2 < start + 5) return start + width / 2;
            if (center + width / 2 > end - 5) return end - width / 2;
            return center;
        }

        private static double JoinedEndInset(Wall wall, Line axis, int end)
        {
            double inset = 0;
            var location = wall.Location as LocationCurve;
            foreach (Element element in location.get_ElementsAtJoin(end))
            {
                var neighbor = element as Wall;
                var other = (neighbor?.Location as LocationCurve)?.Curve as Line;
                if (other == null || neighbor.Id == wall.Id || Math.Abs(axis.Direction.DotProduct(other.Direction)) > 0.1) continue;
                var origin = axis.GetEndPoint(end);
                var a = other.GetEndPoint(0) - origin;
                var b = other.GetEndPoint(1) - origin;
                var delta = new XYZ(a.X, a.Y, 0).GetLength() < new XYZ(b.X, b.Y, 0).GetLength() ? a : b;
                if (new XYZ(delta.X, delta.Y, 0).GetLength() > (wall.Width + neighbor.Width) / 2 + UnitUtil.MmToFt(10)) continue;
                double inward = delta.DotProduct(axis.Direction) * (end == 0 ? 1 : -1);
                // One mm beyond the adjacent wall's inner face avoids a coincident joined edge.
                inset = Math.Max(inset, UnitUtil.FtToMm(inward + neighbor.Width / 2) + 1);
            }
            return inset;
        }

        internal static void Apply(Document doc, PenetrationRecord r)
        {
            r.EdgeAligned = false; r.EdgeShiftMm = 0; r.EdgeFitReview = null;
            var link = doc.GetElement(new ElementId(r.LinkInstanceId)) as RevitLinkInstance;
            var source = link?.GetLinkDocument()?.GetElement(new ElementId(r.LinkedElementId));
            int category = source?.Category?.Id.IntegerValue ?? 0;
            if (category != (int)BuiltInCategory.OST_DuctCurves && category != (int)BuiltInCategory.OST_PipeCurves) return;
            var wall = doc.GetElement(new ElementId(r.HostWallId)) as Wall;
            var axis = (wall?.Location as LocationCurve)?.Curve as Line;
            if (axis == null) return;
            var tangent = new XYZ(axis.Direction.X, axis.Direction.Y, 0).Normalize();
            var center = new XYZ(UnitUtil.MmToFt(r.Xmm), UnitUtil.MmToFt(r.Ymm), UnitUtil.MmToFt(r.Zmm));
            double along = UnitUtil.FtToMm((center - axis.GetEndPoint(0)).DotProduct(tangent));
            double length = UnitUtil.FtToMm(axis.Length);
            double start = JoinedEndInset(wall, axis, 0);
            double end = length - JoinedEndInset(wall, axis, 1);
            double adjusted = AdjustedCenterInSpan(along, r.CutWidthMm, length, start, end);
            if (double.IsNaN(adjusted))
            {
                r.CornerStartAllowed = r.CornerEndAllowed = false;
                r.EdgeFitReview = "Full-size edge opening cannot fit the usable wall span. Required=" + r.CutWidthMm.ToString("0.#") +
                    " mm; usable=" + (end - start).ToString("0.#") + " mm. Opening size and source location preserved for review.";
                return;
            }
            if (Math.Abs(adjusted - along) < 0.001 && along - r.CutWidthMm / 2 >= start + 5 && along + r.CutWidthMm / 2 <= end - 5) return;
            r.EdgeShiftMm = adjusted - along;
            r.FittedCenterXmm = r.Xmm + r.EdgeShiftMm * tangent.X;
            r.FittedCenterYmm = r.Ymm + r.EdgeShiftMm * tangent.Y;
            r.EdgeAligned = true;
            // Keep full pipe/duct opening size. Only the cut moves; source geometry is untouched.
            r.CornerStartAllowed = r.CornerEndAllowed = false;
        }
    }
}
