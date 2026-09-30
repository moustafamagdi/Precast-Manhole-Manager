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
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ProjectRunnerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData input,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = input.Application.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (doc == null) return Result.Failed;
            // Capture the user-formatted sample sheet at launch; modal
            // dialogs and view switches must not replace this reference.
            ViewSheet referenceSheet = uiDoc.ActiveView as ViewSheet;
            using (var log = new DiagnosticLogger())
            {
                try
                {
                    // The dialog is deliberately short-lived/modal. Each
                    // requested operation runs while this Revit IExternalCommand
                    // is active, then the SAME single screen is reopened.
                    List<SimpleManholeItem> rows =
                        SimpleProjectScanService.LoadFast(doc);
                    while (true)
                    {
                        var window = new SimpleProjectWindow(rows);
                        if (window.ShowDialog() != true ||
                            window.Action == ProjectAction.Close)
                            return Result.Succeeded;
                        try
                        {
                            if (window.Action == ProjectAction.Scan)
                            {
                                rows = SimpleProjectScanService.Scan(doc, log);
                                List<ManholeReviewIssue> issues =
                                    ManholeReviewRegistry.Load(doc);
                                ManholeReviewRegistry.ExportReadableCsv(
                                    doc, issues);
                                TaskDialog.Show("Precast Manhole Manager",
                                    "Scan complete. Found " + rows.Count +
                                    " manholes. Review required: " +
                                    rows.Count(x => x.State == "REVIEW") +
                                    ".\nNo openings were changed.");
                            }
                            else if (window.Action == ProjectAction.NumberAll)
                                AssignAllManholeNames(doc, log);
                            else if (window.Action == ProjectAction.ReviewOne)
                                InspectSelected(doc, window.SelectedManhole,
                                    log);
                            else if (window.Action == ProjectAction.Make3D)
                                MakeReview3D(uiDoc, window.SelectedManhole,
                                    log);
                            else if (window.Action == ProjectAction.DraftSheet)
                                GenerateDraftSheet(uiDoc,
                                    window.SelectedManhole, log);
                            else if (window.Action == ProjectAction.ProductionOne)
                                GenerateProductionManhole(uiDoc,
                                    window.SelectedManhole, log);
                            else if (window.Action == ProjectAction.SixRowLayoutSheet)
                                GenerateSixRowLayoutSheet(uiDoc,
                                    window.SheetCandidates, referenceSheet, log);
                            else if (window.Action == ProjectAction.ExportExcel)
                                Export(doc, log);

                            // Keep the latest review state and view reference
                            // even after the operator closes another dialog.
                            rows = SimpleProjectScanService.LoadFast(doc);
                        }
                        catch (Exception ex)
                        {
                            log.Error("Project runner action failed.", ex);
                            TaskDialog.Show("Precast Manhole Manager",
                                "Action stopped. Other manholes are unaffected.\n" +
                                ex.Message + "\n\nLog: " + log.LogPath);
                            rows = SimpleProjectScanService.LoadFast(doc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Project runner failed.", ex);
                    message = ex.Message;
                    TaskDialog.Show("Precast Manhole Manager",
                        ex.Message + "\nLog: " + log.LogPath);
                    return Result.Failed;
                }
            }
        }

        private static Element Resolve(Document doc,
            SimpleManholeItem row)
        {
            if (row == null)
                throw new InvalidOperationException(
                    "Select a manhole first.");
            Element foundation = doc.GetElement(
                new ElementId(row.FoundationId));
            if (foundation == null ||
                foundation.UniqueId != row.UniqueId)
                throw new InvalidOperationException(
                    "Foundation identity changed. Rescan the project.");
            return foundation;
        }

        private static void AssignAllManholeNames(Document doc,
            DiagnosticLogger log)
        {
            log.WriteHeader("PROJECT-WIDE MANHOLE NAME PREVIEW");
            ManholeNumberingPlan preview =
                ManholeNumberingService.Preview(doc);
            string csv = ManholeNumberingService.ExportPreview(preview);
            if (preview.Rows.Count == 0)
            {
                TaskDialog.Show("Assign Internal IDs",
                    "No eligible precast foundations were found. " +
                    "No project elements changed.\nPreview: " + csv);
                return;
            }
            if (preview.Errors.Count != 0)
            {
                TaskDialog.Show("Duplicate Internal IDs",
                    "No IDs changed. " + preview.Errors.Count +
                    " duplicate INTERNAL ID(s).\n" +
                    string.Join("\n", preview.Errors.Take(5)) +
                    "\n\nFull audit CSV: " + csv +
                    "\nCorrect duplicate internal names " +
                    "in Revit and run again.");
                log.Warn("MANHOLE NUMBERING CANCELLED PreflightErrors=" +
                    preview.Errors.Count + " CSV=" + csv);
                return;
            }
            string examples = string.Join("\n", preview.Rows
                .Where(x => x.NewNumber)
                .Take(6)
                .Select(x => x.FoundationId + " => " + x.ProposedName));
            if (examples.Length == 0) examples =
                "All manholes already have names.";
            var ask = new TaskDialog("Assign Internal IDs")
            {
                MainInstruction = "Assign stable names to " +
                    preview.Rows.Count + " precast manhole(s)?",
                MainContent = "Existing names preserved: " +
                    preview.ExistingPreserved +
                    "\nNew IDs (MH-001, MH-002, ...): " +
                    preview.NewlyNumbered +
                    "\nProject Mark changes: NONE" +
                    "\n\nExamples:\n" + examples +
                    "\n\nNumbers are generated in initial " +
                    "ElementId order, NOT consultant-approved site " +
                    "designations. Once committed, they remain fixed " +
                    "across reruns and are saved in foundation storage " +
                    "and internal project data.\n\nAudit: " + csv +
                    "\n\nCheck the CSV and confirm to proceed.",
                CommonButtons = TaskDialogCommonButtons.Yes |
                    TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (ask.Show() != TaskDialogResult.Yes)
            {
                log.Info("MANHOLE NUMBERING cancelled by user. " +
                    "Preview CSV=" + csv);
                return;
            }
            ManholeNumberingService.Apply(doc, preview, log);
            TaskDialog.Show("Assign Manhole Names",
                "Saved to the RVT: " + preview.Rows.Count +
                " manholes.\nGenerated: " +
                preview.NewlyNumbered + "\nPreserved: " +
                preview.ExistingPreserved + "\nNative Mark untouched: " +
                "no changes\n\nPreview CSV: " + csv +
                "\nSave/Synchronize the RVT to retain the names.");
        }

        private static void InspectSelected(Document doc,
            SimpleManholeItem row, DiagnosticLogger log)
        {
            Element foundation = Resolve(doc, row);
            VirtualFoundationResult footprint =
                new VirtualFoundationRecoveryService(doc, log)
                    .Analyze(foundation);
            if (!footprint.Accepted)
            {
                ManholeReviewRegistry.Upsert(doc, foundation,
                    "Cannot identify four-wall footprint: " +
                    footprint.Reason, null, "GEOMETRY", log);
                TaskDialog.Show("Review Manhole",
                    "Footprint needs review for foundation " +
                    row.FoundationId + ".\n" + footprint.Reason +
                    "\nUse 3D Review to inspect.");
                return;
            }
            // Reuse the full tested scanner and unified read-only review.
            // 50 mm clearance / 150 mm max gap / 15 degree plan angle.
            UnifiedOpeningReviewResult review =
                UnifiedOpeningReviewService.Collect(
                    doc, foundation, footprint, log, 50, 150, 15);
            string csv = UnifiedOpeningReviewService.ExportCsv(review);
            bool needsAttention = review.Rows.Any(x =>
                x.Status == "REVIEW");
            if (needsAttention)
                ManholeReviewRegistry.Upsert(doc, foundation,
                    "Detailed opening check: " +
                    review.Rows.Count(x => x.Status == "REVIEW") +
                    " row(s) require review. " +
                    "See unified review CSV; no cuts were created.",
                    footprint.Walls.Select(w => w.Id.IntegerValue),
                    "OPENINGS REVIEW", log);

            TaskDialog.Show("Review Manhole " + row.FoundationId,
                "Actual penetrations: " + review.ActualCount +
                "\nVirtual candidates: " + review.VirtualCount +
                "\nNeed review: " +
                review.Rows.Count(x => x.Status == "REVIEW") +
                "\n\nReview CSV: " + csv +
                "\n\nNo model changes.");
        }

        private static void MakeReview3D(UIDocument uiDoc,
            SimpleManholeItem row, DiagnosticLogger log)
        {
            Document doc = uiDoc.Document;
            Element foundation = Resolve(doc, row);
            List<ManholeReviewIssue> saved =
                ManholeReviewRegistry.Load(doc);
            ManholeReviewIssue issue = saved.FirstOrDefault(x =>
                x.FoundationUniqueId == foundation.UniqueId);

            // Creating a review view should not automatically mark a
            // healthy manhole as problematic or skip it in Batch All.
            bool temporary = issue == null;
            if (temporary)
                issue = new ManholeReviewIssue
                {
                    FoundationUniqueId = foundation.UniqueId,
                    FoundationId = foundation.Id.IntegerValue,
                    Status = "RESOLVED",
                    Severity = "VIEW ONLY",
                    Reason = "3D view requested"
                };

            VirtualFoundationResult footprint =
                new VirtualFoundationRecoveryService(doc, log)
                    .Analyze(foundation);
            View3D view;
            using (var tx = new Transaction(doc,
                "HATCO - Manhole review 3D " +
                foundation.Id.IntegerValue))
            {
                tx.Start();
                try
                {
                    view = ManholeReviewViewService.CreateOrUpdate(
                        doc, foundation, footprint, issue, 350, log);
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Could not commit the review 3D view.");
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    throw;
                }
            }
            if (temporary) saved.Add(issue);
            ManholeReviewRegistry.Save(doc, saved);
            ManholeReviewRegistry.ExportReadableCsv(doc, saved);
            uiDoc.RequestViewChange(view);
            TaskDialog.Show("Precast Manhole Manager",
                "Created / opened 3D view: " + view.Name +
                "\nSave the RVT to retain it.");
        }

        private static void GenerateDraftSheet(UIDocument uiDoc,
            SimpleManholeItem selected, DiagnosticLogger log)
        {
            Document doc = uiDoc.Document;
            Element foundation = Resolve(doc, selected);
            // Never turn unresolved issues into a fabrication document.
            ManholeReviewIssue existing = ManholeReviewRegistry.Load(doc)
                .FirstOrDefault(x => x.FoundationUniqueId ==
                    foundation.UniqueId && x.Status == "OPEN");
            if (existing != null)
                throw new InvalidOperationException(
                    "Manhole is isolated: " + existing.Reason +
                    ". Select a clean prototype first.");

            VirtualFoundationResult footprint =
                new VirtualFoundationRecoveryService(doc, log)
                    .Analyze(foundation);
            if (!footprint.Accepted)
            {
                ManholeReviewRegistry.Upsert(doc, foundation,
                    "Draft cannot validate footprint: " + footprint.Reason,
                    null, "GEOMETRY", log);
                throw new InvalidOperationException(
                    "Four-wall footprint needs review: " + footprint.Reason);
            }

            OpeningResetAuditResult audit =
                OpeningResetAuditService.Audit(doc, footprint.Walls, log);
            if (audit.RequiresManualReview)
            {
                ManholeReviewRegistry.Upsert(doc, foundation,
                    "Draft blocked by existing wall cuts: " +
                    audit.ProfileEditedWalls + " edited profiles; " +
                    audit.NativeUnmanaged + " manual openings; " +
                    audit.VoidCutRelations + " void cut relations.",
                    footprint.Walls.Select(w => w.Id.IntegerValue),
                    "REQUIRES CLEANUP", log);
                throw new InvalidOperationException(
                    "Selected manhole has old/manual cuts. Choose " +
                    "a cleaner prototype from the project list.");
            }

            // Draft geometry only. Never modify foundations, wall sketches,
            // cutting voids, linked MEP, or managed openings here.
            DraftSheetResult result;
            using (Transaction tx = new Transaction(doc,
                "HATCO - Draft Manhole Plan + Four Elevations"))
            {
                tx.Start();
                try
                {
                    result = DraftManholeSheetService.Generate(
                        doc, foundation, footprint, log);
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit did not commit draft views/sheet.");
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    throw;
                }
            }
            // Open the generated Plan to help the user find the views;
            // Revit API view switching occurs only AFTER committing.
            if (result.Views.Count > 0)
                uiDoc.RequestViewChange(result.Views[0]);
            TaskDialog.Show("First Manhole Prototype",
                result.Message +
                "\n\nThese are preliminary model views, not " +
                "dimensioned fabrication shop drawings." +
                "\nNo wall geometry or openings were changed." +
                "\nSave the RVT to keep the five views.");
        }

        // First production milestone: ONE clean manhole with real,
        // confirmed ACTUAL linked-MEP crossings only. One transaction
        // group makes the physical openings + views + sheet atomic.
        private static void GenerateProductionManhole(UIDocument uidoc,
            SimpleManholeItem selected, DiagnosticLogger log)
        {
            Document doc = uidoc.Document;
            Element foundation = Resolve(doc, selected);
            if (doc.IsReadOnly || doc.IsLinked)
                throw new InvalidOperationException(
                    "Production requires an editable host RVT.");
            if (ManholeReviewRegistry.Load(doc).Any(x =>
                x.FoundationUniqueId == foundation.UniqueId &&
                x.Status == "OPEN"))
                throw new InvalidOperationException(
                    "This manhole has an OPEN review issue. Resolve it " +
                    "before making production cuts.");

            string id = (ManholeIdentityStore.Read(foundation) ?? "").Trim();
            if (id.Length == 0)
                throw new InvalidOperationException(
                    "Run Assign Internal IDs first. " +
                    "Internal MH-### names are required before production.");

            VirtualFoundationResult footprint =
                new VirtualFoundationRecoveryService(doc, log)
                    .Analyze(foundation);
            if (!footprint.Accepted)
                throw new InvalidOperationException(
                    "Foundation geometry not approved: " + footprint.Reason);

            // Explicitly avoid overwriting old manually arranged views.
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
            if (placed)
                throw new InvalidOperationException(
                    "The production Plan/Sections already appear on a " +
                    "sheet. Existing manual layouts are protected.");

            log.WriteHeader("FIRST PRODUCTION MANHOLE - READ ONLY PREFLIGHT");
            // Defaults validated by earlier unified review: 50 mm clearance,
            // 150 mm virtual preview radius and 15-degree limit.
            UnifiedOpeningReviewResult review =
                UnifiedOpeningReviewService.Collect(doc, foundation,
                    footprint, log, 50, 150, 15);
            string csv = UnifiedOpeningReviewService.ExportCsv(review);
            CleanSyncPlan plan = CleanSyncPlanService.Build(doc,
                foundation.Id.IntegerValue, footprint, review, log);

            // Report the EXACT blockers instead of grouping every
            // distinct legacy cut/link issue into "legacy cuts or links".
            // Diagnosis stays read-only; no automatic deletion or
            // blanket assumption that an unloaded link is irrelevant.
            var blockers = new List<string>();
            if (!string.IsNullOrWhiteSpace(plan.BlockReason))
                blockers.Add("Audit: " + plan.BlockReason);
            if (plan.UnavailableLinks > 0)
            {
                string names = string.Join("; ",
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>()
                        .Where(x => x.GetLinkDocument() == null)
                        .Select(x => x.Name + " [Id=" +
                            x.Id.IntegerValue + "]"));
                blockers.Add("Unavailable Revit links (" +
                    plan.UnavailableLinks + "): " + names +
                    ". Load them or explicitly verify their coverage " +
                    "before a production cut.");
            }
            if (plan.ProfileResetCount > 0)
                blockers.Add("Edited wall profiles (" +
                    plan.ProfileResetCount + "): " +
                    string.Join(",", plan.Profiles
                        .Where(x => x.Value == "EDITED PROFILE")
                        .Select(x => x.Key)));
            if (plan.VoidCutCount > 0)
                blockers.Add("Existing unattached void cuts (" +
                    plan.VoidCutCount + "): " +
                    string.Join(",", plan.VoidCutIds
                        .Where(x => x.Value.Count > 0)
                        .Select(x => x.Key + " => " +
                            string.Join("/", x.Value))));
            if (plan.ManualOpeningIds.Count > 0)
                blockers.Add("Existing non-tool native openings (" +
                    plan.ManualOpeningIds.Count + "): " +
                    string.Join(",", plan.ManualOpeningIds));
            if (plan.InPlaceCutterCount > 0)
                blockers.Add("In-place wall cutters (" +
                    plan.InPlaceCutterCount + "): " +
                    string.Join(",", plan.InPlaceCutterWallIds.Keys));
            if (plan.UnsupportedSolidCutWallIds.Count > 0)
                blockers.Add("Unsupported solid-solid cuts on walls: " +
                    string.Join(",", plan.UnsupportedSolidCutWallIds));
            if (blockers.Count > 0)
            {
                log.WriteHeader("PRODUCTION PRECHECK: EXACT BLOCKERS");
                foreach (string blocker in blockers)
                    log.Warn("PRODUCTION BLOCKED Foundation=" +
                        foundation.Id.IntegerValue + " " + blocker);
                TaskDialog.Show("Production Precheck - " + id,
                    "No geometry was modified.\n\n" +
                    string.Join("\n\n", blockers) +
                    "\n\nOpening review CSV:\n" + csv +
                    "\n\nDetailed TXT log:\n" + log.LogPath);
                return;
            }

            List<UnifiedOpeningReviewRow> actual = review.Rows
                .Where(x => !x.IsVirtual).ToList();
            if (actual.Count == 0)
                throw new InvalidOperationException(
                    "No confirmed actual linked-MEP wall penetrations. " +
                    "Production will not cut any wall based on virtual " +
                    "candidates alone. Review: " + csv);
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

            // Never let virtual extensions enter the write plan during
            // the first production rollout; they remain in CSV for review.
            plan.ProposedRows.RemoveAll(x => x.IsVirtual);
            if (actual.Select(x => x.Source.SourceKey).Distinct().Count() !=
                actual.Count)
                throw new InvalidOperationException(
                    "Duplicate actual crossing source keys. Review " + csv);

            log.Info("PRODUCTION PREFLIGHT Name=" + id +
                " Actual=" + actual.Count +
                " VirtualDeferred=" + review.VirtualCount +
                " ReviewCsv=" + csv);
            var confirm = new TaskDialog("Approve first production cuts");
            confirm.MainInstruction = id + " | " + actual.Count +
                " confirmed actual opening(s)";
            string proposed = string.Join("\n",
                actual.OrderBy(x => x.Source.WallNumber)
                    .Select(x => "W" + x.Source.WallNumber +
                        " / Source " + x.Source.LinkedElementId +
                        " / " + x.OpeningSize));
            confirm.MainContent =
                "APPROVED OPENING CANDIDATES:\n" + proposed +
                "\n\n50 mm clearance per side. These are REAL Revit cuts " +
                "to this one manhole's four walls, followed by a new " +
                "1:25 Plan + 4 exterior Sections sheet.\n\n" +
                "Virtual candidates deferred: " + review.VirtualCount +
                ". No old cuts/profiles/void cutters will be removed." +
                "\nRead-only audit CSV: " + csv +
                "\n\nContinue only on a saved test RVT copy.";
            confirm.CommonButtons = TaskDialogCommonButtons.Yes |
                TaskDialogCommonButtons.No;
            confirm.DefaultButton = TaskDialogResult.No;
            if (confirm.Show() != TaskDialogResult.Yes)
            {
                log.Info("PRODUCTION CANCELLED by operator. No model edits.");
                return;
            }

            CleanSyncApplyResult applied;
            ViewSheet newSheet;
            View3D production3D;
            using (var group = new TransactionGroup(doc,
                "HATCO - One Manhole Openings and Drawing"))
            {
                group.Start();
                try
                {
                    applied = CleanSyncAtomicService.Apply(doc, plan,
                        new CleanSyncApplyOptions
                        {
                            ResetEditedProfiles = false,
                            RemoveManualNative = false,
                            RemoveVoidCutRelations = false,
                            DeleteIsolatedInPlaceCutters = false,
                            IncludeStraightVirtual = false,
                            RequiredLinksVerified = false
                        }, log);
                    if (!applied.Committed)
                        throw new InvalidOperationException(
                            "Native opening transaction failed: " +
                            applied.Error);
                    using (var tx = new Transaction(doc,
                        "HATCO - First Production Draft and Sheet"))
                    {
                        tx.Start();
                        try
                        {
                            DraftSheetResult views =
                                DraftManholeSheetService.Generate(doc,
                                    foundation, footprint, log,
                                    forProduction: true);
                            newSheet = FirstProductionSheetService.Build(
                                doc, foundation, views, actual, log);
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
            uidoc.RequestViewChange(newSheet);
            TaskDialog.Show("First Production Manhole",
                "COMMITTED: " + id +
                "\nNew native openings: " + applied.NewOpenings +
                "\nManaged unchanged: " + applied.ManagedUnchanged +
                "\nManaged updated: " + applied.ManagedUpdated +
                "\nVirtual deferred: " + review.VirtualCount +
                "\n3D: " + production3D.Name + " (MH_3D)" +
                "\nSheet: " + newSheet.SheetNumber +
                " / " + newSheet.Name +
                "\nPreliminary opening setout is shown on the sheet. " +
                "Verify dimensions and elevations before issuing." +
                "\nReview CSV: " + csv +
                "\nSave the RVT to retain the output.");
        }

        private static void GenerateSixRowLayoutSheet(UIDocument uidoc,
            IList<SimpleManholeItem> candidates, ViewSheet sample,
            DiagnosticLogger log)
        {
            Document doc = uidoc.Document;
            // Never pull an already placed manual view onto a new sheet.
            HashSet<int> placed = new HashSet<int>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport)).Cast<Viewport>()
                    .Select(p => p.ViewId.IntegerValue));
            var list = new List<Element>();
            foreach (SimpleManholeItem item in candidates)
            {
                if (list.Count == 8) break;
                Element foundation = Resolve(doc, item);
                string prefix = "MH_" +
                    item.FoundationId + "_DRAFT_2D";
                bool used = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>()
                    .Any(v => !v.IsTemplate &&
                        (v.Name == prefix + "_PLAN" ||
                         v.Name == prefix + "_W1" ||
                         v.Name == prefix + "_W2" ||
                         v.Name == prefix + "_W3" ||
                         v.Name == prefix + "_W4" ||
                         v.Name == prefix + "_OUT_W1" ||
                         v.Name == prefix + "_OUT_W2" ||
                         v.Name == prefix + "_OUT_W3" ||
                         v.Name == prefix + "_OUT_W4") &&
                        placed.Contains(v.Id.IntegerValue));
                if (used)
                {
                    log.Info("SEVEN ROW SKIP manually placed views for foundation " +
                        item.FoundationId);
                    continue;
                }
                list.Add(foundation);
            }
            if (list.Count == 0)
            {
                TaskDialog.Show("Six-Row Test Sheet",
                    "No unplaced eligible manholes found. " +
                    "Your manually arranged views were preserved.");
                return;
            }
            SixRowLayoutResult result =
                SixRowLayoutSheetService.Generate(
                    doc, list, sample, log);
            if (result.Sheet != null)
                uidoc.RequestViewChange(result.Sheet);
            TaskDialog.Show("Six-Row Test Sheet",
                "New sheet: " + (result.Sheet != null ?
                    result.Sheet.SheetNumber + " / " +
                    result.Sheet.Name : "NONE") +
                "\nRows placed: " + result.PlacedManholes +
                "\nRows skipped: " + result.SkippedManholes +
                "\nManhole IDs: " + string.Join(",",
                    result.PlacedIds) +
                "\n\n" +
                (result.Problems.Count == 0 ? "No row problems." :
                    "Skipped reasons:\n" +
                    string.Join("\n", result.Problems.Take(6))) +
                "\n\nLog: " + log.LogPath +
                "\nSave the RVT after checking the layout.");
        }

        private static void Export(Document doc,
            DiagnosticLogger log)
        {
            List<ManholeDataRecord> saved =
                ManholeDataCarrierService.ReadAll(doc);
            if (saved.Count == 0)
            {
                TaskDialog.Show("Manufacturer Excel",
                    "No saved manhole data exists yet. " +
                    "The Scan/Review functions do not create fabrication " +
                    "records or sheets. Complete and save one manhole " +
                    "using the existing tested workflow first.");
                return;
            }
            ManufacturerExcelExportResult result =
                ManufacturerExcelExportService.Export(doc, log);
            TaskDialog.Show("Manufacturer Excel",
                "Saved manholes: " + result.ManholeCount +
                "\nRecorded openings: " + result.OpeningCount +
                "\nWorkbook: " + result.WorkbookPath);
        }
    }
}
