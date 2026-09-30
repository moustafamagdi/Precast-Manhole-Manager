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
    public sealed class TestCleanSyncCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData input,
            ref string message, ElementSet elements)
        {
            UIDocument uidoc = input.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return Result.Failed;

            Reference selection;
            try
            {
                selection = uidoc.Selection.PickObject(
                    ObjectType.Element, new StructuralFoundationFilter(),
                    "Select ONE test-copy manhole foundation to PREVIEW cleanup/sync");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    Element foundation = doc.GetElement(selection.ElementId);
                    VirtualFoundationResult footprint =
                        new VirtualFoundationRecoveryService(doc, log)
                            .Analyze(foundation);
                    if (!footprint.Accepted)
                    {
                        TaskDialog.Show("Test Clean & Sync",
                            "Foundation footprint needs review: " +
                            footprint.Reason + "\nNo changes.\nLog: " + log.LogPath);
                        return Result.Succeeded;
                    }

                    var settings = new UnifiedReviewSettingsWindow();
                    if (settings.ShowDialog() != true) return Result.Cancelled;

                    UnifiedOpeningReviewResult review =
                        UnifiedOpeningReviewService.Collect(
                            doc, foundation, footprint, log,
                            settings.ClearanceMm, settings.MaxGapMm,
                            settings.ApproachAngleDeg);

                    CleanSyncPlan plan = CleanSyncPlanService.Build(
                        doc, foundation.Id.IntegerValue, footprint, review, log);
                    string planCsv = UnifiedOpeningReviewService.ExportCsv(review);
                    log.Info("Cleanup preview review CSV: " + planCsv);
                    if (!string.IsNullOrWhiteSpace(plan.BlockReason) ||
                        plan.InPlaceCutterCount > 0)
                    {
                        try
                        {
                            ManholeReviewRegistry.Upsert(doc, foundation,
                                !string.IsNullOrWhiteSpace(plan.BlockReason)
                                    ? plan.BlockReason
                                    : "In-place cutter(s): " + string.Join(",",
                                        plan.InPlaceCutterWallIds.Keys) +
                                      "; cleanup requires verification",
                                plan.WallIds, "CLEANUP REVIEW", log);
                        }
                        catch (Exception registryError)
                        {
                            log.Warn("Issue register unavailable: " +
                                registryError.Message);
                        }
                    }

                    var confirmation = new CleanSyncConfirmationWindow(
                        plan, review, log.LogPath);
                    if (confirmation.ShowDialog() != true)
                    {
                        log.Info("Cleanup remained PREVIEW ONLY. No model changes.");
                        TaskDialog.Show("Test Clean & Sync",
                            "Preview completed; NO MODEL CHANGES.\n" +
                            "Review CSV: " + planCsv +
                            "\nLog: " + log.LogPath);
                        return Result.Succeeded;
                    }

                    // Re-validate that target IDs and audit have not changed.
                    // No other model edits should occur while the modal dialog
                    // is open; readback guards remain in the atomic service.
                    log.Warn("User explicitly approved destructive test on a copy.");
                    CleanSyncApplyResult result =
                        CleanSyncAtomicService.Apply(
                            doc, plan, confirmation.ApplyOptions, log);
                    TaskDialog.Show("Test Clean & Sync", result +
                        "\n\nReview CSV: " + planCsv +
                        "\nLog: " + log.LogPath);

                    if (!result.Committed)
                    {
                        try
                        {
                            ManholeReviewRegistry.Upsert(doc, foundation,
                                result.Error ?? "Cleanup rolled back",
                                plan.WallIds, "BLOCKED", log);
                        }
                        catch (Exception registryError)
                        {
                            log.Warn("Issue register unavailable: " +
                                registryError.Message);
                        }
                        message = result.Error ?? "Cleanup transaction rolled back.";
                        return Result.Failed;
                    }
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    log.Error("Test cleanup command failed. No successful transaction.", ex);
                    message = ex.Message;
                    TaskDialog.Show("Test Clean & Sync",
                        "Operation stopped. Review model and log.\n" +
                        ex.Message + "\nLog: " + log.LogPath);
                    return Result.Failed;
                }
            }
        }
    }
}
