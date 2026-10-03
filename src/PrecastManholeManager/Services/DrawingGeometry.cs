using System;
using System.Linq;

namespace Hatco.PrecastManholeManager.Services
{
    // Paper-space rectangles: xmin, ymin, xmax, ymax. No Revit calls in these policies.
    internal static class DrawingGeometry
    {
        internal static bool Valid(double[] r) => r != null && r.Length == 4 &&
            r.All(x => !double.IsNaN(x) && !double.IsInfinity(x)) && r[2] > r[0] && r[3] > r[1];

        internal static bool Contains(double[] outer, double[] inner, double tolerance) =>
            Valid(outer) && Valid(inner) && tolerance >= 0 && !double.IsInfinity(tolerance) &&
            inner[0] >= outer[0] - tolerance && inner[1] >= outer[1] - tolerance &&
            inner[2] <= outer[2] + tolerance && inner[3] <= outer[3] + tolerance;

        internal static bool Overlaps(double[] a, double[] b, double tolerance) =>
            Valid(a) && Valid(b) && tolerance >= 0 && !double.IsInfinity(tolerance) &&
            Math.Min(a[2], b[2]) - Math.Max(a[0], b[0]) > tolerance &&
            Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]) > tolerance;

        internal static double[] Union(double[] a, double[] b) => !Valid(b) ? a : !Valid(a) ? b :
            new[] { Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Max(a[2], b[2]), Math.Max(a[3], b[3]) };

        internal static bool ContainsPoints(double[] box, double[][] points, double tolerance, bool depth)
        {
            if (box == null || box.Length != 6 || points == null || points.Length == 0 ||
                box.Any(x => double.IsNaN(x) || double.IsInfinity(x)) || !(tolerance >= 0) || double.IsInfinity(tolerance) ||
                box[3] <= box[0] || box[4] <= box[1] || (depth && box[5] <= box[2])) return false;
            foreach (var p in points)
            {
                if (p == null || p.Length != 3 || p.Any(x => double.IsNaN(x) || double.IsInfinity(x))) return false;
                for (int axis = 0; axis < (depth ? 3 : 2); axis++)
                    if (p[axis] < box[axis] - tolerance || p[axis] > box[axis + 3] + tolerance) return false;
            }
            return true;
        }

        internal static double[] Row(double[] sheet, int row)
        {
            if (!Valid(sheet) || row < 0 || row >= 6) throw new ArgumentException("Six-row sheet bounds required.");
            const double mm = 1.0 / 304.8;
            double left = sheet[0] + 22 * mm, right = sheet[2] - 165 * mm;
            double top = sheet[3] - 20 * mm, bottom = sheet[1] + 20 * mm;
            var result = new[] { left, top - (top - bottom) * (row + 1) / 6, right, top - (top - bottom) * row / 6 };
            if (!Valid(result)) throw new ArgumentException("Titleblock is too small for the six-row layout.");
            return result;
        }
    }
}
