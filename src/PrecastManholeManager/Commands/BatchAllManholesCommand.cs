using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Services;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class BatchAllManholesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Failed;

            List<Element> foundations = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(IsLikelyManholeFoundation)
                .OrderBy(GetSortYDescending)
                .ThenBy(GetSortX)
                .ThenBy(x => x.Id.IntegerValue)
                .ToList();

            if (foundations.Count == 0)
            {
                TaskDialog.Show(
                    "Batch All Manholes",
                    "No foundations with the two configured manhole base type names were found.");
                return Result.Succeeded;
            }

            var settingsWindow = new BatchOptionsWindow(foundations.Count);
            if (settingsWindow.ShowDialog() != true)
                return Result.Cancelled;

            BatchRunOptions options = settingsWindow.SelectedOptions;
            if (!options.PreviewOnly)
            {
                TaskDialog confirm = new TaskDialog("Batch All - APPLY CHANGES");
                confirm.MainInstruction =
                    "Apply managed opening changes to " + foundations.Count + " foundations?";
                confirm.MainContent =
                    "The virtual geometry check and opening audit run first. " +
                    "Walls with edited/unknown profiles or void cuts will be skipped. " +
                    "NO existing profiles or in-place voids will be reset. " +
                    "Use a disposable project copy for the first write test.";
                confirm.CommonButtons = TaskDialogCommonButtons.Yes |
                    TaskDialogCommonButtons.No;
                if (confirm.Show() != TaskDialogResult.Yes)
                    return Result.Cancelled;
            }

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    log.Info("Batch All candidate foundations: " + foundations.Count);
                    foreach (Element e in foundations)
                        log.Info("Candidate Foundation=" + e.Id.IntegerValue + " Name='" + e.Name + "'");

                    log.Info("Experimental batch settings: PreviewOnly=" + options.PreviewOnly +
                        " ClearancePerSideMm=" + options.ClearanceMm +
                        " EdgePolicy=" + options.EdgePolicy +
                        " OpeningAudit=" + options.AuditExistingOpenings);
                    BatchManholeResult result = BatchManholeProcessor.Process(
                        doc, foundations, log, options);

                    TaskDialog.Show(
                        "Batch All Manholes",
                        result + "\n\nLog:\n" + log.LogPath);

                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    log.Error("Batch All failed.", ex);
                    message = ex.Message;

                    TaskDialog.Show(
                        "Batch All Manholes",
                        "Batch failed.\n\n" + ex.Message + "\n\nLog:\n" + log.LogPath);

                    return Result.Failed;
                }
            }
        }

        private static bool IsLikelyManholeFoundation(Element e)
        {
            Element type = e?.Document?.GetElement(e.GetTypeId());
            string typeName = (type?.Name ?? string.Empty).Trim();
            return string.Equals(typeName, "HTC_ST_PRECAST_FN_200mm_MH",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(typeName, "HTC_ST_PRECAST_FN_300mm_MH",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static double GetSortYDescending(Element e)
        {
            BoundingBoxXYZ b = e.get_BoundingBox(null);
            if (b == null) return 0;
            return -((b.Min.Y + b.Max.Y) / 2.0);
        }

        private static double GetSortX(Element e)
        {
            BoundingBoxXYZ b = e.get_BoundingBox(null);
            if (b == null) return 0;
            return (b.Min.X + b.Max.X) / 2.0;
        }
    }
}
