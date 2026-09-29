using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class ManufacturerExportRequest
    {
        public Document Document { get; set; }
    }

    internal sealed class ManufacturerExportExternalEventHandler : IExternalEventHandler
    {
        private ManufacturerExportRequest _request;
        private readonly Action<ManufacturerExportResult> _completed;

        public ManufacturerExportExternalEventHandler(Action<ManufacturerExportResult> completed)
        {
            _completed = completed;
        }

        public void SetRequest(ManufacturerExportRequest request)
        {
            _request = request;
        }

        public void Execute(UIApplication app)
        {
            ManufacturerExportRequest request = _request;
            _request = null;

            ManufacturerExportResult result = null;

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    if (request == null || request.Document == null)
                        throw new InvalidOperationException("Manufacturer export request is empty.");

                    Document doc = request.Document;
                    if (!doc.IsValidObject)
                        throw new InvalidOperationException("The Revit document is no longer valid.");

                    result = ManufacturerExportService.ExportAll(doc, log);
                    result.LogPath = log.LogPath;
                }
                catch (Exception ex)
                {
                    log.Error("Phase 5 manufacturer export failed.", ex);

                    result = new ManufacturerExportResult
                    {
                        ManholeCount = 0,
                        OpeningCount = 0,
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
                // UI callback errors must not propagate to Revit.
            }
        }

        public string GetName()
        {
            return "HATCO Precast Manhole Manufacturer Export";
        }
    }
}
