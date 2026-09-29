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
    internal sealed class ManufacturerExportResult
    {
        public int ManholeCount { get; set; }
        public int OpeningCount { get; set; }
        public string SummaryCsvPath { get; set; }
        public string OpeningsCsvPath { get; set; }
        public string LogPath { get; set; }
    }

    internal static class ManufacturerExportService
    {
        public static ManufacturerExportResult ExportAll(Document doc, DiagnosticLogger log)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            string folder = OutputPathService.GetManufacturerExportsFolder();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            string summaryPath = Path.Combine(folder, "Manholes_Summary_" + stamp + ".csv");
            string openingsPath = Path.Combine(folder, "Manhole_Openings_" + stamp + ".csv");

            List<ManholeDataRecord> manholes = ManholeDataCarrierService.ReadAll(doc);
            Dictionary<int, ManholeDataRecord> byFoundation = manholes
                .GroupBy(x => x.FoundationId)
                .ToDictionary(g => g.Key, g => g.First());

            log.WriteHeader("PHASE 5 MANUFACTURER EXPORT");
            log.Info($"Saved manholes found: {manholes.Count}");

            WriteManholeSummary(summaryPath, manholes);

            var openingRows = new List<OpeningExportRow>();

            foreach (Opening opening in new FilteredElementCollector(doc)
                         .OfClass(typeof(Opening))
                         .Cast<Opening>())
            {
                ManagedOpeningData managed;
                if (!OpeningStorageService.TryRead(opening, out managed))
                    continue;

                OpeningManholeLinkData link;
                if (!OpeningManholeLinkService.TryReadLink(opening, out link))
                    continue;

                ManholeDataRecord manhole;
                if (!byFoundation.TryGetValue(link.FoundationId, out manhole))
                {
                    log.Warn(
                        $"Opening {opening.Id.IntegerValue} is linked to Foundation {link.FoundationId}, " +
                        "but no saved manhole data carrier was found.");
                    continue;
                }

                OpeningExportRow row = BuildOpeningRow(doc, opening, managed, link, manhole);
                openingRows.Add(row);

                log.Info(
                    $"EXPORT Opening={opening.Id.IntegerValue} Code='{row.OpeningCode}' " +
                    $"Wall=W{row.WallNumber} Size={row.OpeningWidthMm:0.#}x{row.OpeningHeightMm:0.#}mm " +
                    $"Offset={row.OffsetMm:0.#}mm InvertFromBase={row.InvertFromBaseMm:0.#}mm");
            }

            openingRows = openingRows
                .OrderBy(x => x.ManholeNumber ?? string.Empty)
                .ThenBy(x => x.WallNumber)
                .ThenBy(x => x.OpeningNumber ?? string.Empty)
                .ToList();

            WriteOpenings(openingsPath, openingRows);

            log.Info($"Manufacturer summary CSV: {summaryPath}");
            log.Info($"Manufacturer openings CSV: {openingsPath}");
            log.Info($"Opening rows exported: {openingRows.Count}");

            return new ManufacturerExportResult
            {
                ManholeCount = manholes.Count,
                OpeningCount = openingRows.Count,
                SummaryCsvPath = summaryPath,
                OpeningsCsvPath = openingsPath,
                LogPath = log.LogPath
            };
        }

        private static void WriteManholeSummary(string path, IEnumerable<ManholeDataRecord> manholes)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "ManholeNo,FoundationId,Clear_W1_W4_mm,Clear_W2_W3_mm,Outer_W1_W4_mm,Outer_W2_W3_mm," +
                "WallHeight_mm,BaseThickness_mm,BaseTopElevation_mm,W1_WallId,W2_WallId,W3_WallId,W4_WallId");

            foreach (ManholeDataRecord m in manholes)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(m.ManholeNumber),
                    I(m.FoundationId),
                    F(m.ClearW1W4Mm),
                    F(m.ClearW2W3Mm),
                    F(m.OuterW1W4Mm),
                    F(m.OuterW2W3Mm),
                    F(m.WallHeightMm),
                    F(m.BaseThicknessMm),
                    F(m.BaseTopZmm),
                    I(m.Wall1Id),
                    I(m.Wall2Id),
                    I(m.Wall3Id),
                    I(m.Wall4Id)
                }));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static void WriteOpenings(string path, IEnumerable<OpeningExportRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "ManholeNo,WallNo,OpeningNo,OpeningCode,OpeningElementId,OpeningWidth_mm,OpeningHeight_mm," +
                "OpeningType,AdoptedManual,OffsetFromWallStart_mm,InvertFromBase_mm,AbsoluteInvert_mm," +
                "CenterElevation_mm,ServiceCategory,SystemName,FamilyType,SourceLink,SourceElementId,SourceUniqueId,Status");

            foreach (OpeningExportRow r in rows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(r.ManholeNumber),
                    I(r.WallNumber),
                    Csv(r.OpeningNumber),
                    Csv(r.OpeningCode),
                    I(r.OpeningElementId),
                    F(r.OpeningWidthMm),
                    F(r.OpeningHeightMm),
                    Csv(r.OpeningType),
                    r.AdoptedManual ? "Yes" : "No",
                    F(r.OffsetMm),
                    F(r.InvertFromBaseMm),
                    F(r.AbsoluteInvertMm),
                    F(r.CenterZMm),
                    Csv(r.ServiceCategory),
                    Csv(r.SystemName),
                    Csv(r.FamilyType),
                    Csv(r.SourceLink),
                    I(r.SourceElementId),
                    Csv(r.SourceUniqueId),
                    Csv(r.Status)
                }));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static OpeningExportRow BuildOpeningRow(
            Document doc,
            Opening opening,
            ManagedOpeningData managed,
            OpeningManholeLinkData link,
            ManholeDataRecord manhole)
        {
            double offsetMm = CalculateOffsetFromWallStart(doc, managed.HostWallId, managed.Xmm, managed.Ymm, managed.Zmm);

            string category = string.Empty;
            string systemName = string.Empty;
            string familyType = string.Empty;
            double sourceHeightMm = 0.0;

            TryResolveSourceDetails(
                doc,
                managed,
                out category,
                out systemName,
                out familyType,
                out sourceHeightMm);

            if (sourceHeightMm <= 0)
                sourceHeightMm = InferSourceHeightMm(managed);

            double absoluteInvertMm = managed.Zmm - (sourceHeightMm / 2.0);
            double invertFromBaseMm = absoluteInvertMm - manhole.BaseTopZmm;

            return new OpeningExportRow
            {
                ManholeNumber = link.ManholeNumber,
                WallNumber = link.WallNumber,
                OpeningNumber = link.OpeningNumber,
                OpeningCode = link.OpeningCode,
                OpeningElementId = opening.Id.IntegerValue,
                OpeningWidthMm = managed.CutWidthMm,
                OpeningHeightMm = managed.CutHeightMm,
                OpeningType = managed.AdoptedManual ? "Adopted Manual" : "Managed",
                AdoptedManual = managed.AdoptedManual,
                OffsetMm = offsetMm,
                InvertFromBaseMm = invertFromBaseMm,
                AbsoluteInvertMm = absoluteInvertMm,
                CenterZMm = managed.Zmm,
                ServiceCategory = category,
                SystemName = systemName,
                FamilyType = familyType,
                SourceLink = managed.LinkName,
                SourceElementId = managed.LinkedElementId,
                SourceUniqueId = managed.LinkedUniqueId,
                Status = "OK"
            };
        }

        private static double InferSourceHeightMm(ManagedOpeningData managed)
        {
            double sourceHeight = managed.CutHeightMm - (2.0 * managed.ClearanceMm);
            return Math.Max(0.0, sourceHeight);
        }

        private static double CalculateOffsetFromWallStart(
            Document doc,
            int wallId,
            double xmm,
            double ymm,
            double zmm)
        {
            Wall wall = doc.GetElement(new ElementId(wallId)) as Wall;
            if (wall == null || !(wall.Location is LocationCurve lc) || !(lc.Curve is Line line))
                return 0;

            XYZ a = line.GetEndPoint(0);
            XYZ b = line.GetEndPoint(1);

            XYZ start =
                (a.X < b.X || (Math.Abs(a.X - b.X) < 1e-9 && a.Y <= b.Y))
                    ? a
                    : b;

            XYZ other = start.IsAlmostEqualTo(a) ? b : a;
            XYZ direction = (other - start).Normalize();

            XYZ point = new XYZ(
                UnitUtil.MmToFt(xmm),
                UnitUtil.MmToFt(ymm),
                UnitUtil.MmToFt(zmm));

            return UnitUtil.FtToMm(Math.Abs((point - start).DotProduct(direction)));
        }

        private static void TryResolveSourceDetails(
            Document host,
            ManagedOpeningData managed,
            out string category,
            out string systemName,
            out string familyType,
            out double sourceHeightMm)
        {
            category = string.Empty;
            systemName = string.Empty;
            familyType = string.Empty;
            sourceHeightMm = 0.0;

            RevitLinkInstance link = host.GetElement(new ElementId(managed.LinkInstanceId)) as RevitLinkInstance;
            Document linkDoc = link?.GetLinkDocument();
            if (linkDoc == null)
                return;

            Element source = null;

            if (!string.IsNullOrWhiteSpace(managed.LinkedUniqueId))
            {
                try { source = linkDoc.GetElement(managed.LinkedUniqueId); }
                catch { }
            }

            if (source == null && managed.LinkedElementId > 0)
                source = linkDoc.GetElement(new ElementId(managed.LinkedElementId));

            if (source == null)
                return;

            category = source.Category?.Name ?? string.Empty;

            Element type = source.Document.GetElement(source.GetTypeId());
            familyType = type?.Name ?? source.Name ?? string.Empty;

            systemName = GetParameterText(source, "System Name", "System Type", "System Classification");

            double diameterFt = GetDouble(source, BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            if (diameterFt <= 0)
                diameterFt = GetDouble(source, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

            if (diameterFt > 0)
            {
                sourceHeightMm = UnitUtil.FtToMm(diameterFt);
                return;
            }

            double heightFt = GetDouble(source, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            if (heightFt <= 0)
            {
                Parameter hp = source.LookupParameter("Height");
                if (hp != null && hp.StorageType == StorageType.Double)
                    heightFt = hp.AsDouble();
            }

            if (heightFt > 0)
                sourceHeightMm = UnitUtil.FtToMm(heightFt);
        }

        private static double GetDouble(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : 0.0;
        }

        private static string GetParameterText(Element e, params string[] names)
        {
            foreach (string name in names)
            {
                Parameter p = e.LookupParameter(name);
                if (p == null) continue;

                string value = p.AsString() ?? p.AsValueString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static string F(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string I(int value) =>
            value.ToString(CultureInfo.InvariantCulture);

        private static string Csv(string value)
        {
            char q = (char)34;
            string text = value ?? string.Empty;
            text = text.Replace(q.ToString(), new string(q, 2));
            return q + text + q;
        }

        private sealed class OpeningExportRow
        {
            public string ManholeNumber { get; set; }
            public int WallNumber { get; set; }
            public string OpeningNumber { get; set; }
            public string OpeningCode { get; set; }
            public int OpeningElementId { get; set; }
            public double OpeningWidthMm { get; set; }
            public double OpeningHeightMm { get; set; }
            public string OpeningType { get; set; }
            public bool AdoptedManual { get; set; }
            public double OffsetMm { get; set; }
            public double InvertFromBaseMm { get; set; }
            public double AbsoluteInvertMm { get; set; }
            public double CenterZMm { get; set; }
            public string ServiceCategory { get; set; }
            public string SystemName { get; set; }
            public string FamilyType { get; set; }
            public string SourceLink { get; set; }
            public int SourceElementId { get; set; }
            public string SourceUniqueId { get; set; }
            public string Status { get; set; }
        }
    }
}
