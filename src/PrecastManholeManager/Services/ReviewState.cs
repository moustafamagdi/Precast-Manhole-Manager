using System;
using System.Collections.Generic;
using System.Linq;

namespace Hatco.PrecastManholeManager.Services
{
    internal enum ReviewDomain { Legacy, Geometry, Openings, Views, Layout, Dimensions, Presentation, DrawingValidation }

    // Domain results are evidence from an operation, not a live certification of the model.
    internal static class ReviewState
    {
        internal static string Status(IEnumerable<ManholeReviewIssue> rows)
        {
            var list = rows.ToList();
            return list.Any(x => x.Status == "OPEN") ? "OPEN" :
                list.Any(x => x.Status == "IGNORED") ? "IGNORED" : "RESOLVED";
        }

        internal static string Readiness(IEnumerable<ManholeReviewIssue> rows)
        {
            var list = rows.ToList();
            if (list.Any(x => x.Status == "OPEN")) return "REVIEW REQUIRED";
            if (list.Any(x => x.Status == "IGNORED")) return "MANUALLY ACCEPTED / NOT VERIFIED";
            // Layout and current view extents have no complete validator yet.
            // Never turn absence of reported problems into a delivery certificate.
            return "DELIVERY NOT VERIFIED";
        }

        internal static string Describe(IEnumerable<ManholeReviewIssue> rows) => string.Join(" | ",
            rows.Where(x => x.Status == "OPEN" || x.Status == "IGNORED")
                .Select(x => "[" + x.Domain + "/" + x.Status + "] " + x.Reason));

        internal static void Resolve(List<ManholeReviewIssue> rows, string uid, ReviewDomain domain)
        {
            foreach (var row in rows.Where(x => x.FoundationUniqueId == uid && x.Domain == domain))
            {
                row.Status = "RESOLVED";
                row.UpdatedUtc = DateTime.UtcNow.ToString("O");
                row.AcceptedBy = "";
                row.AcceptanceNote = "";
            }
        }

        internal static void Accept(ManholeReviewIssue issue, string user, string note)
        {
            if (issue.Status != "OPEN" && issue.Status != "IGNORED")
                throw new InvalidOperationException("Only active review issues can be accepted.");
            if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("Enter the reason for accepting this issue.");
            issue.Status = "IGNORED";
            issue.AcceptedBy = user;
            issue.AcceptanceNote = note.Trim();
            issue.UpdatedUtc = DateTime.UtcNow.ToString("O");
        }

        internal static void Update(ManholeReviewIssue row, string reason, string severity, string walls, string evidence, bool replace)
        {
            bool same = ManholeReviewRegistry.SameReviewReason(row.Reason, reason) &&
                row.Severity == severity && row.WallIds == walls && row.Evidence == evidence;
            if (row.Status == "IGNORED" && same) return;
            bool append = !replace && row.Status == "OPEN" && !string.IsNullOrWhiteSpace(row.Reason) &&
                !ManholeReviewRegistry.SameReviewReason(row.Reason, reason);
            row.Reason = append ? row.Reason.IndexOf(reason, StringComparison.OrdinalIgnoreCase) >= 0 ? row.Reason : row.Reason + " | " + reason : reason;
            row.Status = "OPEN";
            row.Severity = severity;
            row.WallIds = walls;
            row.Evidence = evidence;
            row.AcceptedBy = "";
            row.AcceptanceNote = "";
            row.UpdatedUtc = DateTime.UtcNow.ToString("O");
        }
    }
}
