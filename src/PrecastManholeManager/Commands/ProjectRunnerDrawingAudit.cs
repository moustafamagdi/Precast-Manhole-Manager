using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Services;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        private static void CheckDrawings(UIApplication app, SimpleManholeItem selected, DiagnosticLogger log)
        {
            var doc = app.ActiveUIDocument.Document;
            var items = selected == null ? SimpleProjectScanService.LoadFast(doc) : new List<SimpleManholeItem> { selected };
            var progress = new BatchProgressWindow(app.MainWindowHandle);
            string summary;
            progress.Start();
            try { summary = RunDrawingAudit(doc, items, log, progress); }
            finally { progress.Finish(); }
            TaskDialog.Show("Drawing checks - no model changes", summary);
        }

        private static string RunDrawingAudit(Document doc, IList<SimpleManholeItem> items, DiagnosticLogger log,
            BatchProgressWindow progress, bool fullAutomation = false)
        {
            // Validate persistence before expensive read-only checks. Never save or edit the RVT here.
            ManholeReviewRegistry.Load(doc);
            string path = Path.ChangeExtension(log.LogPath, ".drawing-checks.csv");
            int done = 0, passed = 0, review = 0, accepted = 0, errors = 0;
            var audit = new DrawingAuditService(doc, log);
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true })
            {
                writer.WriteLine("FoundationId,Manhole,Status,ViewsChecked,Seconds,Details");
                foreach (var item in items)
                {
                    progress.Update(done, items.Count, (fullAutomation ? "Full automation 6/6 - drawing checks: " : "Read-only drawing checks: ") + item.ManholeName);
                    if (progress.CancelRequested) break;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    Element foundation = null;
                    string status, details;
                    int views = 0;
                    try
                    {
                        foundation = Resolve(doc, item);
                        var result = audit.Check(foundation);
                        views = result.ViewsChecked;
                        details = result.Summary;
                        if (result.Issues.Count == 0)
                        {
                            status = "AUTOMATED CHECKS PASSED";
                            // Only clear issues produced by this exact audit; legacy, geometry,
                            // generation errors and manual acceptance remain independent.
                            ManholeReviewRegistry.Resolve(doc, foundation, ReviewDomain.DrawingValidation);
                            passed++;
                        }
                        else
                        {
                            status = "DRAWING REVIEW";
                            ManholeReviewRegistry.Upsert(doc, foundation, details, result.WallIds, "DRAWING CHECK", log,
                                ReviewDomain.DrawingValidation, replace: true);
                            if (ManholeReviewRegistry.Load(doc).Any(x => x.FoundationUniqueId == item.UniqueId && x.Domain == ReviewDomain.DrawingValidation && x.Status == "IGNORED"))
                            { accepted++; status = "MANUALLY ACCEPTED"; }
                            else review++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors++; status = "CHECK ERROR"; details = ex.Message;
                        log.Error("DRAWING CHECK " + item.ManholeName, ex);
                        try { if (foundation != null) ManholeReviewRegistry.Upsert(doc, foundation, "CHECK INCOMPLETE: " + details, null, "DRAWING CHECK ERROR", log, ReviewDomain.DrawingValidation, replace: true); }
                        catch (Exception registryError) { log.Error("Drawing error retained in CSV; register unavailable", registryError); }
                    }
                    done++;
                    writer.WriteLine(item.FoundationId + "," + AuditCsv(item.ManholeName) + "," + status + "," + views + "," +
                        timer.Elapsed.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + AuditCsv(details));
                }
            }
            ManholeReviewRegistry.ExportReadableCsv(doc, ManholeReviewRegistry.Load(doc));
            string summary = "Drawing checks: " + done + "/" + items.Count + "; passed: " + passed + "; review: " + review + "; manually accepted: " + accepted + "; errors: " + errors + "." +
                (done < items.Count ? "\nStopped. Unchecked items retain their previous state." : "") +
                "\nExterior W1-W4 standard. No model geometry, views, dimensions or sheets were changed." +
                "\nAutomated checks do not replace visual approval or a current opening/service check.\nReport: " + path;
            log.Info(summary);
            return summary;
        }

        private static string AuditCsv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}
