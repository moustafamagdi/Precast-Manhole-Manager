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
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(desktop, $"PrecastManholePenetrations_{stamp}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("Link,LinkedElementId,Category,FamilyType,System,Size,WallNo,HostWallId,X_mm,Y_mm,Z_mm,Invert_mm,InvertAboveBase_mm,OffsetFromWallStart_mm,Notes");

            foreach (var r in records ?? Enumerable.Empty<PenetrationRecord>())
            {
                sb.AppendLine(string.Join(",",
                    Q(r.LinkName),
                    r.LinkedElementId,
                    Q(r.Category),
                    Q(r.FamilyType),
                    Q(r.SystemName),
                    Q(r.Size),
                    r.WallNumber,
                    r.HostWallId,
                    F(r.Xmm),
                    F(r.Ymm),
                    F(r.Zmm),
                    F(r.InvertMm),
                    F(r.InvertAboveBaseMm),
                    F(r.OffsetFromWallStartMm),
                    Q(r.Notes)));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string F(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Q(string value)
        {
            string quote = ((char)34).ToString();
            string safe = (value ?? string.Empty).Replace(quote, quote + quote);
            return quote + safe + quote;
        }
    }
}
