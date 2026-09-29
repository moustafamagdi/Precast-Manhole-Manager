using System;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class OpeningHostPlaneService
    {
        public static XYZ MoveToWallSolidMidPlane(
            Wall wall,
            XYZ proposedCenter)
        {
            if (wall == null || proposedCenter == null)
                return proposedCenter;

            LocationCurve location = wall.Location as LocationCurve;
            Curve curve = location?.Curve;
            if (curve == null)
                return proposedCenter;

            XYZ tangent = curve.GetEndPoint(1) - curve.GetEndPoint(0);
            tangent = new XYZ(tangent.X, tangent.Y, 0);
            if (tangent.GetLength() < 1e-9)
                return proposedCenter;

            tangent = tangent.Normalize();
            XYZ normal = new XYZ(-tangent.Y, tangent.X, 0);

            Solid bestSolid = null;
            double bestVolume = 0.0;

            Options options = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false
            };

            GeometryElement geometry = wall.get_Geometry(options);
            if (geometry == null)
                return proposedCenter;

            foreach (GeometryObject obj in geometry)
            {
                Solid solid = obj as Solid;
                if (solid != null && solid.Volume > bestVolume)
                {
                    bestSolid = solid;
                    bestVolume = solid.Volume;
                    continue;
                }

                GeometryInstance instance = obj as GeometryInstance;
                if (instance == null)
                    continue;

                GeometryElement instanceGeometry = instance.GetInstanceGeometry();
                if (instanceGeometry == null)
                    continue;

                foreach (GeometryObject nested in instanceGeometry)
                {
                    Solid nestedSolid = nested as Solid;
                    if (nestedSolid != null && nestedSolid.Volume > bestVolume)
                    {
                        bestSolid = nestedSolid;
                        bestVolume = nestedSolid.Volume;
                    }
                }
            }

            if (bestSolid == null || bestSolid.Volume <= 1e-9)
                return proposedCenter;

            XYZ centroid;
            try
            {
                centroid = bestSolid.ComputeCentroid();
            }
            catch
            {
                return proposedCenter;
            }

            double shift = (centroid - proposedCenter).DotProduct(normal);

            // Never allow a suspiciously large adjustment.
            double maxShift = Math.Max(wall.Width, 0.1);
            if (Math.Abs(shift) > maxShift)
                return proposedCenter;

            return proposedCenter + normal * shift;
        }
    }
}
