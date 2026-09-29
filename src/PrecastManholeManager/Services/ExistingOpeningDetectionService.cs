using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ExistingOpeningDetectionService
    {
        private const double CenterToleranceMm = 20.0;
        private const double SizeToleranceMm = 20.0;

        public static void Apply(
            Document doc,
            IEnumerable<int> manholeWallIds,
            IList<PenetrationRecord> records,
            DiagnosticLogger log)
        {
            if (doc == null || records == null || records.Count == 0)
                return;

            HashSet<int> wallIds = new HashSet<int>(manholeWallIds ?? Enumerable.Empty<int>());

            var managed = new List<OpeningInfo>();
            var manual = new List<OpeningInfo>();

            foreach (Opening opening in new FilteredElementCollector(doc)
                         .OfClass(typeof(Opening))
                         .Cast<Opening>())
            {
                if (opening.Host == null || !wallIds.Contains(opening.Host.Id.IntegerValue))
                    continue;

                OpeningInfo info = TryBuildInfo(opening);
                if (info == null)
                    continue;

                ManagedOpeningData data;
                if (OpeningStorageService.TryRead(opening, out data))
                {
                    info.Managed = data;
                    managed.Add(info);
                }
                else
                {
                    manual.Add(info);
                }
            }

            log?.WriteHeader("EXISTING OPENING DETECTION");
            log?.Info($"Managed openings on manhole walls: {managed.Count}");
            log?.Info($"Manual/unmanaged openings on manhole walls: {manual.Count}");

            foreach (PenetrationRecord record in records)
            {
                OpeningInfo managedMatch = managed.FirstOrDefault(x =>
                    x.Managed != null &&
                    string.Equals(x.Managed.SourceKey, record.SourceKey, StringComparison.Ordinal));

                if (managedMatch != null)
                {
                    record.ExistingOpeningId = managedMatch.Opening.Id.IntegerValue;
                    record.ExistingOpeningWidthMm = managedMatch.WidthMm;
                    record.ExistingOpeningHeightMm = managedMatch.HeightMm;
                    record.ExistingOpeningStatus = "MANAGED";
                    record.AdoptExistingOpening = false;

                    log?.Info(
                        $"MANAGED Source={record.LinkedElementId} W{record.WallNumber} " +
                        $"Opening={record.ExistingOpeningId} Existing={managedMatch.WidthMm:0.#}x{managedMatch.HeightMm:0.#}mm");
                    continue;
                }

                List<OpeningInfo> sameWall = manual
                    .Where(x => x.HostWallId == record.HostWallId)
                    .OrderBy(x => CenterDistanceMm(x, record))
                    .ToList();

                OpeningInfo candidate = sameWall.FirstOrDefault(x =>
                    Math.Abs(x.CenterXmm - record.Xmm) <= CenterToleranceMm &&
                    Math.Abs(x.CenterYmm - record.Ymm) <= CenterToleranceMm &&
                    Math.Abs(x.CenterZmm - record.Zmm) <= CenterToleranceMm);

                if (candidate == null)
                {
                    record.ExistingOpeningStatus = "NONE";
                    record.ExistingOpeningId = 0;
                    continue;
                }

                record.ExistingOpeningId = candidate.Opening.Id.IntegerValue;
                record.ExistingOpeningWidthMm = candidate.WidthMm;
                record.ExistingOpeningHeightMm = candidate.HeightMm;
                record.AdoptExistingOpening = false;

                bool sufficient =
                    candidate.WidthMm + SizeToleranceMm >= record.CutWidthMm &&
                    candidate.HeightMm + SizeToleranceMm >= record.CutHeightMm;

                if (sufficient)
                {
                    record.ExistingOpeningStatus = "EXISTING SUFFICIENT";
                    record.Accepted = false;

                    log?.Info(
                        $"EXISTING SUFFICIENT Source={record.LinkedElementId} W{record.WallNumber} " +
                        $"Opening={candidate.Opening.Id.IntegerValue} Existing={candidate.WidthMm:0.#}x{candidate.HeightMm:0.#}mm " +
                        $"Required={record.CutWidthMm:0.#}x{record.CutHeightMm:0.#}mm");
                }
                else
                {
                    record.ExistingOpeningStatus = "EXISTING TOO SMALL";
                    record.Accepted = false;

                    log?.Warn(
                        $"EXISTING TOO SMALL Source={record.LinkedElementId} W{record.WallNumber} " +
                        $"Opening={candidate.Opening.Id.IntegerValue} Existing={candidate.WidthMm:0.#}x{candidate.HeightMm:0.#}mm " +
                        $"Required={record.CutWidthMm:0.#}x{record.CutHeightMm:0.#}mm");
                }
            }
        }

        private static OpeningInfo TryBuildInfo(Opening opening)
        {
            if (!opening.IsRectBoundary || opening.BoundaryRect == null || opening.BoundaryRect.Count < 2)
                return null;

            XYZ p0 = opening.BoundaryRect[0];
            XYZ p1 = opening.BoundaryRect[1];
            XYZ center = (p0 + p1) * 0.5;

            double horizontalFt = Math.Sqrt(
                Math.Pow(p1.X - p0.X, 2) +
                Math.Pow(p1.Y - p0.Y, 2));

            return new OpeningInfo
            {
                Opening = opening,
                HostWallId = opening.Host.Id.IntegerValue,
                CenterXmm = UnitUtil.FtToMm(center.X),
                CenterYmm = UnitUtil.FtToMm(center.Y),
                CenterZmm = UnitUtil.FtToMm(center.Z),
                WidthMm = UnitUtil.FtToMm(horizontalFt),
                HeightMm = UnitUtil.FtToMm(Math.Abs(p1.Z - p0.Z))
            };
        }

        private static double CenterDistanceMm(OpeningInfo x, PenetrationRecord r)
        {
            double dx = x.CenterXmm - r.Xmm;
            double dy = x.CenterYmm - r.Ymm;
            double dz = x.CenterZmm - r.Zmm;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private sealed class OpeningInfo
        {
            public Opening Opening { get; set; }
            public int HostWallId { get; set; }
            public double CenterXmm { get; set; }
            public double CenterYmm { get; set; }
            public double CenterZmm { get; set; }
            public double WidthMm { get; set; }
            public double HeightMm { get; set; }
            public ManagedOpeningData Managed { get; set; }
        }
    }
}
