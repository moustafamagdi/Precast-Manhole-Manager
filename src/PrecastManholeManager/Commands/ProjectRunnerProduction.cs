using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;
using Hatco.PrecastManholeManager.Services;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        private static ProductionManholeResult GenerateProductionManhole(UIDocument uidoc,
            SimpleManholeItem selected, DiagnosticLogger log, double clearanceMm, bool unattended = false,
            bool existingOnly = false)
        {
            Document doc = uidoc.Document;
            Element foundation = Resolve(doc, selected);
            if (doc.IsReadOnly || doc.IsLinked)
                throw new InvalidOperationException(
                    "Production requires an editable host RVT.");
            if (!unattended && ManholeReviewRegistry.Load(doc).Any(x =>
                x.FoundationUniqueId == foundation.UniqueId &&
                x.Status == "OPEN"))
                throw new InvalidOperationException(
                    "This manhole has an OPEN review issue. Resolve it " +
                    "before making production cuts.");

            string id = (ManholeIdentityStore.Read(foundation) ?? "").Trim();
            if (id.Length == 0 && !existingOnly)
                throw new InvalidOperationException(
                    "Run Assign Internal IDs first. " +
                    "Internal MH-### names are required before production.");

            VirtualFoundationResult footprint =
                new VirtualFoundationRecoveryService(doc, log)
                    .Analyze(foundation);
            if (!footprint.Accepted)
                throw new InvalidOperationException(
                    "Foundation geometry not approved: " + footprint.Reason);

            if (double.IsNaN(clearanceMm) || double.IsInfinity(clearanceMm) || clearanceMm < 0)
                throw new InvalidOperationException("Clearance must be a finite non-negative value.");
            var slot = BatchSheetLayoutService.Find(doc, foundation);
            ViewSheet existingSheet = slot?.Sheet ?? FirstProductionSheetService.FindExisting(doc, foundation);
            if (id.Length == 0) id = "Foundation " + foundation.Id.IntegerValue;
            string viewPrefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D";
            var availableViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate).Select(v => v.Name).ToHashSet();
            bool dimensionViewsReady = availableViews.Contains(viewPrefix + "_PLAN") &&
                Enumerable.Range(1, 4).All(i => availableViews.Contains(viewPrefix + "_OUT_W" + i));
            // Preserve placed views and layout; only the opening table is refreshed on reruns.
            string prefix = "MH_" + foundation.Id.IntegerValue +
                "_PROD_2D";
            HashSet<int> onSheet = new HashSet<int>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport)).Cast<Viewport>()
                    .Select(x => x.ViewId.IntegerValue));
            bool placed = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Any(x => !x.IsTemplate &&
                    (x.Name == prefix + "_PLAN" ||
                     x.Name == prefix + "_OUT_W1" ||
                     x.Name == prefix + "_OUT_W2" ||
                     x.Name == prefix + "_OUT_W3" ||
                     x.Name == prefix + "_OUT_W4") &&
                     onSheet.Contains(x.Id.IntegerValue));
            if (!existingOnly && placed && existingSheet == null)
                throw new InvalidOperationException(
                    "The production Plan/Sections already appear on a " +
                    "sheet. Existing manual layouts are protected.");

            log.WriteHeader("FIRST PRODUCTION MANHOLE - READ ONLY PREFLIGHT");
            // Clearance is selected by the operator, in millimeters per side.
            UnifiedOpeningReviewResult review =
                UnifiedOpeningReviewService.Collect(doc, foundation,
                    footprint, log, clearanceMm, 150, 15);
            string csv = UnifiedOpeningReviewService.ExportCsv(review);
            CleanSyncPlan plan = CleanSyncPlanService.Build(doc,
                foundation.Id.IntegerValue, footprint, review, log);

            var blockers = ProductionPreflightService.PhysicalBlockers(plan);
            log.Info("PRODUCTION LINK SCOPE: operator-selected loaded links. " +
                "Unloaded links skipped=" + plan.UnavailableLinks);
            if (blockers.Count > 0)
            {
                log.WriteHeader("PRODUCTION PRECHECK: EXACT BLOCKERS");
                foreach (string blocker in blockers)
                    log.Warn("PRODUCTION BLOCKED Foundation=" +
                        foundation.Id.IntegerValue + " " + blocker);
                if (unattended) throw new InvalidOperationException(string.Join("; ", blockers));
                TaskDialog.Show("Production Precheck - " + id,
                    "No geometry was modified.\n\n" +
                    string.Join("\n\n", blockers) +
                    "\n\nOpening review CSV:\n" + csv +
                    "\n\nDetailed TXT log:\n" + log.LogPath);
                return new ProductionManholeResult(false, false, "REVIEW: " + string.Join("; ", blockers));
            }

            List<UnifiedOpeningReviewRow> actual = review.Rows
                .Where(x => !x.IsVirtual || x.EndpointQualified).ToList();
            if (actual.Count == 0)
                throw new InvalidOperationException(
                    "No confirmed crossing or validated end connector within 150 mm of the wall. Review: " + csv);
            if (actual.Any(x => x.Status != "ACTUAL FIT PREVIEW" &&
                 !(x.Source.ExistingOpeningStatus == "MANAGED" &&
                   x.Source.CutWidthMm > 0 &&
                   x.Source.CutHeightMm > 0)))
                throw new InvalidOperationException(
                    "At least one actual opening requires manual review. " +
                    "All openings must pass validation. See " + csv);
            foreach (var row in actual)
            {
                string reason;
                if (!OpeningFitValidationService.TryValidate(
                    doc, row.Source, out reason))
                    throw new InvalidOperationException(
                        "Opening fit failed W" + row.Source.WallNumber +
                        ": " + reason + ". Review " + csv);
            }
            // A pair of individually valid openings can still overlap.
            // Refuse uncertain compound cuts in this first milestone.
            for (int i = 0; i < actual.Count; i++)
            for (int j = i + 1; j < actual.Count; j++)
            {
                var a = actual[i].Source;
                var c = actual[j].Source;
                if (a.HostWallId != c.HostWallId) continue;
                Wall wall = doc.GetElement(new ElementId(a.HostWallId))
                    as Wall;
                Line line = (wall?.Location as LocationCurve)?.Curve
                    as Line;
                if (line == null)
                    throw new InvalidOperationException(
                        "Cannot validate spacing between wall openings.");
                XYZ direction = line.Direction;
                XYZ p1 = new XYZ(a.EffectiveOpeningXmm,
                    a.EffectiveOpeningYmm, a.EffectiveOpeningZmm);
                XYZ p2 = new XYZ(c.EffectiveOpeningXmm,
                    c.EffectiveOpeningYmm, c.EffectiveOpeningZmm);
                double along = Math.Abs((p1 - p2).DotProduct(direction));
                double vertical = Math.Abs(
                    a.EffectiveOpeningZmm - c.EffectiveOpeningZmm);
                if (along <
                        (a.CutWidthMm + c.CutWidthMm) * 0.5 + 5 &&
                    vertical <
                        (a.CutHeightMm + c.CutHeightMm) * 0.5 + 5)
                    throw new InvalidOperationException(
                        "Two proposed openings overlap on W" +
                        a.WallNumber + ". Compound cuts need " +
                        "manual engineering review: " +
                        a.LinkedElementId + " / " +
                        c.LinkedElementId + ". See " + csv);
            }
            if (actual.Count > 8)
                throw new InvalidOperationException(
                    "First production milestone supports up to eight " +
                    "actual openings on one manhole so all setout rows " +
                    "remain readable on the sheet. See " + csv);

            // Only connector-verified, unambiguous endpoint projections enter production.
            plan.ProposedRows.RemoveAll(x => x.IsVirtual && !x.EndpointQualified);
            int deferred = review.Rows.Count(x => x.IsVirtual && !x.EndpointQualified);
            if (actual.Select(x => x.Source.SourceKey).Distinct().Count() !=
                actual.Count)
                throw new InvalidOperationException(
                    "Duplicate actual crossing source keys. Review " + csv);

            log.Info("PRODUCTION PREFLIGHT Name=" + id +
                " Actual=" + actual.Count +
                " ClearancePerSideMm=" + clearanceMm +
                " EndpointQualified=" + actual.Count(x => x.IsVirtual) + " VirtualDeferred=" + deferred +
                " ReviewCsv=" + csv);
            if (!unattended)
            {
                var confirm = new TaskDialog("Approve first production cuts");
                confirm.MainInstruction = id + " | " + actual.Count +
                    " confirmed crossing / endpoint opening(s)";
                string proposed = string.Join("\n",
                    actual.OrderBy(x => x.Source.WallNumber)
                        .Select(x => "W" + x.Source.WallNumber +
                            " / Source " + x.Source.LinkedElementId +
                            " / " + x.OpeningSize +
                            (x.Source.ExistingOpeningStatus == "MANAGED"
                                ? " / UPDATE EXISTING" : " / CREATE")));
                confirm.MainContent =
                    "APPROVED OPENING CANDIDATES:\n" + proposed +
                    "\n\n" + clearanceMm.ToString("0.###") +
                    " mm clearance per side. These are REAL Revit cuts " +
                    "to this one manhole's four walls. " +
                    (existingSheet == null ? "Create a new 1:25 Plan + 4 Sections sheet." :
                        "Update existing openings and the sheet table; preserve view layout.") + "\n\n" +
                    "Unverified endpoint candidates deferred: " + deferred +
                    "." +
                    " Existing tool openings may be resized. Manual cuts/profiles/void cutters are preserved." +
                    "\nFailed joins within this manhole walls/base may be detached if required by Revit." +
                    "\nRead-only audit CSV: " + csv +
                    "\n\nContinue only on a saved test RVT copy.";
                confirm.CommonButtons = TaskDialogCommonButtons.Yes |
                    TaskDialogCommonButtons.No;
                confirm.DefaultButton = TaskDialogResult.No;
                if (confirm.Show() != TaskDialogResult.Yes)
                {
                    log.Info("PRODUCTION CANCELLED by operator. No model edits.");
                    return new ProductionManholeResult(false, false, "CANCELLED");
                }
            }

            CleanSyncApplyResult applied;
            ViewSheet newSheet = existingSheet;
            View3D production3D = null;
            using (var group = new TransactionGroup(doc,
                "HATCO - One Manhole Openings and Drawing"))
            {
                group.Start();
                try
                {
                    // Old face references may disappear when native openings are resized.
                    // Remove only our annotations inside the same rollback group as the cuts.
                    using (var dimTx = new Transaction(doc, "HATCO - Refresh Opening References"))
                    {
                        dimTx.Start();
                        TransactionFailureHandling.Configure(dimTx, log);
                        OpeningDimensionService.RemoveOwned(doc, foundation);
                        if (dimTx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Could not prepare opening dimensions for update.");
                    }
                    applied = CleanSyncAtomicService.Apply(doc, plan,
                        new CleanSyncApplyOptions
                        {
                            ResetEditedProfiles = false,
                            RemoveManualNative = false,
                            RemoveVoidCutRelations = false,
                            DeleteIsolatedInPlaceCutters = false,
                            IncludeStraightVirtual = false,
                            IncludeValidatedEndpoints = true,
                            // Required sources are the links loaded by the operator.
                            RequiredLinksVerified = true,
                            ResolveManholeJoinFailures = true
                        }, log);
                    if (!applied.Committed)
                        throw new InvalidOperationException(
                            "Native opening transaction failed: " +
                            applied.Error);
                    if (existingOnly && slot != null)
                    using (var noteTx = new Transaction(doc, "HATCO - Update Existing Opening Note"))
                    {
                        noteTx.Start();
                        TransactionFailureHandling.Configure(noteTx, log);
                        BatchSheetLayoutService.SetStatus(doc, foundation, slot,
                            "OPENINGS: " + string.Join("; ", actual.Select(r => "W" + r.Source.WallNumber + " " + r.OpeningSize)));
                        if (noteTx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Could not update the existing opening note.");
                    }
                    if (!existingOnly)
                    using (var tx = new Transaction(doc,
                        "HATCO - First Production Draft and Sheet"))
                    {
                        tx.Start();
                        TransactionFailureHandling.Configure(tx, log);
                        try
                        {
                            if (slot != null)
                            {
                                if (unattended && BatchSheetLayoutService.HasPreparedViews(doc, foundation, slot))
                                {
                                    log.Info("BATCH REUSE PREPARED VIEWS Foundation=" + foundation.Id.IntegerValue);
                                    BatchSheetLayoutService.SetStatus(doc, foundation, slot,
                                        "OPENINGS: " + string.Join("; ", actual.Select(r => "W" + r.Source.WallNumber + " " + r.OpeningSize)));
                                }
                                else
                                {
                                    DraftSheetResult views = DraftManholeSheetService.Generate(
                                        doc, foundation, footprint, log, forProduction: true);
                                    BatchSheetLayoutService.Place(doc, foundation, slot, views.Views, actual, log);
                                }
                                newSheet = slot.Sheet;
                            }
                            else if (existingSheet != null)
                            {
                                FirstProductionSheetService.Refresh(doc, foundation,
                                    existingSheet, actual, log);
                                newSheet = existingSheet;
                            }
                            else
                            {
                                DraftSheetResult views = DraftManholeSheetService.Generate(
                                    doc, foundation, footprint, log, forProduction: true);
                                newSheet = FirstProductionSheetService.Build(
                                    doc, foundation, views, actual, log);
                            }
                            production3D = ManholeReviewViewService
                                .CreateProduction(doc, foundation,
                                    footprint, log);
                            if (tx.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException(
                                    "Could not commit first production sheet.");
                        }
                        catch
                        {
                            if (tx.GetStatus() == TransactionStatus.Started)
                                tx.RollBack();
                            throw;
                        }
                    }
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Production transaction group did not commit.");
                }
                catch (Exception ex)
                {
                    if (group.GetStatus() == TransactionStatus.Started)
                        group.RollBack();
                    log.Error("FIRST PRODUCTION FAILED: all cuts, " +
                        "views and sheet rolled back.", ex);
                    throw;
                }
            }
            string dimensionStatus;
            bool dimensionsComplete = false;
            bool layoutNeedsReview = false;
            bool dimensionsDeferred = existingOnly && !dimensionViewsReady;
            try { dimensionStatus = dimensionsDeferred ? "Dimensions deferred: prepare PLAN and W1-W4 when required." :
                OpeningDimensionService.Generate(doc, foundation, log, ok => dimensionsComplete = ok); }
            catch (Exception ex)
            {
                log.Error("Dimension stage needs review; openings and sheet remain committed.", ex);
                dimensionStatus = "Dimensions need review: " + ex.Message;
            }
            if (slot != null && !existingOnly)
            {
                try
                {
                    using (var tx = new Transaction(doc, "HATCO - Fit Batch Row After Dimensions"))
                    {
                        tx.Start(); TransactionFailureHandling.Configure(tx, log);
                        var layoutWarnings = BatchSheetLayoutService.Arrange(doc, foundation, slot, log);
                        if (layoutWarnings.Count > 0)
                        {
                            layoutNeedsReview = true;
                            dimensionStatus += "\nLAYOUT REVIEW (placed for manual adjustment): " +
                                string.Join("; ", layoutWarnings);
                            foreach (string warning in layoutWarnings)
                                log.Warn("BATCH LAYOUT REVIEW: " + warning);
                        }
                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Row layout transaction rejected.");
                    }
                }
                catch (Exception ex)
                {
                    if (unattended) throw new InvalidOperationException("Reserved row layout failed: " + ex.Message, ex);
                    layoutNeedsReview = true;
                    dimensionStatus += "\nLAYOUT REVIEW: " + ex.Message;
                }
            }
            string summary = "COMMITTED: " + id + " | New=" + applied.NewOpenings +
                " Updated=" + applied.ManagedUpdated + " Unchanged=" + applied.ManagedUnchanged +
                " | Sheet=" + (newSheet?.SheetNumber ?? "NONE") + (dimensionsDeferred ? " | DIMENSIONS DEFERRED" : dimensionsComplete ? "" : " | DIMENSION REVIEW") + "\n" + dimensionStatus;
            var result = new ProductionManholeResult(true, dimensionsComplete, summary)
            { LayoutNeedsReview = layoutNeedsReview, DimensionsDeferred = dimensionsDeferred };
            if (unattended || existingOnly) return result;
            uidoc.RequestViewChange(newSheet);
            TaskDialog.Show("First Production Manhole",
                "COMMITTED: " + id +
                "\nScope: loaded linked models" +
                "\nNew native openings: " + applied.NewOpenings +
                "\nManaged unchanged: " + applied.ManagedUnchanged +
                "\nManaged updated: " + applied.ManagedUpdated +
                "\nFailed local joins resolved: " + applied.JoinFailuresResolved +
                "\nUnverified endpoints deferred: " + deferred +
                "\n3D: " + production3D.Name + " (MH_3D)" +
                "\nSheet: " + newSheet.SheetNumber +
                " / " + newSheet.Name +
                "\n" + dimensionStatus +
                "\nPreliminary opening setout is shown on the sheet. " +
                "Verify dimensions and elevations before issuing." +
                "\nReview CSV: " + csv +
                "\nSave the RVT to retain the output.");
            return result;
        }

    }
}
