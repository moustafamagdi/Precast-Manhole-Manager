using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class ManholeReviewIssue
    {
        public string FoundationUniqueId { get; set; }
        public int FoundationId { get; set; }
        public string Reason { get; set; }
        public string Severity { get; set; }
        public string Status { get; set; }
        public string UpdatedUtc { get; set; }
        public int ViewId { get; set; }
        public string ViewName { get; set; }
        public string WallIds { get; set; }
        public string Display =>
            "Foundation " + FoundationId + " | " + Severity + " | " + Reason;
    }

    // Durable per-RVT-path local register. Does NOT write project elements,
    // and never purges records when a run does not report an issue.
    // Includes UniqueId to reject recycled ElementIds.
    internal static class ManholeReviewRegistry
    {
        private static readonly object Gate = new object();

        public static string RegisterPath(Document doc)
        {
            string full = doc.PathName ?? string.Empty;
            // Do not merge unnamed test files with saved projects.
            if (string.IsNullOrWhiteSpace(full))
                throw new InvalidOperationException(
                    "Save the RVT test file before recording review issues.");
            string key;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(full).ToUpperInvariant()));
                key = BitConverter.ToString(hash, 0, 10).Replace("-", "");
            }
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Precast Manhole Manager", "Review Register");
            Directory.CreateDirectory(folder);
            string safeName = new string(Path.GetFileNameWithoutExtension(full)
                .Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')
                .Take(45).ToArray());
            return Path.Combine(folder,
                (string.IsNullOrWhiteSpace(safeName) ? "Project" : safeName) +
                "_" + key + ".tsv");
        }

        public static List<ManholeReviewIssue> Load(Document doc)
        {
            lock (Gate)
            {
                return LoadAt(RegisterPath(doc));
            }
        }

        public static void Upsert(Document doc, Element foundation,
            string reason, IEnumerable<int> wallIds,
            string severity, DiagnosticLogger log)
        {
            if (foundation == null) return;
            lock (Gate)
            {
                string path = RegisterPath(doc);
                List<ManholeReviewIssue> issues = LoadAt(path);
                var row = issues.FirstOrDefault(x =>
                    x.FoundationUniqueId == foundation.UniqueId);
                if (row == null)
                {
                    row = new ManholeReviewIssue
                    {
                        FoundationUniqueId = foundation.UniqueId,
                        Status = "OPEN"
                    };
                    issues.Add(row);
                }
                row.FoundationId = foundation.Id.IntegerValue;
                row.Reason = reason ?? "Review required";
                row.Severity = severity ?? "REVIEW";
                row.WallIds = string.Join(",",
                    (wallIds ?? Enumerable.Empty<int>()).Distinct());
                row.UpdatedUtc = DateTime.UtcNow.ToString("O",
                    CultureInfo.InvariantCulture);
                if (row.Status == "RESOLVED")
                    row.Status = "OPEN";
                SaveAt(path, issues);
                log?.Warn("ISSUE REGISTERED Foundation=" +
                    row.FoundationId + " Reason=" + row.Reason +
                    " Register=" + path);
            }
        }

        public static string ExportReadableCsv(Document doc,
            IEnumerable<ManholeReviewIssue> values)
        {
            string path = Path.ChangeExtension(RegisterPath(doc), ".csv");
            var sb = new StringBuilder();
            sb.AppendLine("FoundationId,Status,Severity,Reason,WallIds,ReviewViewId,ReviewViewName,UpdatedUtc,FoundationUniqueId");
            foreach (ManholeReviewIssue issue in values.OrderBy(x =>
                x.FoundationId))
                sb.AppendLine(string.Join(",", new[]
                {
                    issue.FoundationId.ToString(CultureInfo.InvariantCulture),
                    Csv(issue.Status), Csv(issue.Severity),
                    Csv(issue.Reason), Csv(issue.WallIds),
                    issue.ViewId.ToString(CultureInfo.InvariantCulture),
                    Csv(issue.ViewName), Csv(issue.UpdatedUtc),
                    Csv(issue.FoundationUniqueId)
                }));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string Csv(string s)
        {
            string quote = ((char)34).ToString();
            return quote + (s ?? string.Empty).Replace(quote, quote + quote) +
                quote;
        }

        public static void Save(Document doc,
            IEnumerable<ManholeReviewIssue> values)
        {
            lock (Gate)
            {
                SaveAt(RegisterPath(doc), values.ToList());
            }
        }

        private static List<ManholeReviewIssue> LoadAt(string path)
        {
            var rows = new List<ManholeReviewIssue>();
            if (!File.Exists(path)) return rows;
            foreach (string line in File.ReadAllLines(path,
                Encoding.UTF8).Skip(1))
            {
                try
                {
                    string[] p = line.Split('\t');
                    if (p.Length != 10) continue;
                    rows.Add(new ManholeReviewIssue
                    {
                        FoundationUniqueId = Dec(p[0]),
                        FoundationId = int.Parse(p[1],
                            CultureInfo.InvariantCulture),
                        Severity = Dec(p[2]),
                        Status = Dec(p[3]),
                        Reason = Dec(p[4]),
                        WallIds = Dec(p[5]),
                        UpdatedUtc = Dec(p[6]),
                        ViewId = int.Parse(p[7],
                            CultureInfo.InvariantCulture),
                        ViewName = Dec(p[8])
                    });
                }
                catch { /* Preserve good records despite malformed lines. */ }
            }
            return rows;
        }

        private static void SaveAt(string path, List<ManholeReviewIssue> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("UniqueId\tId\tSeverity\tStatus\tReason\tWalls\tUpdatedUtc\tViewId\tViewName\tVersion");
            foreach (ManholeReviewIssue row in rows.OrderBy(x =>
                x.FoundationId))
            {
                sb.AppendLine(string.Join("\t", new[]
                {
                    Enc(row.FoundationUniqueId),
                    row.FoundationId.ToString(CultureInfo.InvariantCulture),
                    Enc(row.Severity), Enc(row.Status), Enc(row.Reason),
                    Enc(row.WallIds), Enc(row.UpdatedUtc),
                    row.ViewId.ToString(CultureInfo.InvariantCulture),
                    Enc(row.ViewName), "1"
                }));
            }
            string temp = path + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(true));
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }

        private static string Enc(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        private static string Dec(string value) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
