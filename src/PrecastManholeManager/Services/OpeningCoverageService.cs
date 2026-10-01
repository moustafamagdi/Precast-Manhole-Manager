using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    // Read actual native opening boundaries once per manhole. Stored dimensions
    // and source keys are not evidence that the physical cut still fits.
    internal static class OpeningCoverageService
    {
        internal static List<PenetrationRecord> Read(Document doc, IEnumerable<int> wallIds, DiagnosticLogger log)
        {
            var walls = new HashSet<int>(wallIds);
            var cuts = new List<PenetrationRecord>();
            foreach (Opening opening in new FilteredElementCollector(doc).OfClass(typeof(Opening)))
            {
                int wallId = opening.Host?.Id.IntegerValue ?? -1;
                if (!walls.Contains(wallId)) continue;
                try
                {
                    if (!opening.IsRectBoundary) continue;
                    var axis = ((opening.Host as Wall)?.Location as LocationCurve)?.Curve as Line;
                    var corners = opening.BoundaryRect;
                    if (axis == null || corners.Count != 2) continue;
                    var center = (corners[0] + corners[1]) / 2;
                    var delta = corners[1] - corners[0];
                    cuts.Add(new PenetrationRecord {
                        HostWallId = wallId, ExistingOpeningId = opening.Id.IntegerValue,
                        Xmm = UnitUtil.FtToMm(center.X), Ymm = UnitUtil.FtToMm(center.Y), Zmm = UnitUtil.FtToMm(center.Z),
                        CutWidthOverrideMm = UnitUtil.FtToMm(Math.Abs(delta.DotProduct(axis.Direction))),
                        CutHeightOverrideMm = UnitUtil.FtToMm(Math.Abs(delta.Z))
                    });
                }
                catch (Exception ex) { log.Warn("OPENING COVERAGE UNVERIFIED Id=" + opening.Id.IntegerValue + ": " + ex.Message); }
            }
            return cuts;
        }

        internal static bool Covers(IEnumerable<PenetrationRecord> cuts, PenetrationRecord required, double dx, double dy)
        {
            return cuts.Any(c => c.CutWidthMm > 0 && c.CutHeightMm > 0 &&
                !double.IsInfinity(c.CutWidthMm) && !double.IsInfinity(c.CutHeightMm) &&
                CompoundOpeningService.Contains(c, required, dx, dy));
        }
    }
}
