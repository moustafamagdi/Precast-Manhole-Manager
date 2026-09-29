using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class OpeningSyncRequest
    {
        public Document Document { get; set; }
        public List<int> ManholeWallIds { get; set; } = new List<int>();
        public List<PenetrationRecord> AcceptedRecords { get; set; } = new List<PenetrationRecord>();
    }

    internal sealed class OpeningSyncResult
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Removed { get; set; }
        public int Failed { get; set; }
        public string LogPath { get; set; }

        public override string ToString()
        {
            return $"Created: {Created} | Updated: {Updated} | Unchanged: {Unchanged} | Removed: {Removed} | Failed: {Failed}";
        }
    }

    internal sealed class OpeningSyncExternalEventHandler : IExternalEventHandler
    {
        private OpeningSyncRequest _request;
        private readonly Action<OpeningSyncResult> _completed;

        public OpeningSyncExternalEventHandler(Action<OpeningSyncResult> completed)
        {
            _completed = completed;
        }

        public void SetRequest(OpeningSyncRequest request)
        {
            _request = request;
        }

        public void Execute(UIApplication app)
        {
            var result = new OpeningSyncResult();
            OpeningSyncRequest request = _request;
            _request = null;

            using (var log = new DiagnosticLogger())
            {
                result.LogPath = log.LogPath;

                try
                {
                    log.WriteHeader("PHASE 3 OPENING SYNC");

                    if (request == null || request.Document == null)
                        throw new InvalidOperationException("Opening sync request is empty.");

                    Document doc = request.Document;
                    if (!doc.IsValidObject)
                        throw new InvalidOperationException("The Revit document is no longer valid.");

                    Document activeDoc = app.ActiveUIDocument?.Document;
                    if (activeDoc == null || activeDoc != doc)
                        throw new InvalidOperationException("Return to the Revit document where the preview was created, then run Create / Update again.");

                    HashSet<int> wallIds = new HashSet<int>(request.ManholeWallIds);
                    log.Info($"Document: {doc.Title}");
                    log.Info($"Manhole walls: {string.Join(", ", wallIds.OrderBy(x => x))}");
                    log.Info($"Accepted penetration records: {request.AcceptedRecords.Count}");

                    var existing = CollectManagedOpenings(doc, wallIds, log);
                    var existingByKey = existing
                        .GroupBy(x => x.Data.SourceKey)
                        .ToDictionary(g => g.Key, g => g.First());

                    HashSet<string> desiredKeys = new HashSet<string>(
                        request.AcceptedRecords.Select(r => r.SourceKey),
                        StringComparer.Ordinal);

                    using (var tx = new Transaction(doc, "HATCO - Sync Precast Manhole Openings"))
                    {
                        tx.Start();

                        foreach (PenetrationRecord record in request.AcceptedRecords)
                        {
                            try
                            {
                                ManagedOpeningEntry current;
                                if (existingByKey.TryGetValue(record.SourceKey, out current))
                                {
                                    if (Matches(current.Data, record))
                                    {
                                        result.Unchanged++;
                                        log.Info($"UNCHANGED Opening={current.Opening.Id.IntegerValue} Key='{record.SourceKey}'");
                                        continue;
                                    }

                                    int oldId = current.Opening.Id.IntegerValue;
                                    doc.Delete(current.Opening.Id);

                                    Opening updated = CreateOpening(doc, record);
                                    OpeningStorageService.Write(updated, record);

                                    result.Updated++;
                                    log.Info(
                                        $"UPDATED OldOpening={oldId} NewOpening={updated.Id.IntegerValue} " +
                                        $"Wall={record.HostWallId} Key='{record.SourceKey}' Size={record.CutWidthMm:0.#}x{record.CutHeightMm:0.#}mm");
                                }
                                else
                                {
                                    Opening created = CreateOpening(doc, record);
                                    OpeningStorageService.Write(created, record);

                                    result.Created++;
                                    log.Info(
                                        $"CREATED Opening={created.Id.IntegerValue} Wall={record.HostWallId} " +
                                        $"Key='{record.SourceKey}' SourceShape='{record.Shape}' " +
                                        $"NativeCut={record.CutWidthMm:0.#}x{record.CutHeightMm:0.#}mm");
                                }
                            }
                            catch (Exception ex)
                            {
                                result.Failed++;
                                log.Error(
                                    $"Failed syncing source LinkInstance={record.LinkInstanceId}, Element={record.LinkedElementId}, Wall={record.HostWallId}.",
                                    ex);
                            }
                        }

                        foreach (ManagedOpeningEntry entry in existing)
                        {
                            if (desiredKeys.Contains(entry.Data.SourceKey))
                                continue;

                            try
                            {
                                int id = entry.Opening.Id.IntegerValue;
                                doc.Delete(entry.Opening.Id);
                                result.Removed++;
                                log.Info($"REMOVED Opening={id} Key='{entry.Data.SourceKey}'");
                            }
                            catch (Exception ex)
                            {
                                result.Failed++;
                                log.Error($"Failed removing stale managed opening {entry.Opening.Id.IntegerValue}.", ex);
                            }
                        }

                        tx.Commit();
                    }

                    log.WriteHeader("PHASE 3 SUMMARY");
                    log.Info(result.ToString());
                    log.Info("Round source penetrations currently use a square/rectangular native Revit opening envelope.");
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    log.Error("Phase 3 opening sync failed.", ex);
                }
            }

            try
            {
                _completed?.Invoke(result);
            }
            catch
            {
                // Never allow a UI callback failure to propagate into Revit's external event.
            }
        }

        public string GetName()
        {
            return "HATCO Precast Manhole Opening Sync";
        }

        private static List<ManagedOpeningEntry> CollectManagedOpenings(
            Document doc,
            HashSet<int> wallIds,
            DiagnosticLogger log)
        {
            var list = new List<ManagedOpeningEntry>();

            foreach (Opening opening in new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>())
            {
                ManagedOpeningData data;
                if (!OpeningStorageService.TryRead(opening, out data))
                    continue;

                if (!wallIds.Contains(data.HostWallId))
                    continue;

                list.Add(new ManagedOpeningEntry
                {
                    Opening = opening,
                    Data = data
                });
            }

            log.Info($"Existing managed openings on selected manhole walls: {list.Count}");
            return list;
        }

        private static bool Matches(ManagedOpeningData data, PenetrationRecord record)
        {
            const double geometryToleranceMm = 1.0;

            return data.HostWallId == record.HostWallId &&
                   Math.Abs(data.CutWidthMm - record.CutWidthMm) <= geometryToleranceMm &&
                   Math.Abs(data.CutHeightMm - record.CutHeightMm) <= geometryToleranceMm &&
                   Math.Abs(data.Xmm - record.Xmm) <= geometryToleranceMm &&
                   Math.Abs(data.Ymm - record.Ymm) <= geometryToleranceMm &&
                   Math.Abs(data.Zmm - record.Zmm) <= geometryToleranceMm;
        }

        private static Opening CreateOpening(Document doc, PenetrationRecord record)
        {
            if (record.CutWidthMm <= 0 || record.CutHeightMm <= 0)
                throw new InvalidOperationException("Opening size is unresolved. Review the penetration size before creation.");

            Wall wall = doc.GetElement(new ElementId(record.HostWallId)) as Wall;
            if (wall == null)
                throw new InvalidOperationException($"Host wall {record.HostWallId} was not found.");

            LocationCurve location = wall.Location as LocationCurve;
            Curve wallCurve = location?.Curve;
            if (wallCurve == null)
                throw new InvalidOperationException($"Host wall {record.HostWallId} has no usable location curve.");

            XYZ tangent = wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0);
            tangent = new XYZ(tangent.X, tangent.Y, 0.0);
            if (tangent.GetLength() < 1e-9)
                throw new InvalidOperationException($"Host wall {record.HostWallId} has an invalid horizontal direction.");

            tangent = tangent.Normalize();

            XYZ center = new XYZ(
                UnitUtil.MmToFt(record.Xmm),
                UnitUtil.MmToFt(record.Ymm),
                UnitUtil.MmToFt(record.Zmm));

            double halfWidth = UnitUtil.MmToFt(record.CutWidthMm) / 2.0;
            double halfHeight = UnitUtil.MmToFt(record.CutHeightMm) / 2.0;

            XYZ lowerLeft = center - tangent * halfWidth - XYZ.BasisZ * halfHeight;
            XYZ upperRight = center + tangent * halfWidth + XYZ.BasisZ * halfHeight;

            return doc.Create.NewOpening(wall, lowerLeft, upperRight);
        }

        private sealed class ManagedOpeningEntry
        {
            public Opening Opening { get; set; }
            public ManagedOpeningData Data { get; set; }
        }
    }
}
