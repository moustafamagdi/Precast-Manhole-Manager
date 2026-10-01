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
        // Runs under the batch failure handler after documentation has been saved.
        // No opening generation, registry gating or modal prompts in this phase.
        private static string RunBatchDimensions(Document doc, IList<SimpleManholeItem> items,
            DiagnosticLogger log, BatchProgressWindow progress, StreamWriter writer)
        {
            int processed = 0, complete = 0, review = 0, skipped = 0, savedThrough = 0;
            DateTime lastSave = DateTime.Now;
            log.WriteHeader("BATCH STAGE 2 - DIMENSIONS - INCLUDING REVIEW MANHOLES");
            foreach (var item in items)
            {
                progress.Update(processed, items.Count, "Stage 2/2 - dimensions: " + item.ManholeName +
                    "\nSaved through dimension item " + savedThrough);
                if (progress.CancelRequested) break;
                Element foundation = null;
                BatchSheetSlot slot = null;
                string status, details;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foundation = Resolve(doc, item);
                    slot = BatchSheetLayoutService.Find(doc, foundation);
                    if (slot == null || !BatchSheetLayoutService.HasPreparedViews(doc, foundation, slot))
                    {
                        skipped++;
                        status = "DIMENSIONS SKIPPED";
                        details = "Prepared plan/sections missing; reserved row and existing views retained for manual completion.";
                    }
                    else
                    {
                        bool ok = false;
                        details = OpeningDimensionService.Generate(doc, foundation, log, value => ok = value);
                        status = ok ? "DIMENSIONS COMPLETE" : "DIMENSIONS REVIEW";
                        if (ok) complete++; else review++;
                    }
                }
                catch (Exception ex)
                {
                    review++;
                    status = "DIMENSIONS REVIEW";
                    details = ex.Message;
                    log.Error("BATCH DIMENSIONS REVIEW " + item.ManholeName, ex);
                }
                processed++;
                WriteBatchRow(writer, item, slot, status, details);
                log.Info("PERF BATCH_DIMENSIONS Foundation=" + item.FoundationId + " Status=" + status +
                    " Seconds=" + timer.Elapsed.TotalSeconds.ToString("0.000"));
                // Reporting failure cannot undo dimension commits or stop later manholes.
                if (status != "DIMENSIONS COMPLETE" && foundation != null)
                {
                    try { ManholeReviewRegistry.Upsert(doc, foundation, details, null, "DIMENSION REVIEW", log); }
                    catch (Exception ex) { log.Error("Dimension review registry update failed; see run report", ex); }
                }
                // Keep opening REVIEW states: successful dimensions do not certify openings.
                if (processed % 10 == 0 || DateTime.Now - lastSave >= TimeSpan.FromMinutes(5))
                {
                    PerformanceMeasurement.Call(log, "Document.Save.DimensionCheckpoint", item.ManholeName,
                        () => doc.Save(new SaveOptions()));
                    savedThrough = processed;
                    lastSave = DateTime.Now;
                    log.Info("DIMENSION CHECKPOINT SAVED Processed=" + processed + " Complete=" + complete +
                        " Review=" + review + " Skipped=" + skipped);
                    WriteBatchRow(writer, item, slot, "DIMENSIONS SAVED", "Saved through dimension item " + savedThrough);
                }
            }
            PerformanceMeasurement.Call(log, "Document.Save.DimensionsEnd", doc.Title,
                () => doc.Save(new SaveOptions()));
            string summary = "Dimensions processed: " + processed + " / " + items.Count +
                "; complete: " + complete + "; review: " + review + "; missing prepared views: " + skipped +
                "; saved through dimension item: " + processed + ".";
            log.Info("BATCH DIMENSION RESULTS: " + summary);
            return summary;
        }
    }
}
