using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class ManufacturerExcelExportResult
    {
        public int ManholeCount { get; set; }
        public int OpeningCount { get; set; }
        public string WorkbookPath { get; set; }
        public string LogPath { get; set; }
    }

    internal static class ManufacturerExcelExportService
    {
        public static ManufacturerExcelExportResult Export(Document doc, DiagnosticLogger log)
        {
            ManufacturerWorkbookData data = ManufacturerWorkbookDataService.Collect(doc, log);

            string folder = OutputPathService.GetManufacturerExportsFolder();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, "Precast_Manholes_" + stamp + ".xlsx");

            var sheets = new List<XlsxSheet>
            {
                BuildManholesSheet(data),
                BuildOpeningsSheet(data)
            };

            HashSet<string> usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MANHOLES",
                "OPENINGS"
            };

            foreach (ManholeDataRecord manhole in data.Manholes)
            {
                List<ManufacturerOpeningRow> openings = data.Openings
                    .Where(x => x.FoundationId == manhole.FoundationId)
                    .OrderBy(x => x.WallNumber)
                    .ThenBy(x => x.OpeningNumber ?? string.Empty)
                    .ToList();

                string sheetName = CreateUniqueSheetName(
                    manhole.ManholeNumber,
                    manhole.FoundationId,
                    usedSheetNames);

                sheets.Add(BuildManholeDetailSheet(sheetName, manhole, openings));
            }

            SimpleXlsxWriter.Write(path, sheets);

            log?.WriteHeader("PHASE 5 EXCEL EXPORT");
            log?.Info("Workbook: " + path);
            log?.Info("Manholes exported: " + data.Manholes.Count);
            log?.Info("Openings exported: " + data.Openings.Count);
            log?.Info("Worksheet count: " + sheets.Count);

            return new ManufacturerExcelExportResult
            {
                ManholeCount = data.Manholes.Count,
                OpeningCount = data.Openings.Count,
                WorkbookPath = path,
                LogPath = log?.LogPath
            };
        }

        private static XlsxSheet BuildManholesSheet(ManufacturerWorkbookData data)
        {
            var sheet = new XlsxSheet
            {
                Name = "MANHOLES",
                FreezeRows = 3
            };

            sheet.ColumnWidths[1] = 16;
            sheet.ColumnWidths[2] = 14;
            sheet.ColumnWidths[3] = 15;
            sheet.ColumnWidths[4] = 15;
            sheet.ColumnWidths[5] = 15;
            sheet.ColumnWidths[6] = 15;
            sheet.ColumnWidths[7] = 14;
            sheet.ColumnWidths[8] = 14;
            sheet.ColumnWidths[9] = 18;
            sheet.ColumnWidths[10] = 12;
            sheet.ColumnWidths[11] = 12;
            sheet.ColumnWidths[12] = 12;
            sheet.ColumnWidths[13] = 12;
            sheet.ColumnWidths[14] = 14;

            sheet.AddRow(XlsxCell.Text("PRECAST MANHOLES - MANUFACTURER SUMMARY", 1));
            sheet.Merges.Add("A1:N1");
            sheet.AddRow(XlsxCell.Text("All dimensions and elevations are in millimeters.", 8));
            sheet.Merges.Add("A2:N2");

            sheet.AddRow(
                XlsxCell.Text("Manhole No.", 2),
                XlsxCell.Text("Foundation ID", 2),
                XlsxCell.Text("Clear W1-W4", 2),
                XlsxCell.Text("Clear W2-W3", 2),
                XlsxCell.Text("Outer W1-W4", 2),
                XlsxCell.Text("Outer W2-W3", 2),
                XlsxCell.Text("Wall Height", 2),
                XlsxCell.Text("Base Thickness", 2),
                XlsxCell.Text("Base Top Elev.", 2),
                XlsxCell.Text("W1 ID", 2),
                XlsxCell.Text("W2 ID", 2),
                XlsxCell.Text("W3 ID", 2),
                XlsxCell.Text("W4 ID", 2),
                XlsxCell.Text("Openings", 2));

            foreach (ManholeDataRecord m in data.Manholes)
            {
                int openingCount = data.Openings.Count(x => x.FoundationId == m.FoundationId);

                sheet.AddRow(
                    XlsxCell.Text(m.ManholeNumber),
                    XlsxCell.Integer(m.FoundationId),
                    XlsxCell.Number(m.ClearW1W4Mm),
                    XlsxCell.Number(m.ClearW2W3Mm),
                    XlsxCell.Number(m.OuterW1W4Mm),
                    XlsxCell.Number(m.OuterW2W3Mm),
                    XlsxCell.Number(m.WallHeightMm),
                    XlsxCell.Number(m.BaseThicknessMm),
                    XlsxCell.Number(m.BaseTopZmm),
                    XlsxCell.Integer(m.Wall1Id),
                    XlsxCell.Integer(m.Wall2Id),
                    XlsxCell.Integer(m.Wall3Id),
                    XlsxCell.Integer(m.Wall4Id),
                    XlsxCell.Integer(openingCount));
            }

            return sheet;
        }

        private static XlsxSheet BuildOpeningsSheet(ManufacturerWorkbookData data)
        {
            var sheet = new XlsxSheet
            {
                Name = "OPENINGS",
                FreezeRows = 3
            };

            double[] widths =
            {
                15, 8, 10, 22, 14, 14, 14, 16, 14, 18,
                18, 18, 18, 18, 18, 24, 22, 12, 36, 14
            };

            for (int i = 0; i < widths.Length; i++)
                sheet.ColumnWidths[i + 1] = widths[i];

            sheet.AddRow(XlsxCell.Text("PRECAST MANHOLE OPENING SCHEDULE", 1));
            sheet.Merges.Add("A1:T1");
            sheet.AddRow(XlsxCell.Text(
                "Opening size = actual precast wall opening. Offset is from the stable wall start used by the add-in.",
                8));
            sheet.Merges.Add("A2:T2");

            sheet.AddRow(
                XlsxCell.Text("Manhole", 2),
                XlsxCell.Text("Wall", 2),
                XlsxCell.Text("Opening", 2),
                XlsxCell.Text("Opening Code", 2),
                XlsxCell.Text("Width", 2),
                XlsxCell.Text("Height", 2),
                XlsxCell.Text("Offset", 2),
                XlsxCell.Text("Invert From Base", 2),
                XlsxCell.Text("Absolute Invert", 2),
                XlsxCell.Text("Center Elev.", 2),
                XlsxCell.Text("Opening Type", 2),
                XlsxCell.Text("Service", 2),
                XlsxCell.Text("System", 2),
                XlsxCell.Text("Family / Type", 2),
                XlsxCell.Text("Opening ID", 2),
                XlsxCell.Text("Source Link", 2),
                XlsxCell.Text("Source Element ID", 2),
                XlsxCell.Text("Foundation ID", 2),
                XlsxCell.Text("Source Unique ID", 2),
                XlsxCell.Text("Status", 2));

            foreach (ManufacturerOpeningRow r in data.Openings)
            {
                sheet.AddRow(
                    XlsxCell.Text(r.ManholeNumber),
                    XlsxCell.Text("W" + r.WallNumber),
                    XlsxCell.Text(r.OpeningNumber),
                    XlsxCell.Text(r.OpeningCode),
                    XlsxCell.Number(r.OpeningWidthMm),
                    XlsxCell.Number(r.OpeningHeightMm),
                    XlsxCell.Number(r.OffsetMm),
                    XlsxCell.Number(r.InvertFromBaseMm),
                    XlsxCell.Number(r.AbsoluteInvertMm),
                    XlsxCell.Number(r.CenterElevationMm),
                    XlsxCell.Text(r.OpeningType),
                    XlsxCell.Text(r.ServiceCategory),
                    XlsxCell.Text(r.SystemName),
                    XlsxCell.Text(r.FamilyType),
                    XlsxCell.Integer(r.OpeningElementId),
                    XlsxCell.Text(r.SourceLink),
                    XlsxCell.Integer(r.SourceElementId),
                    XlsxCell.Integer(r.FoundationId),
                    XlsxCell.Text(r.SourceUniqueId),
                    XlsxCell.Text(r.Status));
            }

            return sheet;
        }

        private static XlsxSheet BuildManholeDetailSheet(
            string sheetName,
            ManholeDataRecord m,
            IList<ManufacturerOpeningRow> openings)
        {
            var sheet = new XlsxSheet
            {
                Name = sheetName,
                FreezeRows = 1
            };

            sheet.ColumnWidths[1] = 18;
            sheet.ColumnWidths[2] = 18;
            sheet.ColumnWidths[3] = 14;
            sheet.ColumnWidths[4] = 16;
            sheet.ColumnWidths[5] = 18;
            sheet.ColumnWidths[6] = 18;
            sheet.ColumnWidths[7] = 20;
            sheet.ColumnWidths[8] = 34;

            sheet.AddRow(XlsxCell.Text(
                "PRECAST MANHOLE DATA SHEET - " + (m.ManholeNumber ?? string.Empty),
                1));
            sheet.Merges.Add("A1:H1");

            sheet.AddRow(XlsxCell.Text("General Data", 3));
            sheet.Merges.Add("A2:H2");

            sheet.AddRow(
                XlsxCell.Text("Manhole No.", 6),
                XlsxCell.Text(m.ManholeNumber, 7),
                XlsxCell.Text("Foundation ID", 6),
                XlsxCell.Integer(m.FoundationId, 7),
                XlsxCell.Text("Total Openings", 6),
                XlsxCell.Integer(openings.Count, 7),
                XlsxCell.Blank(7),
                XlsxCell.Blank(7));

            sheet.AddRow(
                XlsxCell.Text("Clear W1-W4", 6),
                XlsxCell.Number(m.ClearW1W4Mm, 7),
                XlsxCell.Text("Clear W2-W3", 6),
                XlsxCell.Number(m.ClearW2W3Mm, 7),
                XlsxCell.Text("Wall Height", 6),
                XlsxCell.Number(m.WallHeightMm, 7),
                XlsxCell.Text("mm", 7),
                XlsxCell.Blank(7));

            sheet.AddRow(
                XlsxCell.Text("Outer W1-W4", 6),
                XlsxCell.Number(m.OuterW1W4Mm, 7),
                XlsxCell.Text("Outer W2-W3", 6),
                XlsxCell.Number(m.OuterW2W3Mm, 7),
                XlsxCell.Text("Base Thickness", 6),
                XlsxCell.Number(m.BaseThicknessMm, 7),
                XlsxCell.Text("mm", 7),
                XlsxCell.Blank(7));

            sheet.AddRow(
                XlsxCell.Text("Base Top Elev.", 6),
                XlsxCell.Number(m.BaseTopZmm, 7),
                XlsxCell.Text("W1 / W4", 6),
                XlsxCell.Text(m.Wall1Id + " / " + m.Wall4Id, 7),
                XlsxCell.Text("W2 / W3", 6),
                XlsxCell.Text(m.Wall2Id + " / " + m.Wall3Id, 7),
                XlsxCell.Blank(7),
                XlsxCell.Blank(7));

            sheet.AddRow(XlsxCell.Text(
                "Wall convention: W1 opposite W4, W2 opposite W3. All dimensions are millimeters.",
                8));
            sheet.Merges.Add("A7:H7");

            int rowNumber = 8;

            for (int wall = 1; wall <= 4; wall++)
            {
                List<ManufacturerOpeningRow> wallOpenings = openings
                    .Where(x => x.WallNumber == wall)
                    .OrderBy(x => x.OpeningNumber ?? string.Empty)
                    .ToList();

                sheet.AddRow(XlsxCell.Text(
                    "WALL W" + wall + " - " + wallOpenings.Count + " OPENING(S)",
                    3));
                rowNumber++;
                sheet.Merges.Add("A" + rowNumber + ":H" + rowNumber);

                sheet.AddRow(
                    XlsxCell.Text("Opening Code", 2),
                    XlsxCell.Text("Size W x H", 2),
                    XlsxCell.Text("Offset", 2),
                    XlsxCell.Text("Invert Base", 2),
                    XlsxCell.Text("Abs. Invert", 2),
                    XlsxCell.Text("Service", 2),
                    XlsxCell.Text("System", 2),
                    XlsxCell.Text("Source", 2));
                rowNumber++;

                if (wallOpenings.Count == 0)
                {
                    sheet.AddRow(XlsxCell.Text("No openings", 8));
                    rowNumber++;
                    sheet.Merges.Add("A" + rowNumber + ":H" + rowNumber);
                }
                else
                {
                    foreach (ManufacturerOpeningRow r in wallOpenings)
                    {
                        sheet.AddRow(
                            XlsxCell.Text(r.OpeningCode),
                            XlsxCell.Text(
                                r.OpeningWidthMm.ToString("0.#", CultureInfo.InvariantCulture) +
                                " x " +
                                r.OpeningHeightMm.ToString("0.#", CultureInfo.InvariantCulture)),
                            XlsxCell.Number(r.OffsetMm),
                            XlsxCell.Number(r.InvertFromBaseMm),
                            XlsxCell.Number(r.AbsoluteInvertMm),
                            XlsxCell.Text(r.ServiceCategory),
                            XlsxCell.Text(r.SystemName),
                            XlsxCell.Text(
                                (r.SourceLink ?? string.Empty) +
                                " | ID " +
                                r.SourceElementId));
                        rowNumber++;
                    }
                }

                sheet.AddRow(XlsxCell.Blank());
                rowNumber++;
            }

            return sheet;
        }

        private static string CreateUniqueSheetName(
            string manholeNumber,
            int foundationId,
            HashSet<string> used)
        {
            string baseName = string.IsNullOrWhiteSpace(manholeNumber)
                ? "MH-" + foundationId
                : manholeNumber;

            foreach (char invalid in new[] { ':', '\\', '/', '?', '*', '[', ']' })
                baseName = baseName.Replace(invalid, '-');

            baseName = baseName.Trim();
            if (baseName.Length == 0)
                baseName = "MH-" + foundationId;

            if (baseName.Length > 31)
                baseName = baseName.Substring(0, 31);

            string candidate = baseName;
            int suffix = 2;

            while (used.Contains(candidate))
            {
                string suffixText = "-" + suffix;
                int maxBase = 31 - suffixText.Length;
                string shortBase = baseName.Length > maxBase
                    ? baseName.Substring(0, maxBase)
                    : baseName;

                candidate = shortBase + suffixText;
                suffix++;
            }

            used.Add(candidate);
            return candidate;
        }
    }
}
