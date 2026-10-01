using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Services;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        // Commit documentation before attempting any opening. Later cut rollback
        // must leave the existing body views available for manual completion.
        private static void PrepareBatchDocumentation(Document doc,
            IList<SimpleManholeItem> items, DiagnosticLogger log,
            BatchProgressWindow progress, StreamWriter writer, HashSet<int> ready, bool timingDiagnostic = false, bool cropOrderExperiment = false, bool extraRegeneration = true, bool fullAutomation = false)
        {
            int processed = 0;
            int diagnosticAttempts = 0;
            var lastSave = DateTime.Now;
            foreach (var item in items)
            {
                if (timingDiagnostic && diagnosticAttempts >= 3) break;
                progress.Update(processed, items.Count,
                    (fullAutomation ? "Full automation 1/5 - sheets and views: " : "Stage 1/2 - preparing sheets and views: ") + item.ManholeName);
                if (progress.CancelRequested) break;
                Element foundation = null;
                BatchSheetSlot slot = null;
                string status, details;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foundation = Resolve(doc, item);
                    slot = BatchSheetLayoutService.Find(doc, foundation);
                    if (BatchSheetLayoutService.HasPreparedViews(doc, foundation, slot))
                    {
                        ready.Add(item.FoundationId);
                        WriteBatchRow(writer, item, slot, "VIEWS READY", "Reused five existing placed views; layout preserved.");
                        log.Info("PERF DOCUMENTATION_REUSE Foundation=" + item.FoundationId + " Seconds=" + timer.Elapsed.TotalSeconds.ToString("0.000"));
                        processed++;
                        continue;
                    }
                    var footprint = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
                    if (!footprint.Accepted)
                        throw new InvalidOperationException("Cannot identify body for sections: " + footprint.Reason);
                    diagnosticAttempts++;
                    using (var measurement = timingDiagnostic ? PerformanceMeasurement.BeginAttribution(doc, log, cropOrderExperiment, extraRegeneration) : null)
                    using (var tx = new Transaction(doc, "HATCO - Prepare Manhole Documentation"))
                    {
                        tx.Start();
                        TransactionFailureHandling.Configure(tx, log);
                        var views = DraftManholeSheetService.Generate(doc, foundation, footprint, log, forProduction: true);
                        BatchSheetLayoutService.Place(doc, foundation, slot, views.Views,
                            new List<UnifiedOpeningReviewRow>(), log);
                        BatchSheetLayoutService.SetStatus(doc, foundation, slot,
                            "VIEWS READY - verify layout and dimensions.");
                        if (PerformanceMeasurement.Call(log, "Transaction.Commit.Documentation", item.ManholeName,
                            () => tx.Commit()) != TransactionStatus.Committed)
                            throw new InvalidOperationException("Documentation transaction rejected.");
                    }
                    ready.Add(item.FoundationId);
                    status = "VIEWS READY";
                    details = "Body views committed independently of opening stage.";
                }
                catch (Exception ex)
                {
                    status = "VIEWS REVIEW";
                    details = ex.Message;
                    log.Error("BATCH DOCUMENTATION REVIEW " + item.ManholeName, ex);
                    try
                    {
                    if (foundation != null && slot != null)
                    using (var tx = new Transaction(doc, "HATCO - Mark Documentation Review"))
                    {
                        tx.Start();
                        TransactionFailureHandling.Configure(tx, log);
                        BatchSheetLayoutService.SetStatus(doc, foundation, slot,
                            "MANUAL VIEWS REQUIRED - " + details);
                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Cannot label documentation review row.");
                    }
                    }
                    catch (Exception noteError) { log.Error("Documentation review note failed; continuing batch", noteError); }
                }
                WriteBatchRow(writer, item, slot, status, details);
                log.Info("PERF DOCUMENTATION Foundation=" + item.FoundationId + " Seconds=" + timer.Elapsed.TotalSeconds.ToString("0.000"));
                processed++;
                if (processed % 10 == 0 || DateTime.Now - lastSave >= TimeSpan.FromMinutes(5))
                {
                    PerformanceMeasurement.Call(log, "Document.Save.Checkpoint", item.ManholeName,
                        () => doc.Save(new SaveOptions()));
                    lastSave = DateTime.Now;
                }
            }
            if (!progress.CancelRequested && !fullAutomation) ManholeViewPresentationService.ApplyAll(doc, log, (done, total) =>
            {
                progress.Update(done, total, "Cleaning plan section marks and viewport types");
                return !progress.CancelRequested;
            });
            PerformanceMeasurement.Call(log, "Document.Save.DocumentationEnd", doc.Title,
                () => doc.Save(new SaveOptions()));
        }
    }
}
