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

            double[] widths = { 13, 18, 18, 18, 18, 24, 18, 4, 4, 16 };
            for (int i = 0; i < widths.Length; i++)
                sheet.ColumnWidths[i + 1] = widths[i];

            sheet.HiddenColumns.Add(10);

            sheet.AddRow(
                XlsxCell.Text("PRECAST MANHOLE - FABRICATION VIEW", 1));
            sheet.Merges.Add("A1:G1");

            sheet.AddRow(
                XlsxCell.Text(
                    "Select a manhole below. Only fabrication data is shown. All dimensions are in mm.",
                    8));
            sheet.Merges.Add("A2:G2");

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
                    Formula1 = "$J$2:$J$" + (data.Manholes.Count + 1)
                });
            }

            sheet.AddRow(XlsxCell.Text("MANHOLE DATA", 3));
            sheet.Merges.Add("A4:G4");

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
            sheet.Merges.Add("E7:G7");

            sheet.AddRow(XlsxCell.Text(
                "For fabrication use the opening schedule below. Revit IDs and internal model references are intentionally excluded.",
                8));
            sheet.Merges.Add("A8:G8");

            sheet.AddRow(XlsxCell.Text(
                "PDF report: open PRINT REPORT then use File > Export > Create PDF/XPS. Each manhole is separated by a page break.",
                8));
            sheet.Merges.Add("A9:G9");

            sheet.AddRow(XlsxCell.Blank());

            sheet.AddRow(XlsxCell.Text("OPENING SCHEDULE", 3));
            sheet.Merges.Add("A11:G11");

            sheet.AddRow(
                XlsxCell.Text("Wall", 2),
                XlsxCell.Text("Opening", 2),
                XlsxCell.Text("Opening Size W x H", 2),
                XlsxCell.Text("Offset from Ref. Edge", 2),
                XlsxCell.Text("Invert from Base", 2),
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
                int excelRow = firstOpeningRow + i;
                string nth = "ROWS($A$" + firstOpeningRow + ":A" + excelRow + ")";
                string match =
                    "AGGREGATE(15,6,(ROW('DATA_OPENINGS'!$A$2:$A$" + dataLastRow +
                    ")-ROW('DATA_OPENINGS'!$A$2)+1)/('DATA_OPENINGS'!$A$2:$A$" + dataLastRow +
                    "=$B$3)," + nth + ")";

                sheet.AddRow(
                    XlsxCell.Formula(
                        "IFERROR(INDEX('DATA_OPENINGS'!$B$2:$B$" + dataLastRow + "," + match + "),\"\")"),
                    XlsxCell.Formula(
                        "IFERROR(INDEX('DATA_OPENINGS'!$C$2:$C$" + dataLastRow + "," + match + "),\"\")"),
                    XlsxCell.Formula(
                        "IFERROR(TEXT(INDEX('DATA_OPENINGS'!$D$2:$D$" + dataLastRow + "," + match + "),\"0\")&\" x \"&TEXT(INDEX('DATA_OPENINGS'!$E$2:$E$" + dataLastRow + "," + match + "),\"0\"),\"\")"),
                    XlsxCell.Formula(
                        "IFERROR(INDEX('DATA_OPENINGS'!$F$2:$F$" + dataLastRow + "," + match + "),\"\")",
                        5),
                    XlsxCell.Formula(
                        "IFERROR(INDEX('DATA_OPENINGS'!$G$2:$G$" + dataLastRow + "," + match + "),\"\")",
                        5),
                    XlsxCell.Formula(
                        "IFERROR(INDEX('DATA_OPENINGS'!$H$2:$H$" + dataLastRow + "," + match + ")&IF(INDEX('DATA_OPENINGS'!$I$2:$I$" + dataLastRow + "," + match + ")<>\"\",\" / \"&INDEX('DATA_OPENINGS'!$I$2:$I$" + dataLastRow + "," + match + "),\"\"),\"\")"),
                    XlsxCell.Formula(
                        "IFERROR(INDEX('DATA_OPENINGS'!$J$2:$J$" + dataLastRow + "," + match + "),\"\")"));
            }

            for (int i = 0; i < data.Manholes.Count; i++)
            {
                int row = i + 2;
                SetCell(sheet, row, 10, XlsxCell.Text(data.Manholes[i].ManholeNumber));
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

            double[] widths = { 11, 14, 19, 18, 17, 27, 18 };
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
                sheet.Merges.Add("A" + pageStartRow + ":G" + pageStartRow);

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
                    "Opening positions: horizontal offset is opening C/L from the wall reference edge; invert is measured from base top.",
                    8));
                sheet.Merges.Add("A" + (pageStartRow + 3) + ":G" + (pageStartRow + 3));

                sheet.AddRow(XlsxCell.Blank());

                sheet.AddRow(
                    XlsxCell.Text("Wall", 2),
                    XlsxCell.Text("Opening", 2),
                    XlsxCell.Text("Opening Size W x H", 2),
                    XlsxCell.Text("Offset Ref. Edge", 2),
                    XlsxCell.Text("Invert Base", 2),
                    XlsxCell.Text("Service / System", 2),
                    XlsxCell.Text("Type", 2));

                if (openings.Count == 0)
                {
                    int noOpenRow = sheet.Rows.Count + 1;
                    sheet.AddRow(XlsxCell.Text("No managed fabrication openings", 8));
                    sheet.Merges.Add("A" + noOpenRow + ":G" + noOpenRow);
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
                sheet.Merges.Add("A" + noteRow + ":G" + noteRow);

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
                XlsxCell.Text("Manhole"),
                XlsxCell.Text("Wall"),
                XlsxCell.Text("Opening"),
                XlsxCell.Text("Width"),
                XlsxCell.Text("Height"),
                XlsxCell.Text("Offset"),
                XlsxCell.Text("InvertFromBase"),
                XlsxCell.Text("Service"),
                XlsxCell.Text("System"),
                XlsxCell.Text("Type"),
                XlsxCell.Text("Status"));

            foreach (ManufacturerOpeningRow r in data.Openings)
            {
                sheet.AddRow(
                    XlsxCell.Text(r.ManholeNumber),
                    XlsxCell.Text("W" + r.WallNumber),
                    XlsxCell.Text(r.OpeningNumber),
                    XlsxCell.Number(r.OpeningWidthMm),
                    XlsxCell.Number(r.OpeningHeightMm),
                    XlsxCell.Number(r.OffsetMm),
                    XlsxCell.Number(r.InvertFromBaseMm),
                    XlsxCell.Text(r.ServiceCategory),
                    XlsxCell.Text(r.SystemName),
                    XlsxCell.Text(r.OpeningType),
                    XlsxCell.Text(r.Status));
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
