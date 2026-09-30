using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ManholeRecheckService
    {
        public static string RunAll(Document doc, double clearanceMm, DiagnosticLogger log)
        {
            if (double.IsNaN(clearanceMm) || double.IsInfinity(clearanceMm) || clearanceMm < 0)
                throw new InvalidOperationException("Enter a finite non-negative clearance.");
            var items = SimpleProjectScanService.LoadFast(doc);
            int passed = 0, review = 0, errors = 0;
            var report = new System.Text.StringBuilder("FoundationId,InternalId,Result,Details\r\n");
            log.WriteHeader("CLEAN SCAN - ALL MANHOLES - NO MODEL CHANGES");
            foreach (var item in items)
            {
                Element foundation = doc.GetElement(new ElementId(item.FoundationId));
                string status, details;
                try
                {
                    if (foundation == null || foundation.UniqueId != item.UniqueId)
                        throw new InvalidOperationException("Foundation identity changed during scan.");
                    log.Info("CLEAN SCAN " + (passed + review + errors + 1) + "/" + items.Count +
                        " Foundation=" + item.FoundationId);
                    details = Run(doc, foundation, clearanceMm, log);
                    bool open = ManholeReviewRegistry.Load(doc).Any(x =>
                        x.FoundationUniqueId == foundation.UniqueId && x.Status == "OPEN");
                    status = open ? "REVIEW" : "RECHECK PASSED";
                    if (open) review++; else passed++;
                }
                catch (Exception ex)
                {
                    errors++;
                    status = "ERROR";
                    details = ex.Message;
                    log.Error("CLEAN SCAN FAILED Foundation=" + item.FoundationId, ex);
                    if (foundation != null && foundation.UniqueId == item.UniqueId)
                        ManholeReviewRegistry.Upsert(doc, foundation, "Clean scan error: " + ex.Message,
                            null, "ERROR", log);
                }
                report.AppendLine(item.FoundationId + "," + Csv(item.ManholeName) + "," + status + "," + Csv(details));
            }
            string path = System.IO.Path.ChangeExtension(log.LogPath, ".CleanScan.csv");
            System.IO.File.WriteAllText(path, report.ToString(), new System.Text.UTF8Encoding(true));
            ManholeReviewRegistry.ExportReadableCsv(doc, ManholeReviewRegistry.Load(doc));
            return "Clean scan completed for " + items.Count + " manholes.\nPassed: " + passed +
                "\nStill require review: " + review + "\nScan errors: " + errors +
                "\nRepaired issues have been resolved. No openings, walls or views were changed." +
                "\nProduction prerequisites still apply to passed manholes.\nReport: " + path;
        }

        private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

        public static string Run(Document doc, Element foundation, double clearanceMm, DiagnosticLogger log)
        {
            if (double.IsNaN(clearanceMm) || double.IsInfinity(clearanceMm) || clearanceMm < 0)
                throw new InvalidOperationException("Enter a finite non-negative clearance.");
            var footprint = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
            var reasons = new List<string>();
            string csv = "";
            int actualCount = 0;
            if (!footprint.Accepted) reasons.Add("Wall footprint: " + footprint.Reason);
            else
            {
                var review = UnifiedOpeningReviewService.Collect(doc, foundation, footprint, log, clearanceMm, 150, 15);
                csv = UnifiedOpeningReviewService.ExportCsv(review);
                var plan = CleanSyncPlanService.Build(doc, foundation.Id.IntegerValue, footprint, review, log);
                reasons.AddRange(PhysicalBlockers(plan));
                var actual = review.Rows.Where(r => !r.IsVirtual).ToList();
                actualCount = actual.Count;
                foreach (var row in actual)
                {
                    string why;
                    if (!OpeningFitValidationService.TryValidate(doc, row.Source, out why))
                        reasons.Add("W" + row.Source.WallNumber + " Source=" + row.SourceId + ": " + why);
                    else if (row.Status != "ACTUAL FIT PREVIEW" && row.Source.ExistingOpeningStatus != "MANAGED")
                        reasons.Add("W" + row.Source.WallNumber + ": " + row.Notes);
                }
                for (int i = 0; i < actual.Count; i++)
                for (int j = i + 1; j < actual.Count; j++)
                {
                    var a = actual[i].Source;
                    var b = actual[j].Source;
                    if (a.HostWallId != b.HostWallId) continue;
                    var wall = doc.GetElement(new ElementId(a.HostWallId)) as Wall;
                    var line = (wall?.Location as LocationCurve)?.Curve as Line;
                    if (line == null) { reasons.Add("Cannot validate opening spacing."); continue; }
                    var delta = new XYZ(a.EffectiveOpeningXmm - b.EffectiveOpeningXmm,
                        a.EffectiveOpeningYmm - b.EffectiveOpeningYmm, a.EffectiveOpeningZmm - b.EffectiveOpeningZmm);
                    if (Math.Abs(delta.DotProduct(line.Direction)) < (a.CutWidthMm + b.CutWidthMm) * 0.5 + 5 &&
                        Math.Abs(delta.Z) < (a.CutHeightMm + b.CutHeightMm) * 0.5 + 5)
                        reasons.Add("Overlapping openings on W" + a.WallNumber + ": " + a.LinkedElementId + " / " + b.LinkedElementId);
                }
            }
            reasons = reasons.Distinct().ToList();
            if (reasons.Count > 0)
            {
                ManholeReviewRegistry.Upsert(doc, foundation, string.Join("; ", reasons),
                    footprint.Accepted ? footprint.Walls.Select(w => w.Id.IntegerValue) : null, "RECHECK", log);
                var currentIssues = ManholeReviewRegistry.Load(doc);
                foreach (var current in currentIssues.Where(x => x.FoundationUniqueId == foundation.UniqueId))
                {
                    log.Info("RECHECK PREVIOUS REASONS: " + current.Reason);
                    current.Reason = string.Join("; ", reasons);
                }
                ManholeReviewRegistry.Save(doc, currentIssues);
                ManholeReviewRegistry.ExportReadableCsv(doc, currentIssues);
                return "Still requires REVIEW:\n" + string.Join("\n", reasons) +
                    "\n\nIf the wall still has an edited sketch, use Reset Profile before rechecking." +
                    "\nReview CSV: " + csv;
            }
            var issues = ManholeReviewRegistry.Load(doc);
            foreach (var issue in issues.Where(x => x.FoundationUniqueId == foundation.UniqueId))
            {
                log.Info("RECHECK RESOLVED Foundation=" + foundation.Id.IntegerValue + " PreviousReason=" + issue.Reason);
                issue.Status = "RESOLVED";
                issue.Severity = "RECHECK PASSED";
                issue.UpdatedUtc = DateTime.UtcNow.ToString("O");
            }
            ManholeReviewRegistry.Save(doc, issues);
            ManholeReviewRegistry.ExportReadableCsv(doc, issues);
            return "Recheck passed. This manhole is no longer in the OPEN review queue.\n" +
                "Actual crossings: " + actualCount + " | Clearance per side: " + clearanceMm + " mm." +
                "\nYou can now run Generate Selected Manhole; its final production checks still apply." +
                "\nNo model geometry was changed.\nReview CSV: " + csv;
        }

        internal static List<string> PhysicalBlockers(CleanSyncPlan plan)
        {
            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(plan.BlockReason)) reasons.Add(plan.BlockReason);
            if (plan.ProfileResetCount > 0) reasons.Add("Edited wall profiles remain: " + plan.ProfileResetCount);
            if (plan.VoidCutCount > 0) reasons.Add("Void cuts remain: " + plan.VoidCutCount);
            if (plan.ManualOpeningIds.Count > 0) reasons.Add("Non-tool openings remain: " + string.Join(",", plan.ManualOpeningIds));
            if (plan.InPlaceCutterCount > 0) reasons.Add("In-place cutters remain: " + string.Join(",", plan.InPlaceCutterWallIds.Keys));
            if (plan.SolidCutReviewReasons.Count > 0)
                reasons.AddRange(plan.SolidCutReviewReasons.Where(reason => !reasons.Contains(reason)));
            else if (plan.UnsupportedSolidCutWallIds.Count > 0) reasons.Add("Unsupported solid cuts: " + string.Join(",", plan.UnsupportedSolidCutWallIds));
            // Unloaded links are outside the operator-selected scope; existing managed openings are normal.
            return reasons;
        }
    }
}
