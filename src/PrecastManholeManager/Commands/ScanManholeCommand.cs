using System;
using System.Linq;
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
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ScanManholeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null)
                return Result.Failed;

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    log.WriteHeader("REVIT CONTEXT");
                    log.Info($"Revit Version: {commandData.Application.Application.VersionName} / {commandData.Application.Application.VersionNumber}");
                    log.Info($"Document: {doc.Title}");
                    log.Info($"Path: {doc.PathName}");
                    log.Info($"IsWorkshared: {doc.IsWorkshared}");
                    log.Info($"Active View: {doc.ActiveView?.Name} ({doc.ActiveView?.Id.IntegerValue})");

                    Reference picked;
                    try
                    {
                        picked = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            new StructuralFoundationFilter(),
                            "Select the Structural Foundation/base of one manhole");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        log.Warn("User cancelled foundation selection.");
                        return Result.Cancelled;
                    }

                    Element foundation = doc.GetElement(picked.ElementId);
                    if (foundation == null)
                        throw new InvalidOperationException("Selected foundation could not be resolved.");

                    var detector = new ManholeDetectionService(doc, log);
                    var manhole = detector.Detect(foundation);

                    if (!manhole.IsValid)
                    {
                        TaskDialog.Show(
                            "Precast Manhole Manager",
                            $"Phase 1 scan stopped. Could not resolve exactly four manhole walls.\n\n" +
                            $"Candidate walls: {manhole.CandidateWallIds.Count}\n" +
                            $"Detected walls: {manhole.Walls.Count}\n" +
                            $"Details were written to:\n{log.LogPath}");
                        return Result.Succeeded;
                    }

                    var scanner = new MepPenetrationScanner(doc, log);
                    var penetrations = scanner.Scan(manhole);
                    string csvPath = CsvExporter.Export(penetrations);

                    log.WriteHeader("SUMMARY");
                    log.Info($"Foundation: {foundation.Id.IntegerValue}");
                    log.Info($"Walls: {string.Join(", ", manhole.Walls.Select(w => $"W{w.Number}={w.Wall.Id.IntegerValue}"))}");
                    log.Info($"Penetrations: {penetrations.Count}");
                    log.Info($"CSV: {csvPath}");
                    if (!string.IsNullOrWhiteSpace(manhole.Warning))
                        log.Warn(manhole.Warning);

                    string wallSummary = string.Join(" | ",
                        manhole.Walls.Select(w => $"W{w.Number}:{w.Wall.Id.IntegerValue}"));

                    log.WriteHeader("PHASE 2 PREVIEW");
                    log.Info("Opening preview launched.");
                    log.Info("Default clearance per side: 50 mm.");
                    log.Info("No Revit model elements were modified.");

                    OpeningPreviewWindow.ShowModeless(
                        doc,
                        foundation.Id.IntegerValue,
                        wallSummary,
                        manhole.Walls.Select(w => w.Wall.Id.IntegerValue).ToList(),
                        penetrations);

                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    log.Error("Unhandled command exception.", ex);
                    message = ex.Message;

                    TaskDialog.Show(
                        "Precast Manhole Manager",
                        $"The scan failed.\n\n{ex.Message}\n\nDiagnostic log:\n{log.LogPath}");

                    return Result.Failed;
                }
            }
        }
    }
}
