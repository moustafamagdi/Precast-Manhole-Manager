using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Infrastructure
{
    internal static class CsvExporter
    {
        public static string Export(IEnumerable<PenetrationRecord> records)
        {
            string logsFolder = OutputPathService.GetLogsFolder();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(logsFolder, "PrecastManholePenetrations_" + stamp + ".csv");

            var sb = new StringBuilder();
            sb.AppendLine("Accepted,Link,LinkedElementId,Category,FamilyType,System,MEP_Size,Shape,Diameter_mm,Width_mm,Height_mm,ClearancePerSide_mm,RecommendedOpening,WallNo,HostWallId,X_mm,Y_mm,Z_mm,Invert_mm,InvertAboveBase_mm,OffsetFromWallStart_mm,Notes");

            foreach (PenetrationRecord r in records ?? Enumerable.Empty<PenetrationRecord>())
            {
                sb.AppendLine(string.Join(",", new string[]
                {
                    r.Accepted ? "Yes" : "No",
                    Csv(r.LinkName),
                    r.LinkedElementId.ToString(CultureInfo.InvariantCulture),
                    Csv(r.Category),
                    Csv(r.FamilyType),
                    Csv(r.SystemName),
                    Csv(r.Size),
                    Csv(r.Shape),
                    F(r.DiameterMm),
                    F(r.WidthMm),
                    F(r.HeightMm),
                    F(r.ClearanceMm),
                    Csv(r.OpeningSize),
                    r.WallNumber.ToString(CultureInfo.InvariantCulture),
                    r.HostWallId.ToString(CultureInfo.InvariantCulture),
                    F(r.Xmm),
                    F(r.Ymm),
                    F(r.Zmm),
                    F(r.InvertMm),
                    F(r.InvertAboveBaseMm),
                    F(r.OffsetFromWallStartMm),
                    Csv(r.Notes)
                }));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string F(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Csv(string value)
        {
            char q = (char)34;
            string text = value ?? string.Empty;
            text = text.Replace(q.ToString(), new string(q, 2));
            return q + text + q;
        }
    }
}
