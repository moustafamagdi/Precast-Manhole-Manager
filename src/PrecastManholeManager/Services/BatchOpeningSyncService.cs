using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class BatchOpeningSyncResult
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Removed { get; set; }
        public int Review { get; set; }
        public int Failed { get; set; }
    }

    internal static class BatchOpeningSyncService
    {
        public static BatchOpeningSyncResult Sync(
            Document doc,
            IList<int> wallIds,
            IList<PenetrationRecord> records,
            DiagnosticLogger log)
        {
            var result = new BatchOpeningSyncResult();
            HashSet<int> walls = new HashSet<int>(wallIds ?? new List<int>());
            List<ManagedEntry> existing = CollectManaged(doc, walls);
            Dictionary<string, ManagedEntry> byKey = existing
                .GroupBy(x => x.Data.SourceKey)
                .ToDictionary(g => g.Key, g => g.First());

            List<PenetrationRecord> desired = (records ?? new List<PenetrationRecord>())
                .Where(r => r.Accepted &&
                    r.ExistingOpeningStatus != "EXISTING SUFFICIENT" &&
                    r.ExistingOpeningStatus != "EXISTING TOO SMALL")
                .ToList();

            HashSet<string> desiredKeys = new HashSet<string>(
                desired.Select(x => x.SourceKey), StringComparer.Ordinal);

            foreach (PenetrationRecord record in desired)
            {
                try
                {
                    ManagedEntry current;
                    if (byKey.TryGetValue(record.SourceKey, out current))
                    {
                        if (Matches(current.Data, record))
                        {
                            result.Unchanged++;
                            continue;
                        }

                        if (current.Data.AdoptedManual)
                        {
                            result.Review++;
                            log?.Warn("Batch review: adopted manual opening geometry changed. Opening=" + current.Opening.Id.IntegerValue);
                            continue;
                        }

                        string fitReason;
                        if (!OpeningFitValidationService.TryValidate(doc, record, out fitReason))
                        {
                            result.Review++;
                            log?.Warn(
                                "BATCH REVIEW source " + record.LinkedElementId +
                                " on Wall " + record.HostWallId +
                                ": " + fitReason +
                                " Existing managed opening was preserved.");
                            continue;
                        }

                        doc.Delete(current.Opening.Id);
                        Opening updated = CreateOpening(doc, record, log);
                        OpeningStorageService.Write(updated, record);
                        result.Updated++;
                    }
                    else
                    {
                        string fitReason;
                        if (!OpeningFitValidationService.TryValidate(doc, record, out fitReason))
                        {
                            result.Review++;
                            log?.Warn(
                                "BATCH REVIEW source " + record.LinkedElementId +
                                " on Wall " + record.HostWallId +
                                ": " + fitReason);
                            continue;
                        }

                        Opening created = CreateOpening(doc, record, log);
                        OpeningStorageService.Write(created, record);
                        result.Created++;
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    log?.Error("Batch opening sync failed for source " + record.LinkedElementId, ex);
                }
            }

            foreach (ManagedEntry entry in existing)
            {
                if (desiredKeys.Contains(entry.Data.SourceKey))
                    continue;

                if (entry.Data.AdoptedManual)
                {
                    result.Review++;
                    continue;
                }

                RevitLinkInstance sourceLink =
                    doc.GetElement(new ElementId(entry.Data.LinkInstanceId)) as RevitLinkInstance;

                if (sourceLink == null || sourceLink.GetLinkDocument() == null)
                {
                    result.Review++;
                    log?.Warn(
                        "Batch preserved managed opening " + entry.Opening.Id.IntegerValue +
                        " because source link " + entry.Data.LinkInstanceId + " is unloaded/unavailable.");
                    continue;
                }

                try
                {
                    doc.Delete(entry.Opening.Id);
                    result.Removed++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    log?.Error("Batch failed removing stale opening " + entry.Opening.Id.IntegerValue, ex);
                }
            }

            return result;
        }

        private static List<ManagedEntry> CollectManaged(Document doc, HashSet<int> wallIds)
        {
            var result = new List<ManagedEntry>();
            foreach (Opening opening in new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>())
            {
                ManagedOpeningData data;
                if (!OpeningStorageService.TryRead(opening, out data)) continue;
                if (!wallIds.Contains(data.HostWallId)) continue;
                result.Add(new ManagedEntry { Opening = opening, Data = data });
            }
            return result;
        }

        private static bool Matches(ManagedOpeningData data, PenetrationRecord record)
        {
            const double posTol = 1.0;
            const double sizeTol = 1.0;

            bool samePosition =
                data.HostWallId == record.HostWallId &&
                Math.Abs(data.Xmm - record.Xmm) <= posTol &&
                Math.Abs(data.Ymm - record.Ymm) <= posTol &&
                Math.Abs(data.Zmm - record.Zmm) <= posTol;

            if (!samePosition) return false;

            if (data.AdoptedManual)
                return data.CutWidthMm + 20.0 >= record.CutWidthMm &&
                       data.CutHeightMm + 20.0 >= record.CutHeightMm;

            return Math.Abs(data.CutWidthMm - record.CutWidthMm) <= sizeTol &&
                   Math.Abs(data.CutHeightMm - record.CutHeightMm) <= sizeTol;
        }

        private static Opening CreateOpening(Document doc, PenetrationRecord record, DiagnosticLogger log)
        {
            Wall wall = doc.GetElement(new ElementId(record.HostWallId)) as Wall;
            if (wall == null) throw new InvalidOperationException("Host wall not found.");

            LocationCurve location = wall.Location as LocationCurve;
            Curve wallCurve = location?.Curve;
            if (wallCurve == null) throw new InvalidOperationException("Host wall has no usable curve.");

            XYZ tangent = wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0);
            tangent = new XYZ(tangent.X, tangent.Y, 0.0).Normalize();

            XYZ center = new XYZ(
                UnitUtil.MmToFt(record.Xmm),
                UnitUtil.MmToFt(record.Ymm),
                UnitUtil.MmToFt(record.Zmm));

            center = OpeningHostPlaneService.MoveToWallSolidMidPlane(wall, center);

            double halfWidth = UnitUtil.MmToFt(record.CutWidthMm) / 2.0;
            double halfHeight = UnitUtil.MmToFt(record.CutHeightMm) / 2.0;

            XYZ p1 = center - tangent * halfWidth - XYZ.BasisZ * halfHeight;
            XYZ p2 = center + tangent * halfWidth + XYZ.BasisZ * halfHeight;

            OpeningJoinPreparationService.UnjoinConflictingGeometry(doc, wall, record, log);
            return doc.Create.NewOpening(wall, p1, p2);
        }

        private sealed class ManagedEntry
        {
            public Opening Opening { get; set; }
            public ManagedOpeningData Data { get; set; }
        }
    }
}
