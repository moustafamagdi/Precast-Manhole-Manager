using System;
using System.Collections.Generic;
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
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class BatchSelectedManholesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return Result.Failed;

            IList<Reference> picked;
            try
            {
                picked = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new StructuralFoundationFilter(),
                    "Select manhole Structural Foundations, then click Finish");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            List<Element> foundations = picked
                .Select(r => doc.GetElement(r.ElementId))
                .Where(x => x != null)
                .Distinct(new ElementIdComparer())
                .OrderBy(x => x.Id.IntegerValue)
                .ToList();

            if (foundations.Count == 0)
            {
                TaskDialog.Show("Precast Manhole Manager", "No Structural Foundations were selected.");
                return Result.Succeeded;
            }

            var settingsWindow = new BatchOptionsWindow(foundations.Count);
            if (settingsWindow.ShowDialog() != true)
                return Result.Cancelled;

            BatchRunOptions options = settingsWindow.SelectedOptions;
            if (!options.PreviewOnly)
            {
                TaskDialog confirm = new TaskDialog("Batch Selected - APPLY CHANGES");
                confirm.MainInstruction = "Apply managed opening changes to " +
                    foundations.Count + " selected manholes?";
                confirm.MainContent =
                    "No profile reset or in-place void removal will be performed. " +
                    "Use a test project copy first.";
                confirm.CommonButtons =
                    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                if (confirm.Show() != TaskDialogResult.Yes)
                    return Result.Cancelled;
            }

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    log.Info("Experimental Batch Selected settings: PreviewOnly=" +
                        options.PreviewOnly + " ClearanceMm=" + options.ClearanceMm +
                        " EdgePolicy=" + options.EdgePolicy);
                    BatchManholeResult result = BatchManholeProcessor.Process(
                        doc, foundations, log, options);
                    TaskDialog.Show(
                        "Batch Selected Manholes",
                        result + "\n\nLog:\n" + log.LogPath);
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    log.Error("Batch Selected failed.", ex);
                    message = ex.Message;
                    TaskDialog.Show(
                        "Batch Selected Manholes",
                        "Batch failed.\n\n" + ex.Message + "\n\nLog:\n" + log.LogPath);
                    return Result.Failed;
                }
            }
        }

        private sealed class ElementIdComparer : IEqualityComparer<Element>
        {
            public bool Equals(Element x, Element y)
            {
                return x?.Id.IntegerValue == y?.Id.IntegerValue;
            }

            public int GetHashCode(Element obj)
            {
                return obj?.Id.IntegerValue ?? 0;
            }
        }
    }
}
