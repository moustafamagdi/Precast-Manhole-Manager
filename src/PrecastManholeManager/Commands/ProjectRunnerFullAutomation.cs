using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Services;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        // Uses the outer batch's failure handler, MEP cache, report and final-save guard.
        // Opening eligibility is independent of successful documentation.
        private static string RunFullAutomationPhases(UIDocument uidoc, IList<SimpleManholeItem> items,
            DiagnosticLogger log, BatchProgressWindow progress, StreamWriter writer, double clearance, bool merge)
        {
            var doc = uidoc.Document;
            int attempted = 0, openingReview = 0, checkedCount = 0, checksFailed = 0;
            DateTime lastSave = DateTime.Now;
            foreach (var item in items)
            {
                progress.Update(attempted, items.Count, "Full automation 2/5 - create/update openings: " + item.ManholeName);
                if (progress.CancelRequested) break;
                Element foundation = null;
                string details, status;
                try
                {
                    foundation = Resolve(doc, item);
                    var result = GenerateWallOpenings(uidoc, item, log, clearance, merge, deferDimensions: true);
                    details = result.Summary;
                    status = result.OpeningsNeedReview ? "OPENINGS REVIEW" : "OPENINGS PROCESSED";
                    if (result.OpeningsNeedReview) openingReview++;
                }
                catch (Exception ex)
                {
                    openingReview++;
                    status = "OPENINGS ERROR"; details = ex.Message;
                    log.Error("AUTOMATION OPENINGS " + item.ManholeName, ex);
                    TryRegisterAutomationIssue(doc, foundation, details, log);
                }
                WriteBatchRow(writer, item, null, status, details);
                attempted++;
                if (attempted % 10 == 0 || DateTime.Now - lastSave >= TimeSpan.FromMinutes(5))
                {
                    doc.Save(new SaveOptions()); lastSave = DateTime.Now;
                    WriteBatchRow(writer, item, null, "OPENINGS SAVED", "Saved through opening item " + attempted);
                }
            }
            doc.Save(new SaveOptions());
            foreach (var item in items)
            {
                progress.Update(checkedCount, items.Count, "Full automation 3/5 - verify openings and services: " + item.ManholeName);
                if (progress.CancelRequested) break;
                Element foundation = null;
                try
                {
                    foundation = Resolve(doc, item);
                    string details = ManholeRecheckService.Run(doc, foundation, clearance, log);
                    var issue = ManholeReviewRegistry.Load(doc).FirstOrDefault(x => x.FoundationUniqueId == item.UniqueId);
                    WriteBatchRow(writer, item, null, "FINAL CHECK " + (issue?.Status ?? "PASSED"), details);
                }
                catch (Exception ex)
                {
                    checksFailed++;
                    log.Error("AUTOMATION RECHECK " + item.ManholeName, ex);
                    TryRegisterAutomationIssue(doc, foundation, ex.Message, log);
                    WriteBatchRow(writer, item, null, "FINAL CHECK ERROR", ex.Message);
                }
                checkedCount++;
            }
            // Recheck resolves geometry issues only. Dimension failures are recorded afterwards,
            // so a successful geometric check cannot erase a failed dimension phase.
            string dimensions = "Dimensions not started.";
            if (!progress.CancelRequested) dimensions = RunBatchDimensions(doc, items, log, progress, writer, fullAutomation: true);
            string presentation = "Presentation not started.";
            if (!progress.CancelRequested)
                presentation = ManholeViewPresentationService.ApplyAll(doc, log, (done, total) => {
                    progress.Update(done, total, "Full automation 5/5 - plan marks and viewport types");
                    if (!progress.CancelRequested && (done > 0 && done % 10 == 0 || DateTime.Now - lastSave >= TimeSpan.FromMinutes(5)))
                    {
                        doc.Save(new SaveOptions()); lastSave = DateTime.Now;
                        log.Info("AUTOMATION PRESENTATION CHECKPOINT saved through " + done);
                    }
                    return !progress.CancelRequested;
                });
            doc.Save(new SaveOptions());
            return "Opening attempts: " + attempted + "/" + items.Count + "; opening review/errors: " + openingReview +
                "\nGeometry checks: " + checkedCount + "; check errors: " + checksFailed + "\n" + dimensions + "\n" + presentation;
        }

        private static void TryRegisterAutomationIssue(Document doc, Element foundation, string reason, DiagnosticLogger log)
        {
            try { if (foundation != null) ManholeReviewRegistry.Upsert(doc, foundation, reason, null, "AUTOMATION REVIEW", log); }
            catch (Exception ex) { log.Error("Review register unavailable; issue retained in run report", ex); }
        }
    }
}
