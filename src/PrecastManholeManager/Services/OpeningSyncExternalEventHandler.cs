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
        public int Adopted { get; set; }
        public int Removed { get; set; }
        public int Failed { get; set; }
        public string LogPath { get; set; }

        public override string ToString()
        {
            return $"Created: {Created} | Updated: {Updated} | Unchanged: {Unchanged} | Adopted: {Adopted} | Removed: {Removed} | Failed: {Failed}";
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

                    log.Info($"Request document: Title='{doc.Title}', Path='{doc.PathName}', IsReadOnly={doc.IsReadOnly}, IsModifiable={doc.IsModifiable}");

                    if (activeDoc == null)
                    {
                        log.Warn("ActiveUIDocument is null during ExternalEvent. Proceeding with the valid request document.");
                    }
                    else
                    {
                        log.Info($"Active document: Title='{activeDoc.Title}', Path='{activeDoc.PathName}'");

                        bool sameDocument =
                            object.ReferenceEquals(activeDoc, doc) ||
                            activeDoc.Equals(doc) ||
                            (
                                string.Equals(activeDoc.Title, doc.Title, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(activeDoc.PathName ?? string.Empty, doc.PathName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                            );

                        if (!sameDocument)
                        {
                            log.Warn(
                                "Active Revit document differs from the document that created the preview. " +
                                "The sync will still target the original valid document stored by the preview.");
                        }
                    }

                    if (doc.IsReadOnly)
                        throw new InvalidOperationException("The Revit document is read-only and cannot be modified.");

                    if (doc.IsLinked)
                        throw new InvalidOperationException("The target document is a linked document and cannot be modified.");

                    HashSet<int> wallIds = new HashSet<int>(request.ManholeWallIds);
                    log.Info($"Document: {doc.Title}");
                    log.Info($"Manhole walls: {string.Join(", ", wallIds.OrderBy(x => x))}");
                    log.Info($"Accepted penetration records: {request.AcceptedRecords.Count}");

                    var existing = CollectManagedOpenings(doc, wallIds, log);
                    var unmanaged = CollectUnmanagedOpenings(doc, wallIds, log);

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
                                    if (record.AdoptExistingOpening &&
                                        record.ExistingOpeningId > 0 &&
                                        string.Equals(record.ExistingOpeningStatus, "EXISTING SUFFICIENT", StringComparison.Ordinal))
                                    {
                                        Opening manual = doc.GetElement(new ElementId(record.ExistingOpeningId)) as Opening;
                                        if (manual == null)
                                            throw new InvalidOperationException($"Manual opening {record.ExistingOpeningId} was not found.");

                                        if (manual.Host == null || manual.Host.Id.IntegerValue != record.HostWallId)
                                            throw new InvalidOperationException($"Manual opening {record.ExistingOpeningId} is not hosted by wall {record.HostWallId}.");

                                        ManagedOpeningData alreadyManaged;
                                        if (OpeningStorageService.TryRead(manual, out alreadyManaged))
                                            throw new InvalidOperationException($"Opening {record.ExistingOpeningId} is already managed.");

                                        OpeningStorageService.WriteAdoptedManual(manual, record);
                                        OpeningAdoptionStorageService.MarkAdoptedManual(manual);
                                        unmanaged.Remove(manual);

                                        result.Adopted++;
                                        log.Info(
                                            $"ADOPTED MANUAL Opening={manual.Id.IntegerValue} Wall={record.HostWallId} " +
                                            $"Key='{record.SourceKey}' Existing={record.ExistingOpeningWidthMm:0.#}x{record.ExistingOpeningHeightMm:0.#}mm " +
                                            $"Required={record.CutWidthMm:0.#}x{record.CutHeightMm:0.#}mm");
                                        continue;
                                    }

                                    Opening orphan = FindMatchingUnmanagedOpening(unmanaged, record);
                                    if (orphan != null)
                                    {
                                        OpeningStorageService.Write(orphan, record);
                                        unmanaged.Remove(orphan);

                                        result.Updated++;
                                        log.Info(
                                            $"ADOPTED ExistingOpening={orphan.Id.IntegerValue} Wall={record.HostWallId} " +
                                            $"Key='{record.SourceKey}' NativeCut={record.CutWidthMm:0.#}x{record.CutHeightMm:0.#}mm");
                                    }
                                    else
                                    {
                                        using (var sub = new SubTransaction(doc))
                                        {
                                            sub.Start();

                                            Opening created = CreateOpening(doc, record);
                                            OpeningStorageService.Write(created, record);

                                            sub.Commit();

                                            result.Created++;
                                            log.Info(
                                                $"CREATED Opening={created.Id.IntegerValue} Wall={record.HostWallId} " +
                                                $"Key='{record.SourceKey}' SourceShape='{record.Shape}' " +
                                                $"NativeCut={record.CutWidthMm:0.#}x{record.CutHeightMm:0.#}mm");
                                        }
                                    }
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

        private static List<Opening> CollectUnmanagedOpenings(
            Document doc,
            HashSet<int> wallIds,
            DiagnosticLogger log)
        {
            var list = new List<Opening>();

            foreach (Opening opening in new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>())
            {
                if (opening.Host == null || !wallIds.Contains(opening.Host.Id.IntegerValue))
                    continue;

                ManagedOpeningData data;
                if (OpeningStorageService.TryRead(opening, out data))
                    continue;

                list.Add(opening);
            }

            log.Info($"Unmanaged openings on selected manhole walls: {list.Count}");
            return list;
        }

        private static Opening FindMatchingUnmanagedOpening(
            IEnumerable<Opening> openings,
            PenetrationRecord record)
        {
            const double centerToleranceMm = 5.0;
            const double sizeToleranceMm = 5.0;

            foreach (Opening opening in openings)
            {
                if (opening.Host == null || opening.Host.Id.IntegerValue != record.HostWallId)
                    continue;

                if (!opening.IsRectBoundary || opening.BoundaryRect == null || opening.BoundaryRect.Count < 2)
                    continue;

                XYZ p0 = opening.BoundaryRect[0];
                XYZ p1 = opening.BoundaryRect[1];

                XYZ center = (p0 + p1) * 0.5;
                double centerXmm = UnitUtil.FtToMm(center.X);
                double centerYmm = UnitUtil.FtToMm(center.Y);
                double centerZmm = UnitUtil.FtToMm(center.Z);

                double horizontalFt = Math.Sqrt(
                    Math.Pow(p1.X - p0.X, 2) +
                    Math.Pow(p1.Y - p0.Y, 2));

                double widthMm = UnitUtil.FtToMm(horizontalFt);
                double heightMm = UnitUtil.FtToMm(Math.Abs(p1.Z - p0.Z));

                bool centerMatches =
                    Math.Abs(centerXmm - record.Xmm) <= centerToleranceMm &&
                    Math.Abs(centerYmm - record.Ymm) <= centerToleranceMm &&
                    Math.Abs(centerZmm - record.Zmm) <= centerToleranceMm;

                bool sizeMatches =
                    Math.Abs(widthMm - record.CutWidthMm) <= sizeToleranceMm &&
                    Math.Abs(heightMm - record.CutHeightMm) <= sizeToleranceMm;

                if (centerMatches && sizeMatches)
                    return opening;
            }

            return null;
        }

        private static bool Matches(ManagedOpeningData data, PenetrationRecord record)
        {
            const double geometryToleranceMm = 1.0;

            bool sourcePositionMatches =
                data.HostWallId == record.HostWallId &&
                Math.Abs(data.Xmm - record.Xmm) <= geometryToleranceMm &&
                Math.Abs(data.Ymm - record.Ymm) <= geometryToleranceMm &&
                Math.Abs(data.Zmm - record.Zmm) <= geometryToleranceMm;

            if (!sourcePositionMatches)
                return false;

            if (data.AdoptedManual)
            {
                const double adoptedSizeToleranceMm = 20.0;
                return data.CutWidthMm + adoptedSizeToleranceMm >= record.CutWidthMm &&
                       data.CutHeightMm + adoptedSizeToleranceMm >= record.CutHeightMm;
            }

            return Math.Abs(data.CutWidthMm - record.CutWidthMm) <= geometryToleranceMm &&
                   Math.Abs(data.CutHeightMm - record.CutHeightMm) <= geometryToleranceMm;
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
