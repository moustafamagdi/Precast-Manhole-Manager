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
