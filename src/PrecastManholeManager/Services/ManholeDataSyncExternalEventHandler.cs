using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class ManholeDataSyncRequest
    {
        public Document Document { get; set; }
        public ManholeDataRecord Data { get; set; }
    }

    internal sealed class ManholeDataSyncResult
    {
        public bool Success { get; set; }
        public int CarrierElementId { get; set; }
        public string ManholeNumber { get; set; }
        public string LogPath { get; set; }
        public string Message { get; set; }
    }

    internal sealed class ManholeDataSyncExternalEventHandler : IExternalEventHandler
    {
        private ManholeDataSyncRequest _request;
        private readonly Action<ManholeDataSyncResult> _completed;

        public ManholeDataSyncExternalEventHandler(Action<ManholeDataSyncResult> completed)
        {
            _completed = completed;
        }

        public void SetRequest(ManholeDataSyncRequest request)
        {
            _request = request;
        }

        public void Execute(UIApplication app)
        {
            var result = new ManholeDataSyncResult();
            ManholeDataSyncRequest request = _request;
            _request = null;

            using (var log = new DiagnosticLogger())
            {
                result.LogPath = log.LogPath;

                try
                {
                    log.WriteHeader("PHASE 4 MANHOLE DATA SYNC");

                    if (request == null || request.Document == null || request.Data == null)
                        throw new InvalidOperationException("Manhole data sync request is empty.");

                    Document doc = request.Document;
                    if (!doc.IsValidObject)
                        throw new InvalidOperationException("The Revit document is no longer valid.");

                    if (doc.IsReadOnly)
                        throw new InvalidOperationException("The Revit document is read-only.");

                    ManholeDataRecord data = request.Data;

                    log.Info($"Document: {doc.Title}");
                    log.Info($"ManholeNumber: {data.ManholeNumber}");
                    log.Info($"FoundationId: {data.FoundationId}");
                    log.Info($"Walls: W1={data.Wall1Id}, W2={data.Wall2Id}, W3={data.Wall3Id}, W4={data.Wall4Id}");
                    log.Info(
                        $"Dimensions mm: Clear14={data.ClearW1W4Mm:0.#}, Clear23={data.ClearW2W3Mm:0.#}, " +
                        $"Outer14={data.OuterW1W4Mm:0.#}, Outer23={data.OuterW2W3Mm:0.#}, " +
                        $"WallHeight={data.WallHeightMm:0.#}, BaseThickness={data.BaseThicknessMm:0.#}");

                    using (var tx = new Transaction(doc, "HATCO - Save Precast Manhole Data"))
                    {
                        tx.Start();

                        DirectShape carrier = ManholeDataCarrierService.CreateOrUpdate(doc, data);

                        tx.Commit();

                        result.Success = true;
                        result.CarrierElementId = carrier.Id.IntegerValue;
                        result.ManholeNumber = data.ManholeNumber;
                        result.Message = "Manhole data carrier saved successfully.";

                        log.Info($"Carrier ElementId={carrier.Id.IntegerValue}");
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                    log.Error("Phase 4 manhole data sync failed.", ex);
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
            return "HATCO Precast Manhole Data Sync";
        }
    }
}
