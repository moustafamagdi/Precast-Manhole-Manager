using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Selection;
using Hatco.PrecastManholeManager.Services;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class UnifiedOpeningReviewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = data.Application.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (doc == null) return Result.Failed;

            Reference selected;
            try
            {
                selected = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new StructuralFoundationFilter(),
                    "Select a manhole Structural Foundation for read-only unified opening review");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            var settings = new UnifiedReviewSettingsWindow();
            if (settings.ShowDialog() != true)
                return Result.Cancelled;

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    Element foundation = doc.GetElement(selected.ElementId);
                    VirtualFoundationResult footprint =
                        new VirtualFoundationRecoveryService(doc, log)
                            .Analyze(foundation);

                    if (!footprint.Accepted)
                    {
                        TaskDialog.Show("Unified Opening Review",
                            "Virtual footprint needs review: " + footprint.Reason +
                            "\nNo model modifications.\nLog: " + log.LogPath);
                        return Result.Succeeded;
                    }

                    UnifiedOpeningReviewResult review =
                        UnifiedOpeningReviewService.Collect(
                            doc, foundation, footprint, log,
                            settings.ClearanceMm, settings.MaxGapMm,
                            settings.ApproachAngleDeg);

                    // Create the exported snapshot while still in the valid
                    // Revit API command context. The subsequent window is pure WPF.
                    string csv = UnifiedOpeningReviewService.ExportCsv(review);
                    log.Info("Initial unified review CSV: " + csv);
                    UnifiedOpeningReviewWindow.ShowReview(review,
                        foundation.Id.IntegerValue, settings.ClearanceMm,
                        settings.MaxGapMm, settings.ApproachAngleDeg);
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    log.Error("Unified opening review scan failed.", ex);
                    TaskDialog.Show("Unified Opening Review",
                        "Scan failed. No model modifications.\n" +
                        ex.Message + "\nLog: " + log.LogPath);
                    return Result.Failed;
                }
            }
        }
    }
}
