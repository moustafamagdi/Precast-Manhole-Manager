using System;
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
        private static void MakeAllReview3Ds(UIApplication app, DiagnosticLogger log)
        {
            var doc = app.ActiveUIDocument.Document;
            if (doc.IsReadOnly || doc.IsLinked || doc.IsModifiable || doc.IsDetached || doc.IsModelInCloud || string.IsNullOrWhiteSpace(doc.PathName))
                throw new InvalidOperationException("Open an editable saved local RVT. Review views are saved in the current file.");
            var saved = ManholeReviewRegistry.Load(doc);
            var issues = saved.Where(x => x.Status == "OPEN").GroupBy(x => x.FoundationUniqueId)
                .Select(x => x.First()).Where(x => doc.GetElement(x.FoundationUniqueId) != null)
                .OrderBy(x => x.FoundationId).ToList();
            if (issues.Count == 0)
            {
                TaskDialog.Show("Review 3Ds", "No recorded OPEN issues for existing foundations. Run Clean Scan to refresh geometry issues, or run the relevant opening/dimension/repair action to record its results.");
                return;
            }
            if (!new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Any(x => x.IsTemplate && x.Name.Equals("MH_3D", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Load the MH_3D template first.");
            var ask = new TaskDialog("Review 3Ds") {
                MainInstruction = "Create / update " + issues.Count + " review 3D views?",
                MainContent = "Uses recorded OPEN issues for this model. Existing review views are reused. Geometry, openings and sheets are not changed. Each view uses MH_3D and a 350 mm margin.\nSaved in the current RVT at checkpoints and completion. No synchronize.\nRun Clean Scan first if the recorded geometry issues are out of date.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (ask.Show() != TaskDialogResult.Yes) return;
            int done = 0, success = 0, failed = 0;
            var progress = new BatchProgressWindow(app.MainWindowHandle);
            string report = Path.ChangeExtension(log.LogPath, ".review3d.csv");
            DateTime lastSave = DateTime.Now;
            using (var writer = new StreamWriter(report, false, new UTF8Encoding(true)) { AutoFlush = true })
            {
                writer.WriteLine("FoundationId,Manhole,ViewId,ViewName,Status,Reason");
                try
                {
                    progress.Start();
                    foreach (var issue in issues)
                    {
                        progress.Update(done, issues.Count, "Review 3D - " + issue.FoundationId);
                        if (progress.CancelRequested) break;
                        var foundation = doc.GetElement(issue.FoundationUniqueId);
                        int oldId = issue.ViewId;
                        string oldName = issue.ViewName;
                        string name = ManholeIdentityStore.Read(foundation) ?? ("Foundation " + issue.FoundationId);
                        try
                        {
                            var footprint = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
                            using (var tx = new Transaction(doc, "HATCO - Review 3D " + name))
                            {
                                tx.Start(); TransactionFailureHandling.Configure(tx, log);
                                if (!(doc.GetElement(new ElementId(issue.ViewId)) is View3D))
                                {
                                    var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                                        .FirstOrDefault(v => !v.IsTemplate && (v.Name == "MH_REVIEW_" + foundation.Id.IntegerValue.ToString("D7") ||
                                            (v.Name.Contains(" - REVIEW - ") && v.Name.EndsWith(" - " + foundation.Id.IntegerValue))));
                                    if (existing != null) issue.ViewId = existing.Id.IntegerValue;
                                }
                                var view = ManholeReviewViewService.CreateOrUpdate(doc, foundation, footprint, issue, 350, log);
                                string label = name + " - REVIEW - " + (issue.Severity ?? "ISSUE") + " - " + foundation.Id.IntegerValue;
                                foreach (char c in new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', ':', '\\', '/' }) label = label.Replace(c, '_');
                                view.Name = label;
                                issue.ViewName = view.Name;
                                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Review view commit rejected.");
                            }
                            success++;
                            writer.WriteLine(issue.FoundationId + "," + ReviewCsv(name) + "," + issue.ViewId + "," + ReviewCsv(issue.ViewName) + ",READY," + ReviewCsv(issue.Reason));
                        }
                        catch (Exception ex)
                        {
                            issue.ViewId = oldId; issue.ViewName = oldName; failed++;
                            log.Error("REVIEW 3D FAILED Foundation=" + issue.FoundationId, ex);
                            writer.WriteLine(issue.FoundationId + "," + ReviewCsv(name) + ",,,FAILED," + ReviewCsv(ex.Message));
                        }
                        done++;
                        if (done % 10 == 0 || (DateTime.Now - lastSave).TotalMinutes >= 5)
                        {
                            doc.Save(new SaveOptions());
                            ManholeReviewRegistry.Save(doc, saved); lastSave = DateTime.Now;
                        }
                    }
                }
                finally
                {
                    try
                    {
                        doc.Save(new SaveOptions());
                        ManholeReviewRegistry.Save(doc, saved);
                        ManholeReviewRegistry.ExportReadableCsv(doc, saved);
                    }
                    finally { progress.Finish(); }
                }
            }
            TaskDialog.Show("Review 3Ds", "Processed: " + done + "/" + issues.Count + "\nReady: " + success +
                "\nFailed: " + failed + "\nSaved in current RVT.\nReport: " + report);
        }

        private static string ReviewCsv(string text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
    }
}
