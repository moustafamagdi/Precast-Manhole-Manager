using System;
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
        // Deliberately separate from documentation: no numbering, reservation,
        // view/3D generation, viewport placement or row rearrangement.
        private static void RunExistingPrepared(UIApplication app, DiagnosticLogger log,
            double clearance, SimpleManholeItem selected, bool dimensionsOnly,
            System.Collections.Generic.List<SimpleManholeItem> scopedItems = null, string scopeDescription = null)
        {
            var doc = app.ActiveUIDocument.Document;
            if (doc.IsReadOnly || doc.IsLinked || doc.IsModifiable || doc.IsModelInCloud ||
                doc.IsDetached || string.IsNullOrWhiteSpace(doc.PathName))
                throw new InvalidOperationException("Open an editable, saved local RVT. This operation saves the current file in place.");
            var source = scopedItems ?? (selected == null ? SimpleProjectScanService.LoadFast(doc) :
                new System.Collections.Generic.List<SimpleManholeItem> { selected });
            var eligible = new System.Collections.Generic.List<SimpleManholeItem>();
            foreach (var item in source.OrderBy(x => BatchSheetLayoutService.ManholeOrder(x.ManholeName)))
            {
                try
                {
                    var foundation = Resolve(doc, item);
                    var slot = BatchSheetLayoutService.Find(doc, foundation);
                    if (slot != null && BatchSheetLayoutService.HasPreparedViews(doc, foundation, slot))
                        eligible.Add(item);
                    else log.Info("EXISTING ONLY SKIP Foundation=" + item.FoundationId + " Missing prepared plan/sections on reserved sheet.");
                }
                catch (Exception ex) { log.Error("EXISTING ONLY SKIP Foundation=" + item.FoundationId, ex); }
            }
            if (eligible.Count == 0)
                throw new InvalidOperationException("No complete prepared rows found. Requires existing PLAN and W1-W4 on the reserved six-row sheet.");
            var ask = new TaskDialog("Existing Manholes") {
                MainInstruction = (dimensionsOnly ? "Update dimensions for " : "Update openings and dimensions for ") + eligible.Count + " prepared manhole(s)?",
                MainContent = (scopeDescription == null ? "" : scopeDescription + "\n") +
                    "Eligible: " + eligible.Count + "; skipped: " + (source.Count - eligible.Count) + " (missing prepared views/row).\n" +
                    "Targets: " + string.Join(", ", eligible.Take(20).Select(x => x.ManholeName)) + (eligible.Count > 20 ? ", ..." : "") + "\n" +
                    "No new views or sheets; existing viewport positions are preserved.\n" +
                    (dimensionsOnly ? "Existing cuts remain unchanged.\n" : "Pipes and ducts only. Clearance per side: " + clearance + " mm. Current geometry is validated again, including recorded review cases. Unsafe cuts are skipped.\n") +
                    "Hidden dimensions and annotation crop problems are reported for review.\n" +
                    "Saves in the CURRENT RVT every 10 items or 5 minutes, and at completion. No Synchronize with Central.\n" + doc.PathName,
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (ask.Show() != TaskDialogResult.Yes) return;
            string report = Path.ChangeExtension(log.LogPath, ".existing.csv");
            int done = 0, complete = 0, review = 0;
            DateTime saved = DateTime.Now;
            var progress = new BatchProgressWindow(app.MainWindowHandle);
            EventHandler<FailuresProcessingEventArgs> handler = (s, e) => {
                var access = e.GetFailuresAccessor();
                if (access.GetDocument() != doc) return;
                bool error = false;
                foreach (var failure in access.GetFailureMessages())
                {
                    log.Warn("EXISTING REVIT FAILURE: " + failure.GetDescriptionText());
                    if (failure.GetSeverity() == FailureSeverity.Warning) access.DeleteWarning(failure);
                    else error = true;
                }
                if (error) e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            };
            using (var writer = new StreamWriter(report, false, new UTF8Encoding(true)) { AutoFlush = true })
            using (var cache = new LinkedMepScanCache())
            {
                writer.WriteLine("FoundationId,Status,Details");
                doc.Save(new SaveOptions());
                try
                {
                    app.Application.FailuresProcessing += handler;
                    progress.Start();
                    foreach (var item in eligible)
                    {
                        progress.Update(done, eligible.Count, item.ManholeName);
                        if (progress.CancelRequested) break;
                        string status, details;
                        try
                        {
                            bool ok = false;
                            if (dimensionsOnly)
                                details = OpeningDimensionService.Generate(doc, Resolve(doc, item), log, value => ok = value);
                            else
                            {
                                var result = GenerateProductionManhole(app.ActiveUIDocument, item, log, clearance,
                                    unattended: true, existingOnly: true);
                                ok = result.Committed && result.DimensionsComplete;
                                details = result.Summary;
                            }
                            status = ok ? "COMPLETE" : "DIMENSION REVIEW";
                            if (ok) complete++; else review++;
                        }
                        catch (Exception ex)
                        {
                            status = "REVIEW"; details = ex.Message; review++;
                            log.Error("EXISTING ONLY FAILED Foundation=" + item.FoundationId, ex);
                        }
                        done++;
                        writer.WriteLine(item.FoundationId + "," + status + ",\"" + details.Replace("\"", "\"\"") + "\"");
                        if (done % 10 == 0 || (DateTime.Now - saved).TotalMinutes >= 5)
                        {
                            doc.Save(new SaveOptions()); saved = DateTime.Now;
                            log.Info("EXISTING SAVE IN PLACE processed=" + done);
                        }
                    }
                }
                finally
                {
                    try { doc.Save(new SaveOptions()); log.Info("EXISTING FINAL SAVE processed=" + done); }
                    finally { app.Application.FailuresProcessing -= handler; progress.Finish(); }
                }
            }
            TaskDialog.Show("Existing Manholes", "Processed: " + done + " / " + eligible.Count +
                "\nComplete: " + complete + "\nNeeds review: " + review +
                "\nSaved in the current RVT.\nReport: " + report);
        }
    }
}
