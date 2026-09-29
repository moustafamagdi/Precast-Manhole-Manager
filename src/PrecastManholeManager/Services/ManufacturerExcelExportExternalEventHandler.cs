using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class ManufacturerExcelExportRequest
    {
        public Document Document { get; set; }
    }

    internal sealed class ManufacturerExcelExportExternalEventHandler : IExternalEventHandler
    {
        private ManufacturerExcelExportRequest _request;
        private readonly Action<ManufacturerExcelExportResult> _completed;

        public ManufacturerExcelExportExternalEventHandler(Action<ManufacturerExcelExportResult> completed)
        {
            _completed = completed;
        }

        public void SetRequest(ManufacturerExcelExportRequest request)
        {
            _request = request;
        }

        public void Execute(UIApplication app)
        {
            ManufacturerExcelExportRequest request = _request;
            _request = null;

            ManufacturerExcelExportResult result = null;

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    if (request == null || request.Document == null)
                        throw new InvalidOperationException("Manufacturer Excel export request is empty.");

                    if (!request.Document.IsValidObject)
                        throw new InvalidOperationException("The Revit document is no longer valid.");

                    result = ManufacturerExcelExportService.Export(request.Document, log);
                    result.LogPath = log.LogPath;
                }
                catch (Exception ex)
                {
                    log.Error("Manufacturer Excel export failed.", ex);
                    result = new ManufacturerExcelExportResult
                    {
                        LogPath = log.LogPath
                    };
                }
            }

            try
            {
                _completed?.Invoke(result);
            }
            catch
            {
            }
        }

        public string GetName()
        {
            return "HATCO Precast Manhole Manufacturer Excel Export";
        }
    }
}
