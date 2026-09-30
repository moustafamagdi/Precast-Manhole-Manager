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

            data.Manholes = data.Manholes
                .OrderBy(x => NaturalManholeNumber(x.ManholeNumber))
                .ThenBy(x => x.ManholeNumber ?? string.Empty)
                .ToList();

            string folder = OutputPathService.GetManufacturerExportsFolder();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, "Precast_Manholes_" + stamp + ".xlsx");

            var sheets = new List<XlsxSheet>
            {
                BuildManholeView(data),
                BuildPrintReport(data),
                BuildDataManholes(data),
                BuildDataOpenings(data)
            };

            SimpleXlsxWriter.Write(path, sheets);

            log?.WriteHeader("PHASE 5 EXCEL EXPORT");
            log?.Info("Workbook: " + path);
            log?.Info("Visible worksheets: MANHOLE VIEW, PRINT REPORT");
            log?.Info("Hidden worksheets: DATA_MANHOLES, DATA_OPENINGS");
            log?.Info("Manholes exported: " + data.Manholes.Count);
            log?.Info("Openings exported: " + data.Openings.Count);

            return new ManufacturerExcelExportResult
            {
                ManholeCount = data.Manholes.Count,
                OpeningCount = data.Openings.Count,
                WorkbookPath = path,
                LogPath = log?.LogPath
            };
        }

        private static XlsxSheet BuildManholeView(ManufacturerWorkbookData data)
        {
            var sheet = new XlsxSheet
            {
                Name = "MANHOLE VIEW",
                FreezeRows = 11
            };

            double[] widths = { 13, 18, 18, 18, 18, 24, 18, 3, 18, 18, 18, 18, 18, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 16 };
            for (int i = 0; i < widths.Length; i++)
                sheet.ColumnWidths[i + 1] = widths[i];

            sheet.HiddenColumns.Add(26);

            sheet.AddRow(
                XlsxCell.Text("PRECAST MANHOLE - FABRICATION VIEW", 1));
            sheet.Merges.Add("A1:H1");

            sheet.AddRow(
                XlsxCell.Text(
                    "Select a manhole below. Only fabrication data is shown. All dimensions are in mm.",
                    8));
            sheet.Merges.Add("A2:H2");

            string firstManhole = data.Manholes.FirstOrDefault()?.ManholeNumber ?? string.Empty;

            sheet.AddRow(
                XlsxCell.Text("Select Manhole", 6),
                XlsxCell.Text(firstManhole, 7));
            sheet.Merges.Add("B3:C3");

            if (data.Manholes.Count > 0)
            {
                sheet.DataValidations.Add(new XlsxDataValidation
                {
                    SqRef = "B3",
                    Formula1 = "$Z$2:$Z$" + (data.Manholes.Count + 1)
                });
            }

            sheet.AddRow(XlsxCell.Text("MANHOLE DATA", 3));
            sheet.Merges.Add("A4:H4");

            sheet.AddRow(
                XlsxCell.Text("Internal Clear Size", 6),
                XlsxCell.Formula(
                    "IFERROR(TEXT(VLOOKUP($B$3,'DATA_MANHOLES'!$A:$I,2,FALSE),\"0\")&\" x \"&TEXT(VLOOKUP($B$3,'DATA_MANHOLES'!$A:$I,3,FALSE),\"0\"),\"\")",
                    7),
                XlsxCell.Text("Overall Size", 6),
                XlsxCell.Formula(
                    "IFERROR(TEXT(VLOOKUP($B$3,'DATA_MANHOLES'!$A:$I,4,FALSE),\"0\")&\" x \"&TEXT(VLOOKUP($B$3,'DATA_MANHOLES'!$A:$I,5,FALSE),\"0\"),\"\")",
                    7),
                XlsxCell.Text("Wall Height", 6),
                XlsxCell.Formula(
                    "IFERROR(VLOOKUP($B$3,'DATA_MANHOLES'!$A:$I,6,FALSE),\"\")",
                    7),
                XlsxCell.Text("mm", 7));

            sheet.AddRow(
                XlsxCell.Text("Base Thickness", 6),
                XlsxCell.Formula(
                    "IFERROR(VLOOKUP($B$3,'DATA_MANHOLES'!$A:$I,7,FALSE),\"\")",
                    7),
                XlsxCell.Text("Total Openings", 6),
                XlsxCell.Formula(
                    "IFERROR(VLOOKUP($B$3,'DATA_MANHOLES'!$A:$I,8,FALSE),\"\")",
                    7),
                XlsxCell.Text("Wall Arrangement", 6),
                XlsxCell.Text("W1 opposite W4 / W2 opposite W3", 7),
                XlsxCell.Blank(7));

            sheet.AddRow(
                XlsxCell.Text("Horizontal Reference", 6),
                XlsxCell.Text("Offset = opening C/L from wall reference edge", 7),
                XlsxCell.Blank(7),
                XlsxCell.Text("Vertical Reference", 6),
                XlsxCell.Text("Invert = bottom of service from base top", 7),
                XlsxCell.Blank(7),
                XlsxCell.Blank(7));
            sheet.Merges.Add("B7:C7");
            sheet.Merges.Add("E7:H7");

            sheet.AddRow(XlsxCell.Text(
                "For fabrication use the opening schedule below. Revit IDs and internal model references are intentionally excluded.",
                8));
            sheet.Merges.Add("A8:H8");

            sheet.AddRow(XlsxCell.Text(
                "PDF report: open PRINT REPORT then use File > Export > Create PDF/XPS. Each manhole is separated by a page break.",
                8));
            sheet.Merges.Add("A9:H9");

            sheet.AddRow(XlsxCell.Blank());

            // Visual fabrication reference on the right side of MANHOLE VIEW.
            SetCell(sheet, 1, 9, XlsxCell.Text("VERTICAL OPENING REFERENCE", 3));
            sheet.Merges.Add("I1:M1");

            SetCell(sheet, 2, 9, XlsxCell.Text("TOP OF WALL", 6));
            sheet.Merges.Add("I2:M2");

            SetCell(sheet, 3, 9, XlsxCell.Text("────────────────────────", 8));
            sheet.Merges.Add("I3:M3");

            SetCell(sheet, 4, 10, XlsxCell.Text("┌──────────────┐", 7));
            sheet.Merges.Add("J4:L4");

            SetCell(sheet, 5, 10, XlsxCell.Text("│   OPENING    │", 7));
            sheet.Merges.Add("J5:L5");

            SetCell(sheet, 6, 9, XlsxCell.Text("Service Invert →", 6));
            SetCell(sheet, 6, 10, XlsxCell.Text("│  ──────────  │", 7));
            sheet.Merges.Add("J6:L6");

            SetCell(sheet, 7, 10, XlsxCell.Text("│   SERVICE    │", 7));
            sheet.Merges.Add("J7:L7");

            SetCell(sheet, 8, 9, XlsxCell.Text("Opening Bottom →", 6));
            SetCell(sheet, 8, 10, XlsxCell.Text("└──────────────┘", 7));
            sheet.Merges.Add("J8:L8");

            SetCell(sheet, 9, 9, XlsxCell.Text("↑", 6));
            SetCell(sheet, 9, 10, XlsxCell.Text("Opening Bottom from Base", 6));
            sheet.Merges.Add("J9:M9");

            SetCell(sheet, 10, 9, XlsxCell.Text("│", 6));
            SetCell(sheet, 11, 9, XlsxCell.Text("TOP OF BASE = 0", 6));
            sheet.Merges.Add("I11:M11");

            SetCell(sheet, 12, 9, XlsxCell.Text("████████  BASE SLAB  ████████", 8));
            sheet.Merges.Add("I12:M12");

            sheet.AddRow(XlsxCell.Text("OPENING SCHEDULE", 3));
            sheet.Merges.Add("A11:H11");

            sheet.AddRow(
                XlsxCell.Text("Wall", 2),
                XlsxCell.Text("Opening", 2),
                XlsxCell.Text("Opening Size W x H", 2),
                XlsxCell.Text("Offset from Ref. Edge", 2),
                XlsxCell.Text("Opening Bottom from Base", 2),
                XlsxCell.Text("Service Invert", 2),
                XlsxCell.Text("Service / System", 2),
                XlsxCell.Text("Type", 2));

            int dataLastRow = Math.Max(2, data.Openings.Count + 1);
            int maxOpenings = data.Manholes.Count == 0
                ? 8
                : Math.Max(
                    8,
                    data.Manholes.Max(m => data.Openings.Count(o => o.FoundationId == m.FoundationId)));

            int firstOpeningRow = 13;

            for (int i = 0; i < maxOpenings; i++)
            {
                int sequence = i + 1;
                string lookupKey = "$B$3&\"|" + sequence + "\"";

                sheet.AddRow(
                    XlsxCell.Formula(
                        "IFERROR(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,3,FALSE),\"\")"),
                    XlsxCell.Formula(
                        "IFERROR(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,4,FALSE),\"\")"),
                    XlsxCell.Formula(
                        "IFERROR(TEXT(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,5,FALSE),\"0\")&\" x \"&TEXT(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,6,FALSE),\"0\"),\"\")"),
                    XlsxCell.Formula(
                        "IFERROR(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,7,FALSE),\"\")",
                        5),
                    XlsxCell.Formula(
                        "IFERROR(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,8,FALSE),\"\")",
                        5),
                    XlsxCell.Formula(
                        "IFERROR(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,9,FALSE),\"\")",
                        5),
                    XlsxCell.Formula(
                        "IFERROR(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,10,FALSE)&IF(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,11,FALSE)<>\"\",\" / \"&VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,11,FALSE),\"\"),\"\")"),
                    XlsxCell.Formula(
                        "IFERROR(VLOOKUP(" + lookupKey + ",'DATA_OPENINGS'!$A:$M,12,FALSE),\"\")"));
            }

            for (int i = 0; i < data.Manholes.Count; i++)
            {
                int row = i + 2;
                SetCell(sheet, row, 26, XlsxCell.Text(data.Manholes[i].ManholeNumber));
            }

            return sheet;
        }

        private static XlsxSheet BuildPrintReport(ManufacturerWorkbookData data)
        {
            var sheet = new XlsxSheet
            {
                Name = "PRINT REPORT",
                Landscape = true,
                FitToOnePageWide = true
            };

            double[] widths = { 10, 12, 18, 17, 20, 16, 26, 16 };
            for (int i = 0; i < widths.Length; i++)
                sheet.ColumnWidths[i + 1] = widths[i];

            foreach (ManholeDataRecord m in data.Manholes)
            {
                int pageStartRow = sheet.Rows.Count + 1;

                List<ManufacturerOpeningRow> openings = data.Openings
                    .Where(x => x.FoundationId == m.FoundationId)
                    .OrderBy(x => x.WallNumber)
                    .ThenBy(x => x.OpeningNumber ?? string.Empty)
                    .ToList();

                sheet.AddRow(XlsxCell.Text(
                    "PRECAST MANHOLE FABRICATION REPORT - " + (m.ManholeNumber ?? string.Empty),
                    1));
                sheet.Merges.Add("A" + pageStartRow + ":H" + pageStartRow);

                sheet.AddRow(
                    XlsxCell.Text("Internal Clear Size", 6),
                    XlsxCell.Text(
                        F0(m.ClearW2W3Mm) + " x " + F0(m.ClearW1W4Mm),
                        7),
                    XlsxCell.Text("Overall Size", 6),
                    XlsxCell.Text(
                        F0(m.OuterW2W3Mm) + " x " + F0(m.OuterW1W4Mm),
                        7),
                    XlsxCell.Text("Wall Height", 6),
                    XlsxCell.Number(m.WallHeightMm, 7),
                    XlsxCell.Text("mm", 7));

                sheet.AddRow(
                    XlsxCell.Text("Base Thickness", 6),
                    XlsxCell.Number(m.BaseThicknessMm, 7),
                    XlsxCell.Text("Total Openings", 6),
                    XlsxCell.Integer(openings.Count, 7),
                    XlsxCell.Text("Wall Arrangement", 6),
                    XlsxCell.Text("W1-W4 opposite / W2-W3 opposite", 7),
                    XlsxCell.Blank(7));

                sheet.AddRow(XlsxCell.Text(
                    "Opening positions: horizontal offset is opening C/L from wall reference edge; vertical fabrication dimension is opening bottom from base top. Service invert is reference only.",
                    8));
                sheet.Merges.Add("A" + (pageStartRow + 3) + ":H" + (pageStartRow + 3));

                sheet.AddRow(XlsxCell.Blank());

                sheet.AddRow(
                    XlsxCell.Text("Wall", 2),
                    XlsxCell.Text("Opening", 2),
                    XlsxCell.Text("Opening Size W x H", 2),
                    XlsxCell.Text("Offset Ref. Edge", 2),
                    XlsxCell.Text("Opening Bottom Base", 2),
                    XlsxCell.Text("Service Invert", 2),
                    XlsxCell.Text("Service / System", 2),
                    XlsxCell.Text("Type", 2));

                if (openings.Count == 0)
                {
                    int noOpenRow = sheet.Rows.Count + 1;
                    sheet.AddRow(XlsxCell.Text("No managed fabrication openings", 8));
                    sheet.Merges.Add("A" + noOpenRow + ":H" + noOpenRow);
                }
                else
                {
                    foreach (ManufacturerOpeningRow o in openings)
                    {
                        string service = o.ServiceCategory ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(o.SystemName))
                            service += (service.Length > 0 ? " / " : string.Empty) + o.SystemName;

                        sheet.AddRow(
                            XlsxCell.Text("W" + o.WallNumber),
                            XlsxCell.Text(o.OpeningNumber),
                            XlsxCell.Text(
                                F0(o.OpeningWidthMm) + " x " + F0(o.OpeningHeightMm)),
                            XlsxCell.Number(o.OffsetMm),
                            XlsxCell.Number(o.OpeningBottomFromBaseMm),
                            XlsxCell.Number(o.InvertFromBaseMm),
                            XlsxCell.Text(service),
                            XlsxCell.Text(o.OpeningType));
                    }
                }

                sheet.AddRow(XlsxCell.Blank());
                int noteRow = sheet.Rows.Count + 1;
                sheet.AddRow(XlsxCell.Text(
                    "Fabrication note: verify wall orientation and reference edge against the approved coordination drawing before production.",
                    8));
                sheet.Merges.Add("A" + noteRow + ":H" + noteRow);

                sheet.AddRow(XlsxCell.Blank());

                int pageEndRow = sheet.Rows.Count;
                if (m != data.Manholes.Last())
                    sheet.HorizontalPageBreakRows.Add(pageEndRow);
            }

            return sheet;
        }

        private static XlsxSheet BuildDataManholes(ManufacturerWorkbookData data)
        {
            var sheet = new XlsxSheet
            {
                Name = "DATA_MANHOLES",
                Hidden = true
            };

            sheet.AddRow(
                XlsxCell.Text("Manhole"),
                XlsxCell.Text("ClearLength"),
                XlsxCell.Text("ClearWidth"),
                XlsxCell.Text("OuterLength"),
                XlsxCell.Text("OuterWidth"),
                XlsxCell.Text("WallHeight"),
                XlsxCell.Text("BaseThickness"),
                XlsxCell.Text("OpeningCount"),
                XlsxCell.Text("FoundationId"));

            foreach (ManholeDataRecord m in data.Manholes)
            {
                sheet.AddRow(
                    XlsxCell.Text(m.ManholeNumber),
                    XlsxCell.Number(m.ClearW2W3Mm),
                    XlsxCell.Number(m.ClearW1W4Mm),
                    XlsxCell.Number(m.OuterW2W3Mm),
                    XlsxCell.Number(m.OuterW1W4Mm),
                    XlsxCell.Number(m.WallHeightMm),
                    XlsxCell.Number(m.BaseThicknessMm),
                    XlsxCell.Integer(data.Openings.Count(x => x.FoundationId == m.FoundationId)),
                    XlsxCell.Integer(m.FoundationId));
            }

            return sheet;
        }

        private static XlsxSheet BuildDataOpenings(ManufacturerWorkbookData data)
        {
            var sheet = new XlsxSheet
            {
                Name = "DATA_OPENINGS",
                Hidden = true
            };

            sheet.AddRow(
                XlsxCell.Text("LookupKey"),
                XlsxCell.Text("Manhole"),
                XlsxCell.Text("Wall"),
                XlsxCell.Text("Opening"),
                XlsxCell.Text("Width"),
                XlsxCell.Text("Height"),
                XlsxCell.Text("Offset"),
                XlsxCell.Text("OpeningBottomFromBase"),
                XlsxCell.Text("ServiceInvertFromBase"),
                XlsxCell.Text("Service"),
                XlsxCell.Text("System"),
                XlsxCell.Text("Type"),
                XlsxCell.Text("Status"));

            foreach (IGrouping<string, ManufacturerOpeningRow> group in data.Openings
                         .GroupBy(x => x.ManholeNumber ?? string.Empty)
                         .OrderBy(g => NaturalManholeNumber(g.Key))
                         .ThenBy(g => g.Key))
            {
                int sequence = 1;

                foreach (ManufacturerOpeningRow r in group
                             .OrderBy(x => x.WallNumber)
                             .ThenBy(x => x.OpeningNumber ?? string.Empty))
                {
                    string lookupKey =
                        (r.ManholeNumber ?? string.Empty) +
                        "|" +
                        sequence.ToString(CultureInfo.InvariantCulture);

                    sheet.AddRow(
                        XlsxCell.Text(lookupKey),
                        XlsxCell.Text(r.ManholeNumber),
                        XlsxCell.Text("W" + r.WallNumber),
                        XlsxCell.Text(r.OpeningNumber),
                        XlsxCell.Number(r.OpeningWidthMm),
                        XlsxCell.Number(r.OpeningHeightMm),
                        XlsxCell.Number(r.OffsetMm),
                        XlsxCell.Number(r.OpeningBottomFromBaseMm),
                        XlsxCell.Number(r.InvertFromBaseMm),
                        XlsxCell.Text(r.ServiceCategory),
                        XlsxCell.Text(r.SystemName),
                        XlsxCell.Text(r.OpeningType),
                        XlsxCell.Text(r.Status));

                    sequence++;
                }
            }

            return sheet;
        }

        private static void SetCell(
            XlsxSheet sheet,
            int rowNumber,
            int columnNumber,
            XlsxCell cell)
        {
            while (sheet.Rows.Count < rowNumber)
                sheet.Rows.Add(new List<XlsxCell>());

            List<XlsxCell> row = sheet.Rows[rowNumber - 1];

            while (row.Count < columnNumber)
                row.Add(XlsxCell.Blank());

            row[columnNumber - 1] = cell;
        }

        private static int NaturalManholeNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return int.MaxValue;

            string digits = new string(
                value.Reverse()
                    .TakeWhile(char.IsDigit)
                    .Reverse()
                    .ToArray());

            int number;
            return int.TryParse(digits, out number)
                ? number
                : int.MaxValue;
        }

        private static string F0(double value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
