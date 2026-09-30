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
                            else if (window.Action == ProjectAction.ReviewOne)
                                InspectSelected(doc, window.SelectedManhole,
                                    log);
                            else if (window.Action == ProjectAction.Make3D)
                                MakeReview3D(uiDoc, window.SelectedManhole,
                                    log);
                            else if (window.Action == ProjectAction.DraftSheet)
                                GenerateDraftSheet(uiDoc,
                                    window.SelectedManhole, log);
                            else if (window.Action == ProjectAction.SevenRowSheet)
                                GenerateSevenRowSheet(uiDoc,
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

        private static void GenerateSevenRowSheet(UIDocument uidoc,
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
                if (list.Count == 12) break;
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
                         v.Name == prefix + "_W4") &&
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
                TaskDialog.Show("Seven-Row Test Sheet",
                    "No unplaced eligible manholes found. " +
                    "Your manually arranged views were preserved.");
                return;
            }
            SevenRowSheetResult result =
                SevenRowManholeSheetService.Generate(
                    doc, list, sample, log);
            if (result.Sheet != null)
                uidoc.RequestViewChange(result.Sheet);
            TaskDialog.Show("Seven-Row Test Sheet",
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
