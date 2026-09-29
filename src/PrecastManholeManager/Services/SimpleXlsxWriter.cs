using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class XlsxSheet
    {
        public string Name { get; set; }
        public List<List<XlsxCell>> Rows { get; } = new List<List<XlsxCell>>();
        public List<string> Merges { get; } = new List<string>();
        public Dictionary<int, double> ColumnWidths { get; } = new Dictionary<int, double>();
        public int FreezeRows { get; set; }

        public void AddRow(params XlsxCell[] cells)
        {
            Rows.Add(cells.ToList());
        }
    }

    internal sealed class XlsxCell
    {
        public object Value { get; set; }
        public int Style { get; set; }

        public static XlsxCell Text(string value, int style = 4)
        {
            return new XlsxCell { Value = value ?? string.Empty, Style = style };
        }

        public static XlsxCell Number(double value, int style = 5)
        {
            return new XlsxCell { Value = value, Style = style };
        }

        public static XlsxCell Integer(int value, int style = 5)
        {
            return new XlsxCell { Value = value, Style = style };
        }

        public static XlsxCell Blank(int style = 0)
        {
            return new XlsxCell { Value = string.Empty, Style = style };
        }
    }

    internal static class SimpleXlsxWriter
    {
        public static void Write(string path, IList<XlsxSheet> sheets)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("XLSX path is empty.");

            if (sheets == null || sheets.Count == 0)
                throw new ArgumentException("At least one worksheet is required.");

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (File.Exists(path))
                File.Delete(path);

            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "[Content_Types].xml", BuildContentTypes(sheets.Count));
                WriteEntry(archive, "_rels/.rels", BuildRootRelationships());
                WriteEntry(archive, "xl/workbook.xml", BuildWorkbook(sheets));
                WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationships(sheets.Count));
                WriteEntry(archive, "xl/styles.xml", BuildStyles());

                for (int i = 0; i < sheets.Count; i++)
                {
                    WriteEntry(
                        archive,
                        "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml",
                        BuildWorksheet(sheets[i]));
                }
            }
        }

        private static string BuildContentTypes(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");

            for (int i = 1; i <= sheetCount; i++)
            {
                sb.Append("<Override PartName=\"/xl/worksheets/sheet");
                sb.Append(i);
                sb.Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            }

            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildRootRelationships()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>";
        }

        private static string BuildWorkbook(IList<XlsxSheet> sheets)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<bookViews><workbookView/></bookViews>");
            sb.Append("<sheets>");

            for (int i = 0; i < sheets.Count; i++)
            {
                sb.Append("<sheet name=\"");
                sb.Append(Xml(sheets[i].Name));
                sb.Append("\" sheetId=\"");
                sb.Append(i + 1);
                sb.Append("\" r:id=\"rId");
                sb.Append(i + 1);
                sb.Append("\"/>");
            }

            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string BuildWorkbookRelationships(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

            for (int i = 1; i <= sheetCount; i++)
            {
                sb.Append("<Relationship Id=\"rId");
                sb.Append(i);
                sb.Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet");
                sb.Append(i);
                sb.Append(".xml\"/>");
            }

            sb.Append("<Relationship Id=\"rId");
            sb.Append(sheetCount + 1);
            sb.Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildStyles()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"5\">" +
                "<font><sz val=\"10\"/><name val=\"Calibri\"/></font>" +
                "<font><b/><sz val=\"16\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
                "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
                "<font><b/><sz val=\"10\"/><name val=\"Calibri\"/></font>" +
                "<font><i/><sz val=\"9\"/><color rgb=\"FF666666\"/><name val=\"Calibri\"/></font>" +
                "</fonts>" +
                "<fills count=\"5\">" +
                "<fill><patternFill patternType=\"none\"/></fill>" +
                "<fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF17365D\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9EAF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "</fills>" +
                "<borders count=\"2\">" +
                "<border><left/><right/><top/><bottom/><diagonal/></border>" +
                "<border><left style=\"thin\"><color rgb=\"FFD9D9D9\"/></left><right style=\"thin\"><color rgb=\"FFD9D9D9\"/></right><top style=\"thin\"><color rgb=\"FFD9D9D9\"/></top><bottom style=\"thin\"><color rgb=\"FFD9D9D9\"/></bottom><diagonal/></border>" +
                "</borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"9\">" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
                "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\"/></xf>" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" +
                "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\"/></xf>" +
                "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\"/></xf>" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" +
                "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" +
                "</cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                "</styleSheet>";
        }

        private static string BuildWorksheet(XlsxSheet sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            if (sheet.FreezeRows > 0)
            {
                sb.Append("<pane ySplit=\"");
                sb.Append(sheet.FreezeRows);
                sb.Append("\" topLeftCell=\"A");
                sb.Append(sheet.FreezeRows + 1);
                sb.Append("\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            }
            sb.Append("</sheetView></sheetViews>");

            if (sheet.ColumnWidths.Count > 0)
            {
                sb.Append("<cols>");
                foreach (KeyValuePair<int, double> width in sheet.ColumnWidths.OrderBy(x => x.Key))
                {
                    sb.Append("<col min=\"");
                    sb.Append(width.Key);
                    sb.Append("\" max=\"");
                    sb.Append(width.Key);
                    sb.Append("\" width=\"");
                    sb.Append(width.Value.ToString("0.##", CultureInfo.InvariantCulture));
                    sb.Append("\" customWidth=\"1\"/>");
                }
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");

            for (int rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
            {
                int excelRow = rowIndex + 1;
                sb.Append("<row r=\"");
                sb.Append(excelRow);
                sb.Append("\">");

                List<XlsxCell> row = sheet.Rows[rowIndex];
                for (int colIndex = 0; colIndex < row.Count; colIndex++)
                {
                    XlsxCell cell = row[colIndex] ?? XlsxCell.Blank();
                    string reference = ColumnName(colIndex + 1) + excelRow;

                    if (cell.Value is int || cell.Value is long || cell.Value is float ||
                        cell.Value is double || cell.Value is decimal)
                    {
                        sb.Append("<c r=\"");
                        sb.Append(reference);
                        sb.Append("\" s=\"");
                        sb.Append(cell.Style);
                        sb.Append("\"><v>");
                        sb.Append(Convert.ToString(cell.Value, CultureInfo.InvariantCulture));
                        sb.Append("</v></c>");
                    }
                    else
                    {
                        sb.Append("<c r=\"");
                        sb.Append(reference);
                        sb.Append("\" s=\"");
                        sb.Append(cell.Style);
                        sb.Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                        sb.Append(Xml(Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? string.Empty));
                        sb.Append("</t></is></c>");
                    }
                }

                sb.Append("</row>");
            }

            sb.Append("</sheetData>");

            if (sheet.Merges.Count > 0)
            {
                sb.Append("<mergeCells count=\"");
                sb.Append(sheet.Merges.Count);
                sb.Append("\">");
                foreach (string merge in sheet.Merges)
                {
                    sb.Append("<mergeCell ref=\"");
                    sb.Append(Xml(merge));
                    sb.Append("\"/>");
                }
                sb.Append("</mergeCells>");
            }

            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static void WriteEntry(ZipArchive archive, string path, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string Xml(string value)
        {
            return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
        }

        private static string ColumnName(int number)
        {
            string name = string.Empty;
            while (number > 0)
            {
                int modulo = (number - 1) % 26;
                name = Convert.ToChar('A' + modulo) + name;
                number = (number - modulo) / 26;
            }
            return name;
        }
    }
}
