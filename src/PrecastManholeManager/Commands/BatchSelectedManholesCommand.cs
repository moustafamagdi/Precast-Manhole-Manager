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

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    BatchManholeResult result = BatchManholeProcessor.Process(doc, foundations, log);
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
