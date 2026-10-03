using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;
using Hatco.PrecastManholeManager.Services;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        private static void RunUnattended(UIApplication app, DiagnosticLogger log, double clearance, bool timingDiagnostic = false, bool cropOrderExperiment = false, bool extraRegeneration = true, bool sheetsOnly = false, bool mergeOverlapping = false, bool dimensionsAfterSheets = false, bool fullAutomation = false, IList<SimpleManholeItem> targets = null)
        {
            if (fullAutomation) { sheetsOnly = true; dimensionsAfterSheets = false; timingDiagnostic = false; }
            if (dimensionsAfterSheets) { sheetsOnly = true; timingDiagnostic = false; }
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc.Document;
            if (doc.IsReadOnly || doc.IsLinked || doc.IsModelInCloud || doc.IsModifiable)
                throw new InvalidOperationException("Run on an editable local RVT. For a cloud model, open a local detached copy first.");
            if (string.IsNullOrWhiteSpace(doc.PathName) || doc.IsDetached)
                throw new InvalidOperationException("Save the current model to its intended RVT path before running. The tool saves in place and does not create another RVT.");
            if (double.IsNaN(clearance) || double.IsInfinity(clearance) || clearance < 0)
                throw new InvalidOperationException("Invalid clearance.");
            WorkflowPreflightService.Require(doc, log, drawings: true, dimensions: !sheetsOnly || dimensionsAfterSheets || fullAutomation, openings: !sheetsOnly || fullAutomation, clearance: clearance);
            var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v=>v.IsTemplate).Select(v=>v.Name).ToList();
            foreach (string name in (sheetsOnly ? new[] { "MH_PLAN", "MH_SEC" } : new[] { "MH_PLAN", "MH_SEC", "MH_3D" }))
                if (!templates.Any(n=>n.Equals(name,StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Missing template " + name);
            ManholeViewTitleService.RequiredSectionType(doc);
            if ((!sheetsOnly || dimensionsAfterSheets || fullAutomation) && !new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Any(t=>t.StyleType == DimensionStyleType.Linear && t.Name.Equals("HTC_DIM_1.8mm",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Missing linear dimension type HTC_DIM_1.8mm.");
            var titleblock = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .OrderByDescending(t=>t.Name.IndexOf("A0",StringComparison.OrdinalIgnoreCase)>=0?2:
                    t.Name.IndexOf("A1",StringComparison.OrdinalIgnoreCase)>=0?1:0).FirstOrDefault();
            if (titleblock == null) throw new InvalidOperationException("Load an A0/A1 titleblock.");
            if (!new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Any())
                throw new InvalidOperationException("Load a text type.");
            if (targets != null && (!fullAutomation || targets.Count == 0)) throw new InvalidOperationException("A non-empty full automation scope is required.");
            if (targets != null) foreach (var target in targets) Resolve(doc, target);
            var targetIds = targets == null ? null : new HashSet<string>(targets.Select(x => x.UniqueId));
            var numbering = ManholeNumberingService.Preview(doc);
            int targetCount = targetIds == null ? numbering.Rows.Count : numbering.Rows.Count(x => targetIds.Contains(x.FoundationUniqueId));
            if (targetCount == 0) throw new InvalidOperationException("No recognized manhole in the requested scope.");
            if (numbering.Errors.Count > 0) throw new InvalidOperationException(string.Join("\n", numbering.Errors));
            if (numbering.Rows.Count == 0) throw new InvalidOperationException("No eligible manholes found.");
            foreach (var row in numbering.Rows)
                if (!row.NewNumber && (targetIds == null || targetIds.Contains(row.FoundationUniqueId))) BatchSheetLayoutService.Find(doc, doc.GetElement(new ElementId(row.FoundationId)));
            string folder = Path.Combine(Path.GetDirectoryName(log.LogPath), "Batch_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0,6));
            string output = doc.PathName;
            var ask = new TaskDialog(targetIds != null ? "Complete Manhole" : fullAutomation ? "Full Automation - All" : "Generate / Update All") {
                MainInstruction = (fullAutomation ? "Run FULL AUTOMATION for " : dimensionsAfterSheets ? "Prepare SHEETS then DIMENSIONS for " : sheetsOnly ? "Prepare SHEETS ONLY for " : "Run ") + targetCount + " manholes unattended?",
                MainContent = (targets == null ? "" : "Scope: " + string.Join(", ", targets.Select(x => x.ManholeName + " [" + x.FoundationId + "]")) + "\n") + (fullAutomation ? "Sheets -> create/update openings -> geometry recheck -> dimensions -> plan marks / NO BUBBLE NTS -> read-only drawing checks. No prompts between phases. Merge overlapping openings: " + (mergeOverlapping ? "ON" : "OFF") + ". No automatic base repair, profile reset or moved-view refresh.\n" : "") + "Pipes and ducts only. Clearance per side: " + clearance + " mm.\n" +
                    (!sheetsOnly ? "Each opening/overlapping group commits independently. Merge overlapping openings: " + (mergeOverlapping ? "ON" : "OFF") + ".\n" : "") +
                    (dimensionsAfterSheets ? "Stage 1: create/reuse and place views, then save. Stage 2: opening/body/base dimensions for all prepared manholes, including REVIEW cases. No opening creation or repair. No prompts between stages.\n" : sheetsOnly && !fullAutomation ? "SHEETS ONLY: creates/reuses body views and reserved rows. No opening pass; existing physical cuts remain unchanged.\n" : "") +
                    (timingDiagnostic ? "TIMING DIAGNOSTIC: at most 3 new documentation attempts, no opening pass. Extra regeneration=" + extraRegeneration + ". Saves changes in the current RVT.\n" : "") +
                    (timingDiagnostic && cropOrderExperiment ? "EXPERIMENT: set plan crop bounds before activating the crop.\n" : "") +
                    "Six fixed rows per sheet at 1:25; one manhole per row, failed rows remain reserved. Existing generated views may move from their individual tool sheets into these rows.\n" +
                    (sheetsOnly ? "Prepares and saves body views on sheets; opening problems do not block documentation.\n" : "Stage 1 prepares and saves body views on sheets for all identifiable manholes. Stage 2 attempts openings; failed cuts retain the prepared views for manual completion.\n") +
                    "Missing internal IDs will be assigned. Repaired issues are checked again. Verified end connectors touching/entering a wall or up to 150 mm before it are included (approach within 45 degrees).\n" +
                    "Changes are saved IN THE CURRENT RVT every 10 manholes or 5 minutes and at completion. No new RVT is created.\n" +
                    (doc.IsWorkshared ? "Uses Save only; Synchronize with Central is not performed.\n" : "") +
                    "Current RVT: " + output + "\n\nStart now, then leave Revit open. Stop is available between manholes.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No };
            if (ask.Show() != TaskDialogResult.Yes) return;
            Directory.CreateDirectory(folder);
            doc.Save(new SaveOptions());
            log.Info("BATCH SAVE IN PLACE: " + output);
            string report = Path.Combine(folder, "RunReport.csv");
            string summaryPath = Path.Combine(folder, "RunSummary.txt");
            int processed = 0, committed = 0, review = 0, dimensionReview = 0, savedThrough = 0;
            var documented = new HashSet<int>();
            string dimensionSummary = "Dimensions: not requested.";
            string automationSummary = "";
            string stopped = "";
            bool runFailed = false;
            DateTime lastSave = DateTime.Now;
            var progress = new BatchProgressWindow(app.MainWindowHandle);
            EventHandler<FailuresProcessingEventArgs> handler = (s,e) =>
            {
                var accessor = e.GetFailuresAccessor();
                if (accessor.GetDocument() != doc) return;
                bool error = false;
                foreach (var failure in accessor.GetFailureMessages())
                {
                    log.Warn("BATCH REVIT FAILURE: " + failure.GetDescriptionText());
                    if (failure.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(failure);
                    else error = true;
                }
                if (error) e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            };
            using (var writer = new StreamWriter(report, false, new UTF8Encoding(true)) { AutoFlush = true })
            using (var cache = new LinkedMepScanCache())
            {
                writer.WriteLine("Time,FoundationId,Manhole,Sheet,Row,Status,Details");
                try
                {
                    app.Application.FailuresProcessing += handler;
                    progress.Start();
                    progress.Update(0, targetCount, "Assigning IDs and reserving every manhole position...");
                    ManholeNumberingService.Apply(doc, numbering, log, targetIds);
                    var allItems = SimpleProjectScanService.LoadFast(doc);
                    var items = allItems.Where(i => targetIds == null || targetIds.Contains(i.UniqueId)).OrderBy(i=>BatchSheetLayoutService.ManholeOrder(i.ManholeName))
                        .ThenBy(i=>i.ManholeName, StringComparer.OrdinalIgnoreCase).ThenBy(i=>i.FoundationId).ToList();
                    using (var tx = new Transaction(doc, "HATCO - Reserve Stable Batch Rows"))
                    {
                        tx.Start(); TransactionFailureHandling.Configure(tx, log);
                        PerformanceMeasurement.Call(log, "Batch.ReserveRows", doc.Title,
                            () => BatchSheetLayoutService.Reserve(doc, allItems.Select(i=>Resolve(doc,i)).ToList(), titleblock, log, targetIds));
                        if (PerformanceMeasurement.Call(log, "Transaction.Commit.Reservations", doc.Title,
                            () => tx.Commit()) != TransactionStatus.Committed) throw new InvalidOperationException("Could not reserve sheet rows.");
                    }
                    PerformanceMeasurement.Call(log, "Document.Save.Reservations", doc.Title, () => doc.Save(new SaveOptions()));
                    foreach (var item in items)
                    {
                        var slot = BatchSheetLayoutService.Find(doc, Resolve(doc,item));
                        WriteBatchRow(writer, item, slot, "QUEUED", "Reservation validated; existing sheet notes preserved");
                    }
                    PrepareBatchDocumentation(doc, items, log, progress, writer, documented, timingDiagnostic, cropOrderExperiment, extraRegeneration, fullAutomation);
                    if (timingDiagnostic) stopped = "Timing diagnostic finished. Opening stage was not run. See the .performance.csv beside the log.";
                    if (sheetsOnly) stopped = progress.CancelRequested ? "Sheets-only pass stopped by user; saved progress retained." : "Sheets-only pass finished. Opening stage was not run. See VIEWS statuses in the report.";
                    if (fullAutomation)
                    {
                        automationSummary = RunFullAutomationPhases(uidoc, items, log, progress, writer, clearance, mergeOverlapping, targetIds == null ? null : new HashSet<int>(items.Select(i => i.FoundationId)));
                        stopped = progress.CancelRequested ? "Full automation stopped by user; committed progress saved." : "Full automation phases finished; review report for incomplete items.";
                    }
                    if (dimensionsAfterSheets)
                    {
                        if (!progress.CancelRequested)
                            dimensionSummary = RunBatchDimensions(doc, items, log, progress, writer);
                        else dimensionSummary = "Dimensions not started: stopped during sheet preparation.";
                        stopped = progress.CancelRequested ? "Sheets + dimensions stopped by user; saved progress retained." : "Sheets + dimensions pass finished; see per-manhole results.";
                    }
                    foreach (var item in (timingDiagnostic || sheetsOnly) ? new List<SimpleManholeItem>() : items)
                    {
                        progress.Update(processed, items.Count, "Stage 2/2 - openings: " + item.ManholeName + "\nSaved through opening item " + savedThrough + "\n" + output);
                        if (progress.CancelRequested) { stopped = "Stopped by user between manholes."; break; }
                        var foundation = Resolve(doc,item);
                        var slot = BatchSheetLayoutService.Find(doc,foundation);
                        WriteBatchRow(writer,item,slot,"RUNNING", "");
                        string status, details;
                        ProductionManholeResult result;
                        bool modelCommitted = false;
                        try
                        {
                            if (!documented.Contains(item.FoundationId))
                                throw new InvalidOperationException("Documentation needs manual completion; reserved row retained. See VIEWS REVIEW in run report.");
                            // Fresh production preflight replaces stale registry gating without a second MEP scan.
                            result = GenerateWallOpenings(uidoc, item, log, clearance, mergeOverlapping);
                            if (!result.Committed) throw new InvalidOperationException(result.Summary);
                            modelCommitted = true;
                            details = result.Summary;
                            status = result.DimensionsComplete ? "COMPLETE" : "COMMITTED - DIMENSION REVIEW";
                            if (result.LayoutNeedsReview)
                                status = result.DimensionsComplete ? "COMMITTED - LAYOUT REVIEW" : "COMMITTED - DIMENSION AND LAYOUT REVIEW";
                            if (result.OpeningsNeedReview) { status = "COMMITTED - OPENINGS REVIEW"; review++; }
                            try
                            {
                                var footprint = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
                                if (!footprint.Accepted) throw new InvalidOperationException(footprint.Reason);
                                using (var viewTx = new Transaction(doc, "HATCO - Update Production 3D"))
                                {
                                    viewTx.Start(); TransactionFailureHandling.Configure(viewTx, log);
                                    ManholeReviewViewService.CreateProduction(doc, foundation, footprint, log);
                                    if (viewTx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Production 3D transaction rejected.");
                                }
                            }
                            catch (Exception ex)
                            {
                                status += " / 3D REVIEW"; details += "\n3D: " + ex.Message;
                                ManholeReviewRegistry.Upsert(doc, foundation, ex.Message, null, "3D REVIEW", log, ReviewDomain.Presentation);
                                log.Error("Production 3D failed; opening commits retained", ex);
                            }
                            committed++;
                            if (!result.DimensionsComplete) dimensionReview++;
                            if (result.OpeningsNeedReview) ManholeReviewRegistry.Upsert(doc, foundation, details, null, "OPENINGS REVIEW", log, ReviewDomain.Openings);
                            if (!result.DimensionsComplete) ManholeReviewRegistry.Upsert(doc, foundation, details, null, "DIMENSION REVIEW", log, ReviewDomain.Dimensions);
                            else ManholeReviewRegistry.Resolve(doc, foundation, ReviewDomain.Dimensions);
                            if (result.LayoutNeedsReview) ManholeReviewRegistry.Upsert(doc, foundation, details, null, "LAYOUT REVIEW", log, ReviewDomain.Layout);
                            if (status != "COMPLETE")
                            {
                                using (var tx = new Transaction(doc,"HATCO - Mark Dimension Review"))
                                {
                                    tx.Start(); TransactionFailureHandling.Configure(tx,log);
                                    BatchSheetLayoutService.SetStatus(doc,foundation,slot,status + " - see run report for wall results.");
                                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Cannot label dimension review row.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            status = modelCommitted ? "COMMITTED - REPORT REVIEW" : "REVIEW";
                            details = ex.Message; review++;
                            log.Error("BATCH MANHOLE FAILED " + item.ManholeName,ex);
                            ManholeReviewRegistry.Upsert(doc,foundation,details,null,"BATCH REVIEW",log, modelCommitted ? ReviewDomain.Layout : ReviewDomain.Openings);
                            using (var tx = new Transaction(doc,"HATCO - Mark Reserved Review Row"))
                            {
                                tx.Start(); TransactionFailureHandling.Configure(tx,log);
                                BatchSheetLayoutService.SetStatus(doc,foundation,slot,
                                    (documented.Contains(item.FoundationId) ? "VIEWS RETAINED / OPENINGS REVIEW - " : "MANUAL VIEWS REQUIRED - ") + details);
                                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Cannot update reserved review row.");
                            }
                        }
                        processed++;
                        WriteBatchRow(writer,item,slot,status,details);
                        if (processed % 10 == 0 || DateTime.Now - lastSave >= TimeSpan.FromMinutes(5))
                        {
                            doc.Save(new SaveOptions()); savedThrough = processed; lastSave = DateTime.Now;
                            File.WriteAllText(summaryPath,"RUNNING\nSaved through: " + savedThrough + "\nRVT: " + output);
                        }
                    }
                }
                catch (Exception ex) { runFailed = true; stopped = "Run stopped: " + ex.Message; log.Error(stopped,ex); }
                finally
                {
                    try { doc.Save(new SaveOptions()); savedThrough = processed; }
                    catch (Exception ex) { runFailed = true; stopped += "\nFINAL SAVE FAILED: " + ex.Message; log.Error("Batch save failed",ex); }
                    app.Application.FailuresProcessing -= handler;
                    progress.Finish();
                }
            }
            string readiness;
            try { readiness = ManholeReviewRegistry.ExportReadiness(doc, Path.Combine(folder, "Readiness.csv"), targetIds); }
            catch (Exception ex) { readiness = "Readiness report unavailable: " + ex.Message; runFailed = true; log.Error(readiness, ex); }
            string summary = (stopped.Length == 0 ? "Run completed." : stopped) +
                "\nManholes with prepared views: " + documented.Count + " / " + targetCount +
                (fullAutomation ? "\n" + automationSummary : sheetsOnly ? "\nOpening stage: not requested." : "\nProcessed openings: " + processed + " / " + numbering.Rows.Count + "\nCommitted: " + committed +
                "\nReview: " + review + "\nSaved through item: " + savedThrough +
                "\nCommitted with dimension review: " + dimensionReview) +
                (dimensionsAfterSheets ? "\n" + dimensionSummary : "") +
                "\n" + readiness + "\nRVT: " + output + "\nReport: " + report + "\nLog: " + log.LogPath;
            File.WriteAllText(summaryPath,summary);
            log.Info("BATCH RUN RESULTS: " + summary);
            if (runFailed) TaskDialog.Show("Run needs attention", summary);
        }
        private static void WriteBatchRow(StreamWriter writer, SimpleManholeItem item, BatchSheetSlot slot, string status, string details)
        {
            Func<string,string> csv = value => "\"" + (value ?? "").Replace("\"","\"\"") + "\"";
            writer.WriteLine(string.Join(",",new[] { csv(DateTime.Now.ToString("O")),item.FoundationId.ToString(),
                csv(item.ManholeName),csv(slot?.Sheet?.SheetNumber),slot == null ? "" : (slot.Row+1).ToString(),csv(status),csv(details) }));
        }
    }
}
