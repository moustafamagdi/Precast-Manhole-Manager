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
        private static void RunUnattended(UIApplication app, DiagnosticLogger log, double clearance, bool timingDiagnostic = false)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc.Document;
            if (doc.IsReadOnly || doc.IsLinked || doc.IsModelInCloud || doc.IsModifiable)
                throw new InvalidOperationException("Run on an editable local RVT. For a cloud model, open a local detached copy first.");
            if (string.IsNullOrWhiteSpace(doc.PathName) || doc.IsDetached)
                throw new InvalidOperationException("Save the current model to its intended RVT path before running. The tool saves in place and does not create another RVT.");
            if (double.IsNaN(clearance) || double.IsInfinity(clearance) || clearance < 0)
                throw new InvalidOperationException("Invalid clearance.");
            var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v=>v.IsTemplate).Select(v=>v.Name).ToList();
            foreach (string name in new[] { "MH_PLAN", "MH_SEC", "MH_3D" })
                if (!templates.Any(n=>n.Equals(name,StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Missing template " + name);
            ManholeViewTitleService.RequiredSectionType(doc);
            if (!new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Any(t=>t.StyleType == DimensionStyleType.Linear && t.Name.Equals("HTC_DIM_1.8mm",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Missing linear dimension type HTC_DIM_1.8mm.");
            var titleblock = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .OrderByDescending(t=>t.Name.IndexOf("A0",StringComparison.OrdinalIgnoreCase)>=0?2:
                    t.Name.IndexOf("A1",StringComparison.OrdinalIgnoreCase)>=0?1:0).FirstOrDefault();
            if (titleblock == null) throw new InvalidOperationException("Load an A0/A1 titleblock.");
            if (!new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Any())
                throw new InvalidOperationException("Load a text type.");
            var numbering = ManholeNumberingService.Preview(doc);
            if (numbering.Errors.Count > 0) throw new InvalidOperationException(string.Join("\n", numbering.Errors));
            if (numbering.Rows.Count == 0) throw new InvalidOperationException("No eligible manholes found.");
            foreach (var row in numbering.Rows)
                BatchSheetLayoutService.Find(doc, doc.GetElement(new ElementId(row.FoundationId)));
            string folder = Path.Combine(Path.GetDirectoryName(log.LogPath), "Batch_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0,6));
            string output = doc.PathName;
            var ask = new TaskDialog("Generate / Update All") {
                MainInstruction = "Run " + numbering.Rows.Count + " manholes unattended?",
                MainContent = "Pipes and ducts only. Clearance per side: " + clearance + " mm.\n" +
                    (timingDiagnostic ? "TIMING DIAGNOSTIC: at most 3 new documentation attempts, extra regeneration, no opening pass. Saves changes in the current RVT.\n" : "") +
                    "Six fixed rows per sheet at 1:25; one manhole per row, failed rows remain reserved. Existing generated views may move from their individual tool sheets into these rows.\n" +
                    "Stage 1 prepares and saves body views on sheets for all identifiable manholes. Stage 2 attempts openings; failed cuts retain the prepared views for manual completion.\n" +
                    "Missing internal IDs will be assigned. Repaired issues are checked again. Virtual-only crossings remain deferred.\n" +
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
            string stopped = "";
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
                    progress.Update(0, numbering.Rows.Count, "Assigning IDs and reserving every manhole position...");
                    ManholeNumberingService.Apply(doc, numbering, log);
                    var items = SimpleProjectScanService.LoadFast(doc).OrderBy(i=>BatchSheetLayoutService.ManholeOrder(i.ManholeName))
                        .ThenBy(i=>i.ManholeName, StringComparer.OrdinalIgnoreCase).ThenBy(i=>i.FoundationId).ToList();
                    using (var tx = new Transaction(doc, "HATCO - Reserve Stable Batch Rows"))
                    {
                        tx.Start(); TransactionFailureHandling.Configure(tx, log);
                        BatchSheetLayoutService.Reserve(doc, items.Select(i=>Resolve(doc,i)).ToList(), titleblock);
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Could not reserve sheet rows.");
                    }
                    doc.Save(new SaveOptions());
                    foreach (var item in items)
                    {
                        var slot = BatchSheetLayoutService.Find(doc, Resolve(doc,item));
                        WriteBatchRow(writer, item, slot, "QUEUED", "Reserved before execution");
                    }
                    PrepareBatchDocumentation(doc, items, log, progress, writer, documented, timingDiagnostic);
                    if (timingDiagnostic) stopped = "Timing diagnostic finished. Opening stage was not run. See the .performance.csv beside the log.";
                    foreach (var item in timingDiagnostic ? new List<SimpleManholeItem>() : items)
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
                            using (var group = new TransactionGroup(doc,"HATCO - Batch Manhole Including Layout"))
                            {
                                group.Start();
                                result = GenerateProductionManhole(uidoc,item,log,clearance,true);
                                if (!result.Committed) throw new InvalidOperationException(result.Summary);
                                details = result.Summary;
                                if (group.Assimilate() != TransactionStatus.Committed)
                                    throw new InvalidOperationException("Batch manhole transaction rejected.");
                            }
                            status = result.DimensionsComplete ? "COMPLETE" : "COMMITTED - DIMENSION REVIEW";
                            if (result.LayoutNeedsReview)
                                status = result.DimensionsComplete ? "COMMITTED - LAYOUT REVIEW" : "COMMITTED - DIMENSION AND LAYOUT REVIEW";
                            modelCommitted = true;
                            committed++;
                            if (!result.DimensionsComplete) dimensionReview++;
                            var issues = ManholeReviewRegistry.Load(doc);
                            foreach (var issue in issues.Where(x=>x.FoundationUniqueId == foundation.UniqueId && x.Status == "OPEN"))
                            { issue.Status = "RESOLVED"; issue.Severity = "BATCH PRODUCTION PASSED"; }
                            ManholeReviewRegistry.Save(doc,issues);
                            if (status != "COMPLETE")
                            {
                                using (var tx = new Transaction(doc,"HATCO - Mark Dimension Review"))
                                {
                                    tx.Start(); TransactionFailureHandling.Configure(tx,log);
                                    BatchSheetLayoutService.SetStatus(doc,foundation,slot,status + " - openings committed; see run report.");
                                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Cannot label dimension review row.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            status = modelCommitted ? "COMMITTED - REPORT REVIEW" : "REVIEW";
                            details = ex.Message; review++;
                            log.Error("BATCH MANHOLE FAILED " + item.ManholeName,ex);
                            ManholeReviewRegistry.Upsert(doc,foundation,details,null,"BATCH REVIEW",log);
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
                catch (Exception ex) { stopped = "Run stopped: " + ex.Message; log.Error(stopped,ex); }
                finally
                {
                    try { doc.Save(new SaveOptions()); savedThrough = processed; }
                    catch (Exception ex) { stopped += "\nFINAL SAVE FAILED: " + ex.Message; log.Error("Batch save failed",ex); }
                    app.Application.FailuresProcessing -= handler;
                    progress.Finish();
                }
            }
            string summary = (stopped.Length == 0 ? "Run completed." : stopped) +
                "\nManholes with prepared views: " + documented.Count + " / " + numbering.Rows.Count +
                "\nProcessed: " + processed + " / " + numbering.Rows.Count + "\nCommitted: " + committed +
                "\nReview: " + review + "\nSaved through item: " + savedThrough +
                "\nCommitted with dimension review: " + dimensionReview +
                "\nRVT: " + output + "\nReport: " + report + "\nLog: " + log.LogPath;
            File.WriteAllText(summaryPath,summary);
            TaskDialog.Show("Unattended Run Results",summary);
        }
        private static void WriteBatchRow(StreamWriter writer, SimpleManholeItem item, BatchSheetSlot slot, string status, string details)
        {
            Func<string,string> csv = value => "\"" + (value ?? "").Replace("\"","\"\"") + "\"";
            writer.WriteLine(string.Join(",",new[] { csv(DateTime.Now.ToString("O")),item.FoundationId.ToString(),
                csv(item.ManholeName),csv(slot.Sheet.SheetNumber),(slot.Row+1).ToString(),csv(status),csv(details) }));
        }
    }
}
