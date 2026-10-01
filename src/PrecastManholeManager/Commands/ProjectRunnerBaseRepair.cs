using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        private static ProductionManholeResult RepairAndGenerate(UIDocument uidoc, SimpleManholeItem item,
            DiagnosticLogger log, double clearance)
        {
            var doc = uidoc.Document;
            var foundation = Resolve(doc, item);
            using (var group = new TransactionGroup(doc, "HATCO - Repair Base and Openings"))
            {
                group.Start();
                try
                {
                    double drop = ManholeBaseRepairService.Apply(doc, foundation, clearance, log);
                    if (drop <= 0)
                    {
                        group.RollBack();
                        return new ProductionManholeResult(false, true, "SKIPPED: no lower-wall opening failure.");
                    }
                    // Lower walls may expose additional crossings. Never commit a repair
                    // that leaves a newly detected opening less than 100 mm above the base.
                    var repaired = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
                    if (!repaired.Accepted) throw new InvalidOperationException(repaired.Reason);
                    var check = UnifiedOpeningReviewService.Collect(doc, foundation, repaired, log, clearance, 150, 15);
                    double baseTop = UnitUtil.FtToMm(foundation.get_BoundingBox(null).Max.Z);
                    foreach (var row in check.Rows.Where(x => !x.IsVirtual || x.EndpointQualified))
                        if (row.Source.EffectiveOpeningZmm - row.Source.CutHeightMm / 2 - baseTop < 99.5)
                            throw new InvalidOperationException("A newly detected opening needs a deeper base repair; no changes committed.");
                    ExpandRepairedViews(doc, foundation, UnitUtil.MmToFt(drop), log);
                    var result = GenerateProductionManhole(uidoc, item, log, clearance, unattended: true, existingOnly: true);
                    if (!result.Committed || (!result.DimensionsComplete && !result.DimensionsDeferred))
                        throw new InvalidOperationException("Repair rolled back because openings/dimensions are incomplete. " + result.Summary);
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Repair group did not commit.");
                    log.Info("BASE REPAIR COMMITTED Foundation=" + foundation.Id.IntegerValue + " DropMm=" + drop.ToString("0.###"));
                    return new ProductionManholeResult(true, result.DimensionsComplete, "BASE LOWERED " + drop.ToString("0.###") +
                        " mm; gap below lowest opening=100 mm; original pin states restored.\n" + result.Summary)
                        { DimensionsDeferred = result.DimensionsDeferred };
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                    log.Warn("BASE REPAIR ROLLED BACK Foundation=" + foundation.Id.IntegerValue + " Base/walls/pins/openings/dimensions restored.");
                    throw;
                }
            }
        }

        private static void ExpandRepairedViews(Document doc, Element foundation, double drop, DiagnosticLogger log)
        {
            string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D";
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && (v.Name == prefix + "_PLAN" ||
                    Enumerable.Range(1, 4).Any(i => v.Name == prefix + "_OUT_W" + i))).ToList();
            using (var tx = new Transaction(doc, "HATCO - Extend Repaired View Bounds"))
            {
                tx.Start(); TransactionFailureHandling.Configure(tx, log);
                foreach (var view in views)
                {
                    if (view is ViewSection)
                    {
                        using (var manager = view.GetCropRegionShapeManager())
                            if (manager.ShapeSet || manager.NumberOfSplitRegions > 1)
                                throw new InvalidOperationException("Custom/split section crop requires manual repair: " + view.Name);
                        if (Math.Abs(view.UpDirection.Z - 1) > 1e-6)
                            throw new InvalidOperationException("Repair requires vertical sections.");
                        var crop = view.CropBox;
                        double newBottom = crop.Min.Y - drop;
                        crop.Min = new XYZ(crop.Min.X, newBottom, crop.Min.Z);
                        view.CropBox = crop;
                        doc.Regenerate();
                        if (Math.Abs(view.CropBox.Min.Y - newBottom) > UnitUtil.MmToFt(1))
                            throw new InvalidOperationException("Section crop is controlled and could not be extended: " + view.Name);
                    }
                    else if (view is ViewPlan plan)
                    {
                        using (var range = plan.GetViewRange())
                        {
                            var planes = new[] { PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane };
                            foreach (var plane in planes)
                            {
                                if (range.GetLevelId(plane) != plan.GenLevel.Id)
                                    throw new InvalidOperationException("Repair requires plan bottom/depth relative to its associated level.");
                                range.SetOffset(plane, range.GetOffset(plane) - drop);
                            }
                            plan.SetViewRange(range);
                            doc.Regenerate();
                            using (var check = plan.GetViewRange())
                                foreach (var plane in planes)
                                    if (Math.Abs(check.GetOffset(plane) - range.GetOffset(plane)) > UnitUtil.MmToFt(1))
                                        throw new InvalidOperationException("Plan view range could not be extended.");
                        }
                    }
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit rejected repaired view bounds.");
            }
        }
    }
}
