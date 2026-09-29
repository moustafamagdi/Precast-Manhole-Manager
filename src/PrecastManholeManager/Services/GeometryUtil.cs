using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class GeometryUtil
    {
        public static IEnumerable<Solid> GetSolids(Element element, DiagnosticLogger log)
        {
            if (element == null) yield break;

            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement geo = null;
            try { geo = element.get_Geometry(options); }
            catch (Exception ex)
            {
                log?.Error($"Failed reading geometry for element {element.Id.IntegerValue}.", ex);
            }

            if (geo == null) yield break;

            foreach (GeometryObject obj in geo)
            {
                foreach (var solid in ExtractSolids(obj))
                    if (solid != null && solid.Volume > 1e-9)
                        yield return solid;
            }
        }

        private static IEnumerable<Solid> ExtractSolids(GeometryObject obj)
        {
            if (obj is Solid solid)
            {
                yield return solid;
                yield break;
            }

            if (obj is GeometryInstance instance)
            {
                var instanceGeo = instance.GetInstanceGeometry();
                if (instanceGeo == null) yield break;

                foreach (GeometryObject child in instanceGeo)
                    foreach (var s in ExtractSolids(child))
                        yield return s;
            }
        }

        public static XYZ Midpoint(Curve curve)
        {
            return curve?.Evaluate(0.5, true);
        }

        public static double DistancePointToUnboundedLine2D(XYZ point, Line line)
        {
            XYZ p0 = line.GetEndPoint(0);
            XYZ d = line.Direction;
            XYZ v = point - p0;
            return Math.Abs(v.X * d.Y - v.Y * d.X);
        }
    }
}
