using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ExportManufacturerExcelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
                return Result.Failed;

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    ManufacturerExcelExportResult result =
                        ManufacturerExcelExportService.Export(doc, log);

                    TaskDialog.Show(
                        "Manufacturer Excel Export",
                        "Excel workbook created successfully.\n\n" +
                        "Manholes: " + result.ManholeCount + "\n" +
                        "Openings: " + result.OpeningCount + "\n\n" +
                        "Workbook:\n" + result.WorkbookPath + "\n\n" +
                        "Log:\n" + log.LogPath);

                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    log.Error("Standalone manufacturer Excel export failed.", ex);
                    message = ex.Message;

                    TaskDialog.Show(
                        "Manufacturer Excel Export",
                        "Export failed.\n\n" + ex.Message + "\n\nLog:\n" + log.LogPath);

                    return Result.Failed;
                }
            }
        }
    }
}
