using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ManholeBaseRepairService
    {
        // Caller owns an outer transaction group including the subsequent opening pass.
        // Return zero when there is no lower-wall fit failure to repair.
        internal static double Apply(Document doc, Element foundation, double clearance, DiagnosticLogger log)
        {
            var footprint = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
            if (!footprint.Accepted) throw new InvalidOperationException(footprint.Reason);
            var review = UnifiedOpeningReviewService.Collect(doc, foundation, footprint, log, clearance, 150, 15);
            var plan = CleanSyncPlanService.Build(doc, foundation.Id.IntegerValue, footprint, review, log);
            var blockers = ProductionPreflightService.PhysicalBlockers(plan);
            if (blockers.Count > 0) throw new InvalidOperationException(string.Join("; ", blockers));
            var rows = review.Rows.Where(x => !x.IsVirtual || x.EndpointQualified).ToList();
            if (rows.Count == 0) return 0;
            bool needsRepair = false;
            double lowest = double.PositiveInfinity;
            foreach (var row in rows)
            {
                var opening = row.Source;
                var wall = doc.GetElement(new ElementId(opening.HostWallId)) as Wall;
                var box = wall?.get_BoundingBox(null);
                if (box == null) throw new InvalidOperationException("Cannot verify target wall bounds.");
                double bottom = opening.EffectiveOpeningZmm - opening.CutHeightMm / 2;
                double top = opening.EffectiveOpeningZmm + opening.CutHeightMm / 2;
                if (opening.CutHeightMm <= 0 || opening.CutWidthMm <= 0 || double.IsNaN(opening.CutWidthMm) ||
                    double.IsInfinity(opening.CutWidthMm) || double.IsNaN(bottom) || double.IsInfinity(bottom))
                    throw new InvalidOperationException("Cannot repair an unresolved opening size.");
                lowest = Math.Min(lowest, bottom);
                string reason;
                if (OpeningFitValidationService.TryValidate(doc, opening, out reason)) continue;
                if (!reason.StartsWith("Opening extends beyond wall vertical limits.", StringComparison.Ordinal) ||
                    !IsLowerFailure(bottom, top, UnitUtil.FtToMm(box.Min.Z), UnitUtil.FtToMm(box.Max.Z)))
                    throw new InvalidOperationException("Base lowering cannot resolve " + row.Wall + ": " + reason);
                needsRepair = true;
            }
            if (!needsRepair) return 0;
            var original = foundation.get_BoundingBox(null);
            double dropMm = RequiredDrop(UnitUtil.FtToMm(original.Max.Z), lowest);
            if (dropMm <= 0 || double.IsInfinity(dropMm))
                throw new InvalidOperationException("Lower-wall failure is not repairable by lowering this base.");
            double drop = UnitUtil.MmToFt(dropMm);
            double targetTop = original.Max.Z - drop;
            var elements = footprint.Walls.Cast<Element>().Concat(new[] { foundation }).ToList();
            var owned = new HashSet<int>(elements.Select(x => x.Id.IntegerValue));
            var pinned = elements.ToDictionary(x => x.Id.IntegerValue, x => x.Pinned);
            var oldWallBoxes = footprint.Walls.ToDictionary(x => x.Id.IntegerValue, x => x.get_BoundingBox(null));
            foreach (var element in elements)
            {
                if (element.GroupId != ElementId.InvalidElementId)
                    throw new InvalidOperationException("Repair cannot modify a grouped base/wall: " + element.Id.IntegerValue);
                if (JoinGeometryUtils.GetJoinedElements(doc, element).Any(x => !owned.Contains(x.IntegerValue)))
                    throw new InvalidOperationException("Repair requires review of joins to elements outside this manhole: " + element.Id.IntegerValue);
            }
            foreach (var wall in footprint.Walls)
            {
                if (wall.WallType.Kind != WallKind.Basic ||
                    wall.get_Parameter(BuiltInParameter.WALL_BOTTOM_IS_ATTACHED)?.AsInteger() == 1 ||
                    wall.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.AsInteger() == 1)
                    throw new InvalidOperationException("Repair requires unattached basic walls: " + wall.Id.IntegerValue);
            }
            log.Info("BASE REPAIR PLAN Foundation=" + foundation.Id.IntegerValue + " DropMm=" + dropMm.ToString("0.###") +
                " OldTopMm=" + UnitUtil.FtToMm(original.Max.Z).ToString("0.###") +
                " NewTopMm=" + UnitUtil.FtToMm(targetTop).ToString("0.###") + " OpeningToBaseMm=100");
            using (var tx = new Transaction(doc, "HATCO - Lower Manhole Base and Extend Walls"))
            {
                tx.Start(); TransactionFailureHandling.Configure(tx, log);
                foreach (var element in elements) if (element.Pinned) element.Pinned = false;
                // Record parameter heights before moving a joined foundation can affect wall geometry.
                var wallSettings = footprint.Walls.Select(w => new {
                    Wall = w,
                    Base = ((Level)doc.GetElement(w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId())).ProjectElevation +
                        w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble(),
                    Height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble(),
                    TopOffset = w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble(),
                    Unconnected = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId() == ElementId.InvalidElementId
                }).ToList();
                ElementTransformUtils.MoveElement(doc, foundation.Id, new XYZ(0, 0, -drop));
                foreach (var setting in wallSettings)
                {
                    var wall = setting.Wall;
                    var level = (Level)doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId());
                    // Geometry uses project coordinates; Level.Elevation can instead
                    // report a survey/shared datum chosen in the level type.
                    double desiredOffset = BaseOffset(targetTop, level.ProjectElevation);
                    log.Info("BASE REPAIR WALL SETTINGS Wall=" + wall.Id.IntegerValue +
                        " LevelElevationMm=" + UnitUtil.FtToMm(level.Elevation).ToString("0.###") +
                        " ProjectElevationMm=" + UnitUtil.FtToMm(level.ProjectElevation).ToString("0.###") +
                        " OldBaseMm=" + UnitUtil.FtToMm(setting.Base).ToString("0.###") +
                        " TargetBaseMm=" + UnitUtil.FtToMm(targetTop).ToString("0.###") +
                        " NewOffsetMm=" + UnitUtil.FtToMm(desiredOffset).ToString("0.###") +
                        " OldHeightMm=" + UnitUtil.FtToMm(setting.Height).ToString("0.###") +
                        " Unconnected=" + setting.Unconnected);
                    Set(wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET), desiredOffset);
                    if (setting.Unconnected)
                        Set(wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM), setting.Height + setting.Base - targetTop);
                    else Set(wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET), setting.TopOffset);
                }
                doc.Regenerate();
                double tolerance = UnitUtil.MmToFt(1);
                var moved = foundation.get_BoundingBox(null);
                if (!SameXY(original, moved, tolerance) || Math.Abs(moved.Min.Z - (original.Min.Z - drop)) > tolerance ||
                    Math.Abs(moved.Max.Z - targetTop) > tolerance)
                    throw new InvalidOperationException("Base did not translate vertically with unchanged thickness/footprint.");
                foreach (var wall in footprint.Walls)
                {
                    var after = wall.get_BoundingBox(null);
                    var before = oldWallBoxes[wall.Id.IntegerValue];
                    string bounds = "Wall=" + wall.Id.IntegerValue + " Before=" + Bounds(before) +
                        " After=" + Bounds(after) + " TargetBottomMm=" + UnitUtil.FtToMm(targetTop).ToString("0.###") +
                        " BaseBottomMm=" + UnitUtil.FtToMm(moved.Min.Z).ToString("0.###") +
                        " XYUnchanged=" + SameXY(before, after, tolerance);
                    log.Info("BASE REPAIR WALL VALIDATION " + bounds);
                    if (!SameXY(before, after, tolerance) || Math.Abs(after.Max.Z - before.Max.Z) > tolerance ||
                        after.Min.Z > targetTop + tolerance || after.Min.Z < moved.Min.Z - tolerance)
                        throw new InvalidOperationException("Could not preserve wall top/footprint and extend bottom to the lowered base. " + bounds);
                }
                foreach (var element in elements) element.Pinned = pinned[element.Id.IntegerValue];
                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit rejected base repair; changes will be rolled back.");
            }
            log.Info("BASE REPAIR GEOMETRY STAGED; original pin states restored. Waiting for opening/dimension validation.");
            return dropMm;
        }

        private static string Bounds(BoundingBoxXYZ box) => box == null ? "NULL" :
            "[" + string.Join(",", new[] { box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z }
                .Select(x => UnitUtil.FtToMm(x).ToString("0.###"))) + "]mm";

        internal static double BaseOffset(double targetProjectZ, double levelProjectZ) => targetProjectZ - levelProjectZ;

        private static bool SameXY(BoundingBoxXYZ a, BoundingBoxXYZ b, double tolerance) => b != null &&
            Math.Abs(a.Min.X - b.Min.X) <= tolerance && Math.Abs(a.Min.Y - b.Min.Y) <= tolerance &&
            Math.Abs(a.Max.X - b.Max.X) <= tolerance && Math.Abs(a.Max.Y - b.Max.Y) <= tolerance;

        private static void Set(Parameter parameter, double value)
        {
            if (parameter != null && Math.Abs(parameter.AsDouble() - value) < 1e-9) return;
            if (parameter == null || parameter.IsReadOnly || !parameter.Set(value))
                throw new InvalidOperationException("A required wall constraint cannot be changed.");
        }

        internal static bool IsLowerFailure(double bottom, double top, double wallBottom, double wallTop) =>
            bottom < wallBottom + 5 && top <= wallTop - 5;
        internal static double RequiredDrop(double baseTop, double lowestOpeningBottom) =>
            Math.Max(0, baseTop - lowestOpeningBottom + 100);
    }
}
