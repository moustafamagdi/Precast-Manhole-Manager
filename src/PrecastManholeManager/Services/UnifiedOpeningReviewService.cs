using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    // One row per actual crossing or proposed virtual endpoint. Read-only.
    internal sealed class UnifiedOpeningReviewRow
    {
        public string Detection { get; set; }
        public string Wall { get; set; }
        public int WallId { get; set; }
        public int SourceId { get; set; }
        public string LinkName { get; set; }
        public string Service { get; set; }
        public string SourceSize { get; set; }
        public double GapMm { get; set; }
        public double SlopePercent { get; set; }
        public double ApproachAngleDeg { get; set; }
        public string Existing { get; set; }
        public string WallProfile { get; set; }
        public string VoidCuts { get; set; }
        public string OpeningSize { get; set; }
        public string VerticalReference { get; set; }
        public string Status { get; set; }
        public string Notes { get; set; }
        public PenetrationRecord Source { get; set; }
        public bool IsVirtual { get; set; }
        public bool EndpointQualified { get; set; }

        internal void Evaluate(Document doc, double clearanceMm,
            OpeningResetAuditResult audit)
        {
            Source.ClearanceMm = clearanceMm;
            // No edits to source element or wall. Keep original hit coordinates.
            Source.CutWidthOverrideMm = 0;
            Source.CutHeightOverrideMm = 0;
            Source.FittedCenterXmm = null;
            Source.FittedCenterYmm = null;
            Source.FittedCenterZmm = null;

            DuctEdgeOpeningService.Apply(doc, Source);
            if (!Source.EdgeAligned) CornerOpeningService.Fit(doc, Source);
            OpeningSize = Source.CutWidthMm > 0 && Source.CutHeightMm > 0
                ? Source.CutWidthMm.ToString("0.#") + " x " +
                  Source.CutHeightMm.ToString("0.#") + " mm"
                : "UNRESOLVED";

            VerticalReference = Source.CutHeightMm > 0
                ? (Source.Zmm - Source.CutHeightMm * 0.5 -
                   BaseTopZmm).ToString("0.#") + " mm"
                : "UNRESOLVED";

            WallOpeningAuditInfo wallAudit;
            if (audit.WallDetails.TryGetValue(WallId, out wallAudit))
            {
                WallProfile = wallAudit.ProfileStatus;
                VoidCuts = wallAudit.VoidCutCount < 0
                    ? "UNKNOWN" : wallAudit.VoidCutCount.ToString();
            }
            else
            {
                WallProfile = "UNKNOWN";
                VoidCuts = "UNKNOWN";
            }

            var flags = new List<string>();
            if (wallAudit == null || wallAudit.NeedsReview)
                flags.Add("EXISTING WALL MODIFICATION - REVIEW");
            if (Source.ExistingOpeningStatus == "EXISTING SUFFICIENT")
                flags.Add("EXISTING NATIVE OPENING - NO NEW CUT");
            else if (Source.ExistingOpeningStatus == "EXISTING TOO SMALL")
                flags.Add("NATIVE OPENING TOO SMALL");
            else if (Source.ExistingOpeningStatus == "MANAGED")
                flags.Add("MANAGED OPENING - COMPARE ONLY");

            if (Source.CutWidthMm <= 0 || Source.CutHeightMm <= 0)
            {
                flags.Add("UNRESOLVED MEP SIZE");
            }
            else
            {
                string fitReason;
                if (!OpeningFitValidationService.TryValidate(doc, Source, out fitReason))
                    flags.Add("WALL FIT REVIEW: " + fitReason);
            }

            if (IsVirtual && !EndpointQualified)
            {
                flags.Add("VIRTUAL EXTENSION - APPROVAL REQUIRED");
                if (Math.Abs(SlopePercent) > 0.1 || ApproachAngleDeg > 5)
                    flags.Add("SLOPED/SKEWED SOURCE - verify projected opening envelope");
            }

            Status = flags.Count == 0 ? "ACTUAL FIT PREVIEW" : "REVIEW";
            Notes = string.Join(" | ", flags);
            if (Source.EdgeAligned) Notes += " | DUCT SITE ADJUSTMENT: move along wall " + Source.EdgeShiftMm.ToString("0.#") + " mm; full opening starts at wall end";
            if (Source.CornerStartAllowed || Source.CornerEndAllowed) Notes += " | VERIFIED SHARED CORNER - projected opening clipped to host wall end";
            if (IsVirtual && Detection == "INSIDE WALL")
                Notes += " | Endpoint already enters wall thickness.";
        }

        internal double BaseTopZmm { get; set; }
    }

    internal sealed class UnifiedOpeningReviewResult
    {
        public List<UnifiedOpeningReviewRow> Rows { get; } =
            new List<UnifiedOpeningReviewRow>();
        public OpeningResetAuditResult Audit { get; set; }
        public VirtualMepScanResult VirtualScan { get; set; }
        public int ActualCount { get; set; }
        public int VirtualCount { get; set; }
        public int DuplicatesSkipped { get; set; }
        public string LogPath { get; set; }
    }

    internal static class UnifiedOpeningReviewService
    {
        public static UnifiedOpeningReviewResult Collect(Document doc,
            Element foundation, VirtualFoundationResult footprint,
            DiagnosticLogger log, double clearanceMm,
            double maxVirtualGapMm, double maxApproachAngleDeg)
        {
            using (LinkedMepScanCache.BeginIfNeeded())
                return CollectIndexed(doc, foundation, footprint, log, clearanceMm, maxVirtualGapMm, maxApproachAngleDeg);
        }

        private static UnifiedOpeningReviewResult CollectIndexed(Document doc,
            Element foundation, VirtualFoundationResult footprint,
            DiagnosticLogger log, double clearanceMm,
            double maxVirtualGapMm, double maxApproachAngleDeg)
        {
            if (doc == null || foundation == null || footprint == null ||
                !footprint.Accepted)
                throw new InvalidOperationException("A validated virtual footprint is required.");

            var result = new UnifiedOpeningReviewResult { LogPath = log.LogPath };
            ManholeDetectionResult manhole = BuildManhole(foundation, footprint);
            result.Audit = OpeningResetAuditService.Audit(
                doc, footprint.Walls, log);

            List<PenetrationRecord> actual = new MepPenetrationScanner(doc, log)
                .Scan(manhole);
            result.ActualCount = actual.Count;

            VirtualMepScanResult virtualScan =
                new VirtualMepExtensionScanner(doc, log)
                    .Scan(footprint, maxVirtualGapMm, maxApproachAngleDeg);
            result.VirtualScan = virtualScan;

            var seenActual = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<UnifiedOpeningReviewRow>();
            foreach (PenetrationRecord record in actual)
            {
                string key = record.SourceKey;
                if (!seenActual.Add(key))
                {
                    result.DuplicatesSkipped++;
                    log.Warn("UNIFIED duplicate actual source key skipped: " + key);
                    continue;
                }
                all.Add(MakeRow(record, "ACTUAL", false, manhole.FoundationTopZ));
            }

            // Virtual candidates identify the same source via the linked
            // element UniqueId plus the target wall; avoid double-reporting an
            // actual penetration of that wall.
            var seenVirtual = new HashSet<string>(StringComparer.Ordinal);
            foreach (VirtualMepCandidate candidate in virtualScan.Candidates)
            {
                string key = candidate.LinkInstanceId + "|" +
                    (candidate.SourceUniqueId ?? candidate.SourceElementId.ToString()) +
                    "|" + candidate.WallId;
                if (seenActual.Contains(key))
                {
                    result.DuplicatesSkipped++;
                    log.Warn("UNIFIED virtual candidate already detected as actual: " + key);
                    continue;
                }
                string virtualKey = key + "|" + candidate.Endpoint;
                if (!seenVirtual.Add(virtualKey))
                {
                    result.DuplicatesSkipped++;
                    continue;
                }

                RevitLinkInstance link =
                    doc.GetElement(new ElementId(candidate.LinkInstanceId)) as RevitLinkInstance;
                Document linkedDoc = link?.GetLinkDocument();
                Element sourceElement = null;
                if (linkedDoc != null)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(candidate.SourceUniqueId))
                            sourceElement = linkedDoc.GetElement(candidate.SourceUniqueId);
                        if (sourceElement == null)
                            sourceElement = linkedDoc.GetElement(
                                new ElementId(candidate.SourceElementId));
                    }
                    catch (Exception ex)
                    {
                        log.Warn("Cannot resolve virtual source " +
                            candidate.SourceElementId + ": " + ex.Message);
                    }
                }

                PenetrationRecord record = MakeVirtualRecord(
                    candidate, sourceElement, manhole);
                all.Add(MakeRow(record,
                    candidate.Status == "INSIDE_WALL_REVIEW"
                        ? "INSIDE WALL" : "VIRTUAL",
                    true, manhole.FoundationTopZ, candidate));
            }

            foreach (var row in all) row.Source.ClearanceMm = clearanceMm;
            CornerOpeningService.AddAdjacentPipeRows(doc, footprint, all,
                manhole.Walls.ToDictionary(w => w.Wall.Id.IntegerValue, w => w.Number), log);
            result.ActualCount = all.Count(r => !r.IsVirtual);

            // Native opening matching applies to actual AND virtual proposals.
            // It does not detect openings embedded in edited profiles;
            // those remain flagged through the separate wall audit.
            var allRecords = all.Select(x => x.Source).ToList();
            ExistingOpeningDetectionService.Apply(
                doc, footprint.Walls.Select(w => w.Id.IntegerValue),
                allRecords, log);

            foreach (UnifiedOpeningReviewRow row in all)
            {
                row.Existing = row.Source.ExistingOpeningStatus;
                row.Source.ClearanceMm = clearanceMm;
            }

            CornerOpeningService.Qualify(doc, footprint, all, log);
            foreach (var row in all) row.Evaluate(doc, clearanceMm, result.Audit);
            result.Rows.AddRange(all.OrderBy(x => x.Wall)
                .ThenBy(x => x.Detection)
                .ThenBy(x => x.SourceId));
            result.VirtualCount = result.Rows.Count(r => r.IsVirtual);

            log.WriteHeader("UNIFIED OPENING REVIEW SUMMARY");
            log.Info("Actual=" + result.ActualCount +
                " Virtual=" + result.VirtualCount +
                " ClearancePerSideMm=" + clearanceMm +
                " MaxVirtualGapMm=" + maxVirtualGapMm +
                " MaxApproachAngleDeg=" + maxApproachAngleDeg +
                " DuplicateRowsSkipped=" + result.DuplicatesSkipped +
                " ReviewRows=" + result.Rows.Count(r => r.Status == "REVIEW") +
                " NO MODEL CHANGES");
            foreach (UnifiedOpeningReviewRow row in result.Rows)
                log.Info("REVIEW_ROW " + row.Detection + " W=" + row.Wall +
                    " Source=" + row.SourceId +
                    " Opening=" + row.OpeningSize +
                    " Status=" + row.Status +
                    " Reasons=" + row.Notes);
            return result;
        }

        public static string ExportCsv(UnifiedOpeningReviewResult result)
        {
            string folder = OutputPathService.GetLogsFolder();
            string path = Path.Combine(folder, "UnifiedOpeningReview_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff",
                    CultureInfo.InvariantCulture) + ".csv");
            var sb = new StringBuilder();
            sb.AppendLine("Detection,Wall,WallId,SourceId,Link,Service,SourceSize,Gap_mm,ApproachDeg,Slope_percent,Opening_mm,OpeningBottomFromBase,NativeOpening,WallProfile,VoidCuts,Status,ReviewReasons");
            foreach (UnifiedOpeningReviewRow row in result.Rows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(row.Detection), Csv(row.Wall), row.WallId.ToString(),
                    row.SourceId.ToString(), Csv(row.LinkName), Csv(row.Service),
                    Csv(row.SourceSize), Num(row.GapMm), Num(row.ApproachAngleDeg),
                    Num(row.SlopePercent), Csv(row.OpeningSize), Csv(row.VerticalReference),
                    Csv(row.Existing), Csv(row.WallProfile), Csv(row.VoidCuts),
                    Csv(row.Status), Csv(row.Notes)
                }));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static UnifiedOpeningReviewRow MakeRow(
            PenetrationRecord r, string detection, bool isVirtual,
            double baseTopFt, VirtualMepCandidate candidate = null)
        {
            return new UnifiedOpeningReviewRow
            {
                Detection = detection,
                IsVirtual = isVirtual,
                EndpointQualified = candidate?.EligibleForProduction == true,
                Wall = "W" + r.WallNumber,
                WallId = r.HostWallId,
                SourceId = r.LinkedElementId,
                LinkName = r.LinkName,
                Service = string.IsNullOrWhiteSpace(r.SystemName)
                    ? r.Category : r.Category + " / " + r.SystemName,
                SourceSize = r.Size,
                GapMm = candidate?.GapToFaceMm ?? 0,
                SlopePercent = candidate?.SlopePercent ?? 0,
                ApproachAngleDeg = candidate?.DeviationDeg ?? 0,
                Existing = r.ExistingOpeningStatus,
                Source = r,
                BaseTopZmm = UnitUtil.FtToMm(baseTopFt)
            };
        }

        private static PenetrationRecord MakeVirtualRecord(
            VirtualMepCandidate candidate, Element source,
            ManholeDetectionResult manhole)
        {
            double diameter = ParamFt(source, BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            if (diameter <= 0)
                diameter = ParamFt(source, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            double width = ParamFt(source, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            double height = ParamFt(source, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            double dMm = UnitUtil.FtToMm(diameter);
            double wMm = UnitUtil.FtToMm(width);
            double hMm = UnitUtil.FtToMm(height);
            string shape = dMm > 0 ? "Round" :
                wMm > 0 && hMm > 0 ? "Rectangular" : "Review";
            double halfHeightFt = dMm > 0 ? diameter / 2 : height / 2;
            Wall wall = manhole.Walls.First(w =>
                w.Wall.Id.IntegerValue == candidate.WallId).Wall;
            Line axis = ((LocationCurve)wall.Location).Curve as Line;
            XYZ start = axis.GetEndPoint(0);
            XYZ end = axis.GetEndPoint(1);
            if (end.X < start.X ||
                (Math.Abs(end.X - start.X) < 1e-9 && end.Y < start.Y))
            {
                XYZ temp = start; start = end; end = temp;
            }
            XYZ direction = (end - start).Normalize();
            XYZ hit = candidate.ProjectedHit;
            double invertFt = hit.Z - halfHeightFt;
            return new PenetrationRecord
            {
                LinkName = candidate.LinkName,
                LinkInstanceId = candidate.LinkInstanceId,
                LinkedElementId = candidate.SourceElementId,
                LinkedUniqueId = candidate.SourceUniqueId,
                Category = candidate.Category,
                SystemName = candidate.SystemName,
                Size = candidate.SourceSize,
                Shape = shape,
                DiameterMm = dMm,
                WidthMm = wMm,
                HeightMm = hMm,
                ProjectedWidthMm = candidate.ProjectedWidthMm,
                ProjectedHeightMm = candidate.ProjectedHeightMm,
                WallNumber = candidate.WallNumber,
                HostWallId = candidate.WallId,
                Xmm = UnitUtil.FtToMm(hit.X),
                Ymm = UnitUtil.FtToMm(hit.Y),
                Zmm = UnitUtil.FtToMm(hit.Z),
                InvertMm = UnitUtil.FtToMm(invertFt),
                InvertAboveBaseMm = UnitUtil.FtToMm(invertFt -
                    manhole.FoundationTopZ),
                OffsetFromWallStartMm = UnitUtil.FtToMm(
                    (hit - start).DotProduct(direction)),
                Notes = "PROJECTED SOURCE; NO LINK CHANGES"
            };
        }

        private static double ParamFt(Element e, BuiltInParameter name)
        {
            Parameter p = e?.get_Parameter(name);
            return p != null && p.StorageType == StorageType.Double
                ? p.AsDouble() : 0;
        }

        internal static ManholeDetectionResult BuildManhole(
            Element foundation, VirtualFoundationResult footprint)
        {
            BoundingBoxXYZ box = foundation.get_BoundingBox(null);
            XYZ center = footprint.VirtualCenter;
            var walls = footprint.Walls.Select(w =>
            {
                Line line = ((LocationCurve)w.Location).Curve as Line;
                XYZ d = new XYZ(line.Direction.X, line.Direction.Y, 0).Normalize();
                return new ManholeWall
                {
                    Wall = w, Axis = line, Direction = d,
                    MidPoint = (line.GetEndPoint(0) +
                        line.GetEndPoint(1)) * 0.5,
                    LengthFt = line.Length
                };
            }).OrderBy(w =>
            {
                XYZ v = w.MidPoint - center;
                double angle = Math.Atan2(v.X, v.Y);
                return angle < 0 ? angle + 2 * Math.PI : angle;
            }).ToList();
            int[] numbers = { 1, 2, 4, 3 };
            for (int i = 0; i < walls.Count; i++)
                walls[i].Number = numbers[i];

            return new ManholeDetectionResult
            {
                Foundation = foundation,
                FoundationBox = box,
                Center = center,
                FoundationTopZ = box.Max.Z,
                FoundationThicknessFt = box.Max.Z - box.Min.Z,
                Walls = walls.OrderBy(w => w.Number).ToList()
            };
        }

        private static string Num(double d) =>
            d.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Csv(string t)
        {
            char quote = (char)34;
            return quote + (t ?? string.Empty)
                .Replace(quote.ToString(), new string(quote, 2)) + quote;
        }
    }
}
