using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Selection;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class TestVirtualMepCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (doc == null) return Result.Failed;

            Reference picked;
            try
            {
                picked = uiDoc.Selection.PickObject(ObjectType.Element,
                    new StructuralFoundationFilter(),
                    "Select one manhole foundation: virtual MEP + opening reset AUDIT ONLY");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    Element foundation = doc.GetElement(picked.ElementId);
                    var recovery = new VirtualFoundationRecoveryService(doc, log);
                    VirtualFoundationResult footprint = recovery.Analyze(foundation);
                    if (!footprint.Accepted)
                    {
                        TaskDialog.Show("Virtual MEP Experiment",
                            "Virtual footprint needs review: " + footprint.Reason +
                            "\nNo model changes.\nLog: " + log.LogPath);
                        return Result.Succeeded;
                    }

                    OpeningResetAuditResult audit =
                        OpeningResetAuditService.Audit(doc, footprint.Walls, log);

                    var scanner = new VirtualMepExtensionScanner(doc, log);
                    VirtualMepScanResult scan = scanner.Scan(footprint, 150.0, 15.0);

                    TaskDialog.Show("Virtual MEP + Opening Audit",
                        "READ-ONLY RESULTS\n" +
                        "Wall group: " +
                        string.Join(", ", footprint.Walls.Select(x => x.Id.IntegerValue)) +
                        "\nNearby MEP elements: " + scan.NearbyMep +
                        "\nVirtual endpoint candidates: " + scan.Candidates.Count +
                        "\nNot accepted as virtual: " + scan.DiagnosedNonCandidates +
                        "\nAmbiguous: " + scan.Candidates.Count(x => x.Status == "AMBIGUOUS REVIEW") +
                        "\nMEP links loaded/unavailable: " +
                        scan.LoadedLinks + "/" + scan.UnavailableLinks +
                        "\n\nEXISTING OPENINGS\n" +
                        "Native managed: " + audit.NativeManaged +
                        "\nNative manual: " + audit.NativeUnmanaged +
                        "\nEdited wall profiles: " + audit.ProfileEditedWalls +
                        "\nUnknown profile status: " + audit.ProfileUnknownWalls +
                        "\nVoid cut relationships: " + audit.VoidCutRelations +
                        "\nUnknown void status: " + audit.VoidUnknownWalls +
                        "\n\nALL VIRTUAL RESULTS REQUIRE REVIEW. NO OPENINGS DELETED OR CREATED." +
                        "\n\nCandidates CSV:\n" + scan.CsvPath +
                        "\n\nAll nearby MEP / rejection reasons CSV:\n" +
                        scan.DiagnosticCsvPath +
                        "\n\nDiagnostic log:\n" + log.LogPath);
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    log.Error("Virtual MEP experiment failed.", ex);
                    message = ex.Message;
                    TaskDialog.Show("Virtual MEP Experiment",
                        "Experiment failed. Nothing was modified.\n" +
                        ex.Message + "\nLog: " + log.LogPath);
                    return Result.Failed;
                }
            }
        }
    }
}
