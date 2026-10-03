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
        public ReviewDomain Domain { get; set; } = ReviewDomain.Legacy;
        public string Evidence { get; set; } = "";
        public string AcceptedBy { get; set; } = "";
        public string AcceptanceNote { get; set; } = "";
        public string Display =>
            "Foundation " + FoundationId + " | " + Severity + " | " + Reason;
    }

    // Durable per-RVT-path local register. Does NOT write project elements,
    // and never purges records when a run does not report an issue.
    // Includes UniqueId to reject recycled ElementIds.
    internal static class ManholeReviewRegistry
    {
        private static readonly object Gate = new object();

        internal static bool SameReviewReason(string previous, string current)
        {
            Func<string, string> normalize = value => string.Join(";", (value ?? "")
                .Split(new[] { ';', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => System.Text.RegularExpressions.Regex.Replace(x.Trim(), @"\s+", " "))
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)).ToUpperInvariant();
            return normalize(previous) == normalize(current);
        }

        public static void Resolve(Document doc, Element foundation, ReviewDomain domain)
        {
            lock (Gate)
            {
                string path = RegisterPath(doc);
                var rows = LoadAt(path);
                if (!rows.Any(x => x.FoundationUniqueId == foundation.UniqueId && x.Domain == domain && x.Status != "RESOLVED")) return;
                ReviewState.Resolve(rows, foundation.UniqueId, domain);
                SaveAt(path, rows);
            }
        }

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
                // ACC/BIM360 cloud paths are identifiers, not local
                // filesystem paths. Hash the full logical location as-is.
                string identity = full.Contains("://") ? full
                    : Path.GetFullPath(full);
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    identity.ToUpperInvariant()));
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
            string severity, DiagnosticLogger log, ReviewDomain domain = ReviewDomain.Legacy,
            bool replace = false, string evidence = "")
        {
            if (foundation == null) return;
            lock (Gate)
            {
                string path = RegisterPath(doc);
                List<ManholeReviewIssue> issues = LoadAt(path);
                var row = issues.FirstOrDefault(x =>
                    x.FoundationUniqueId == foundation.UniqueId && x.Domain == domain);
                if (row == null)
                {
                    row = new ManholeReviewIssue
                    {
                        FoundationUniqueId = foundation.UniqueId,
                        Domain = domain,
                        Status = "OPEN"
                    };
                    issues.Add(row);
                }
                row.FoundationId = foundation.Id.IntegerValue;
                string newReason = reason ?? "Review required";
                var affected = (wallIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(x => x).ToList();
                if (string.IsNullOrEmpty(evidence)) evidence = ReviewEvidence.ForDomain(doc, foundation, affected, domain);
                ReviewState.Update(row, newReason, severity ?? "REVIEW",
                    string.Join(",", affected), evidence, replace);
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
            sb.AppendLine("FoundationId,Status,Severity,Reason,WallIds,ReviewViewId,ReviewViewName,UpdatedUtc,FoundationUniqueId,Domain,AcceptedBy,AcceptanceNote,Readiness");
            var all = values.ToList();
            foreach (ManholeReviewIssue issue in all.OrderBy(x =>
                x.FoundationId))
                sb.AppendLine(string.Join(",", new[]
                {
                    issue.FoundationId.ToString(CultureInfo.InvariantCulture),
                    Csv(issue.Status), Csv(issue.Severity),
                    Csv(issue.Reason), Csv(issue.WallIds),
                    issue.ViewId.ToString(CultureInfo.InvariantCulture),
                    Csv(issue.ViewName), Csv(issue.UpdatedUtc),
                    Csv(issue.FoundationUniqueId), issue.Domain.ToString(), Csv(issue.AcceptedBy), Csv(issue.AcceptanceNote),
                    Csv(ReviewState.Readiness(all.Where(x => x.FoundationUniqueId == issue.FoundationUniqueId)))
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

        public static string ExportReadiness(Document doc, string path, ISet<string> scope = null)
        {
            var items = SimpleProjectScanService.LoadFast(doc).Where(x => scope == null || scope.Contains(x.UniqueId)).ToList();
            var report = new StringBuilder("FoundationId,Manhole,ReviewStatus,DeliveryReadiness,Issues\r\n");
            foreach (var item in items)
                report.AppendLine(item.FoundationId + "," + Csv(item.ManholeName) + "," + Csv(item.State) + "," + Csv(item.Readiness) + "," + Csv(item.Problem));
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
            ExportReadableCsv(doc, Load(doc));
            return "Review snapshot: " + items.Count(x => x.State == "REVIEW") + " require review; " +
                items.Count(x => x.State == "IGNORED") + " manually accepted. Delivery is not certified by phase completion.\nReadiness report: " + path;
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
                    if (p.Length != 10 && p.Length != 14) throw new FormatException("Unexpected column count.");
                    if ((p.Length == 10 && p[9] != "1") || (p.Length == 14 && p[9] != "2")) throw new FormatException("Unsupported review schema version.");
                    if (p.Length == 14 && !Enum.IsDefined(typeof(ReviewDomain), p[10])) throw new FormatException("Unknown review domain.");
                    string status = Dec(p[3]);
                    if (status != "OPEN" && status != "IGNORED" && status != "RESOLVED") throw new FormatException("Unknown review status.");
                    rows.Add(new ManholeReviewIssue
                    {
                        FoundationUniqueId = Dec(p[0]),
                        FoundationId = int.Parse(p[1],
                            CultureInfo.InvariantCulture),
                        Severity = Dec(p[2]),
                        Status = status,
                        Reason = Dec(p[4]),
                        WallIds = Dec(p[5]),
                        UpdatedUtc = Dec(p[6]),
                        ViewId = int.Parse(p[7],
                            CultureInfo.InvariantCulture),
                        ViewName = Dec(p[8]),
                        // Old reasons can mix domains. Never guess and silently clear them.
                        Domain = p.Length == 14 ? (ReviewDomain)Enum.Parse(typeof(ReviewDomain), p[10]) : ReviewDomain.Legacy,
                        Evidence = p.Length == 14 ? Dec(p[11]) : "",
                        AcceptedBy = p.Length == 14 ? Dec(p[12]) : "",
                        AcceptanceNote = p.Length == 14 ? Dec(p[13]) : ""
                    });
                }
                catch (Exception ex) { throw new InvalidDataException("Review register contains an unreadable row; original file retained: " + path, ex); }
            }
            return rows;
        }

        private static void SaveAt(string path, List<ManholeReviewIssue> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("UniqueId\tId\tSeverity\tStatus\tReason\tWalls\tUpdatedUtc\tViewId\tViewName\tVersion\tDomain\tEvidence\tAcceptedBy\tAcceptanceNote");
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
                    Enc(row.ViewName), "2", row.Domain.ToString(), Enc(row.Evidence), Enc(row.AcceptedBy), Enc(row.AcceptanceNote)
                }));
            }
            string temp = path + ".tmp";
            if (File.Exists(path))
            {
                // Validate before replacement; a malformed source is never silently discarded.
                LoadAt(path);
                if (File.ReadLines(path).FirstOrDefault()?.Split('\t').Length == 10 && !File.Exists(path + ".v1.bak"))
                    File.Copy(path, path + ".v1.bak");
            }
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
