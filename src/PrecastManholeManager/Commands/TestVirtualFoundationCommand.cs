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
    public sealed class TestVirtualFoundationCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (doc == null) return Result.Failed;

            Reference selection;
            try
            {
                selection = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new StructuralFoundationFilter(),
                    "Select a cropped or complete manhole foundation to TEST virtually (read-only)");
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
                    var service = new VirtualFoundationRecoveryService(doc, log);
                    VirtualFoundationResult result = service.Analyze(foundation);

                    string summary = result.Accepted
                        ? "VIRTUAL FOOTPRINT FOUND" + Environment.NewLine +
                          "Walls: " + string.Join(", ", result.Walls.Select(x => x.Id.IntegerValue)) +
                          Environment.NewLine +
                          "Clear dimensions: " +
                          UnitUtil.FtToMm(result.ClearAlongAFt).ToString("0.#") + " x " +
                          UnitUtil.FtToMm(result.ClearAlongBFt).ToString("0.#") + " mm" +
                          Environment.NewLine +
                          "Outer dimensions: " +
                          UnitUtil.FtToMm(result.OuterAlongAFt).ToString("0.#") + " x " +
                          UnitUtil.FtToMm(result.OuterAlongBFt).ToString("0.#") + " mm" +
                          Environment.NewLine +
                          "Cropped bbox to virtual center shift: " +
                          result.CenterShiftMm.ToString("0.#") + " mm"
                        : "NEEDS REVIEW: " + result.Reason;

                    TaskDialog.Show(
                        "Virtual Foundation (Experimental)",
                        summary + Environment.NewLine + Environment.NewLine +
                        "Candidate combinations: " + result.TestedCombinations +
                        " | Valid: " + result.ValidCombinations +
                        Environment.NewLine + Environment.NewLine +
                        "NO MODEL CHANGES WERE MADE." +
                        Environment.NewLine + "Diagnostic log:" +
                        Environment.NewLine + log.LogPath);

                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    log.Error("Virtual foundation experiment failed.", ex);
                    message = ex.Message;
                    TaskDialog.Show("Virtual Foundation",
                        "Experiment failed; model unchanged." + Environment.NewLine +
                        ex.Message + Environment.NewLine + log.LogPath);
                    return Result.Failed;
                }
            }
        }
    }
}
