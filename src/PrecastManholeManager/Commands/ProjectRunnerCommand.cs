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
    public sealed partial class ProjectRunnerCommand : IExternalCommand
    {
        private static double _lastClearanceMm = 50;

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
                        var window = new SimpleProjectWindow(rows, _lastClearanceMm);
                        if (window.ShowDialog() != true ||
                            window.Action == ProjectAction.Close)
                            return Result.Succeeded;
                        _lastClearanceMm = window.ClearanceMm;
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
                            else if (window.Action == ProjectAction.CleanScan)
                                TaskDialog.Show("Clean Scan - All Manholes", ManholeRecheckService.RunAll(
                                    doc, window.ClearanceMm, log));
                            else if (window.Action == ProjectAction.NumberAll)
                                AssignAllManholeNames(doc, log);
                            else if (window.Action == ProjectAction.ProductionAll)
                            {
                                RunUnattended(input.Application, log, window.ClearanceMm, window.TimingDiagnostic, window.CropOrderExperiment, window.DiagnosticExtraRegeneration);
                                return Result.Succeeded;
                            }
                            else if (window.Action == ProjectAction.ExistingOne ||
                                window.Action == ProjectAction.ExistingAll || window.Action == ProjectAction.ExistingDimensions)
                            {
                                RunExistingPrepared(input.Application, log, window.ClearanceMm,
                                    window.Action == ProjectAction.ExistingOne ? window.SelectedManhole : null,
                                    window.Action == ProjectAction.ExistingDimensions);
                                return Result.Succeeded;
                            }
                            else if (window.Action == ProjectAction.ReviewOne)
                                InspectSelected(doc, window.SelectedManhole,
                                    log, window.ClearanceMm);
                            else if (window.Action == ProjectAction.RecheckOne)
                                TaskDialog.Show("Recheck Selected", ManholeRecheckService.Run(
                                    doc, Resolve(doc, window.SelectedManhole), window.ClearanceMm, log));
                            else if (window.Action == ProjectAction.Make3D)
                                MakeReview3D(uiDoc, window.SelectedManhole,
                                    log);
                            else if (window.Action == ProjectAction.DraftSheet)
                                GenerateDraftSheet(uiDoc,
                                    window.SelectedManhole, log);
                            else if (window.Action == ProjectAction.ProductionOne)
                                GenerateProductionManhole(uiDoc,
                                    window.SelectedManhole, log, window.ClearanceMm);
                            else if (window.Action == ProjectAction.DimensionOne)
                                TaskDialog.Show("Opening Dimensions", OpeningDimensionService.Generate(
                                    doc, Resolve(doc, window.SelectedManhole), log));
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
            SimpleManholeItem row, DiagnosticLogger log, double clearanceMm)
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
            // User-selected clearance / 150 mm max gap / 15 degree plan angle.
            UnifiedOpeningReviewResult review =
                UnifiedOpeningReviewService.Collect(
                    doc, foundation, footprint, log, clearanceMm, 150, 15);
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
