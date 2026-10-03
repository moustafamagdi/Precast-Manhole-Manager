using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ManholeRecheckService
    {
        public static string RunAll(Document doc, double clearanceMm, DiagnosticLogger log, bool reviewOnly = false)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using (var cache = new LinkedMepScanCache())
            {
                string result = RunAllIndexed(doc, clearanceMm, log, reviewOnly);
                log.Info("CLEAN SCAN TOTAL SECONDS=" + timer.Elapsed.TotalSeconds.ToString("0.0"));
                return result + "\nElapsed: " + timer.Elapsed.TotalMinutes.ToString("0.0") + " min.";
            }
        }

        private static string RunAllIndexed(Document doc, double clearanceMm, DiagnosticLogger log, bool reviewOnly)
        {
            if (double.IsNaN(clearanceMm) || double.IsInfinity(clearanceMm) || clearanceMm < 0)
                throw new InvalidOperationException("Enter a finite non-negative clearance.");
            var items = SimpleProjectScanService.LoadFast(doc);
            int unavailable = 0;
            if (reviewOnly)
            {
                var openIds = new HashSet<string>(ManholeReviewRegistry.Load(doc)
                    .Where(x => x.Status == "OPEN").Select(x => x.FoundationUniqueId), StringComparer.Ordinal);
                items = items.Where(x => openIds.Contains(x.UniqueId)).ToList();
                unavailable = openIds.Except(items.Select(x => x.UniqueId)).Count();
                log.Info("RECHECK REVIEW ONLY Matched=" + items.Count + " Unavailable=" + unavailable);
                if (openIds.Count == 0) return "No OPEN review cases in this model.";
            }
            int passed = 0, review = 0, errors = 0, ignored = 0;
            var report = new System.Text.StringBuilder("FoundationId,InternalId,Result,Details\r\n");
            log.WriteHeader(reviewOnly ? "RECHECK OPEN REVIEW CASES - NO MODEL CHANGES" : "CLEAN SCAN - ALL MANHOLES - NO MODEL CHANGES");
            foreach (var item in items)
            {
                Element foundation = doc.GetElement(new ElementId(item.FoundationId));
                string status, details;
                try
                {
                    if (foundation == null || foundation.UniqueId != item.UniqueId)
                        throw new InvalidOperationException("Foundation identity changed during scan.");
                    log.Info("CLEAN SCAN " + (passed + review + errors + ignored + 1) + "/" + items.Count +
                        " Foundation=" + item.FoundationId);
                    details = Run(doc, foundation, clearanceMm, log);
                    var current = ManholeReviewRegistry.Load(doc).FirstOrDefault(x => x.FoundationUniqueId == foundation.UniqueId);
                    status = current?.Status == "IGNORED" ? "IGNORED" : current?.Status == "OPEN" ? "REVIEW" : "RECHECK PASSED";
                    if (status == "IGNORED") ignored++; else if (status == "REVIEW") review++; else passed++;
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
            string path = System.IO.Path.ChangeExtension(log.LogPath, reviewOnly ? ".ReviewRecheck.csv" : ".CleanScan.csv");
            System.IO.File.WriteAllText(path, report.ToString(), new System.Text.UTF8Encoding(true));
            ManholeReviewRegistry.ExportReadableCsv(doc, ManholeReviewRegistry.Load(doc));
            return (reviewOnly ? "Review recheck completed for " : "Clean scan completed for ") + items.Count + " manholes.\nPassed: " + passed +
                "\nIgnored by user: " + ignored + "\nStill require review: " + review + "\nScan errors: " + errors +
                (unavailable > 0 ? "\nReview records not found among current manholes: " + unavailable + " (kept OPEN)." : "") +
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
                var review = UnifiedOpeningReviewService.Collect(doc, foundation, footprint, log, clearanceMm, 150, VirtualMepExtensionScanner.ProductionMaxApproachDeg);
                csv = UnifiedOpeningReviewService.ExportCsv(review);
                var plan = CleanSyncPlanService.Build(doc, foundation.Id.IntegerValue, footprint, review, log);
                reasons.AddRange(ProductionPreflightService.PhysicalBlockers(plan));
                var actual = review.Rows.Where(r => !r.IsVirtual || r.EndpointQualified).ToList();
                actualCount = actual.Count;
                if (actualCount == 0)
                    reasons.Add("NO ELIGIBLE SERVICES: no pipe/duct crossing or qualified end connector within 150 mm. Check manhole position and loaded MEP links; existing openings alone do not prove service coverage.");
                foreach (var pair in plan.ManagedOpeningIds)
                {
                    try
                    {
                        var opening = doc.GetElement(new ElementId(pair.Value)) as Opening;
                        var members = CompoundOpeningService.ReadMembers(opening, pair.Key);
                        CompoundOpeningService.AuthorizeReplacement(members, actual.Select(r => r.Source.SourceKey), true);
                    }
                    catch (Exception ex) { reasons.Add("Existing opening " + pair.Value + ": " + ex.Message); }
                }
                var physicalCuts = OpeningCoverageService.Read(doc, plan.WallIds, log);
                foreach (var row in actual)
                {
                    if (row.Source.EdgeAligned && Math.Abs(row.Source.EdgeShiftMm) > 0.1)
                        reasons.Add("W" + row.Source.WallNumber + " Source=" + row.SourceId + ": pipe/duct site adjustment " + row.Source.EdgeShiftMm.ToString("0.#") + " mm required.");
                    string why;
                    if (!OpeningFitValidationService.TryValidate(doc, row.Source, out why))
                        reasons.Add("W" + row.Source.WallNumber + " Source=" + row.SourceId + ": " + why);
                    else
                    {
                        var host = doc.GetElement(new ElementId(row.Source.HostWallId)) as Wall;
                        var axis = (host?.Location as LocationCurve)?.Curve as Line;
                        if (axis == null || !OpeningCoverageService.Covers(physicalCuts, row.Source, axis.Direction.X, axis.Direction.Y))
                            reasons.Add("MISSING OPENING W" + row.Source.WallNumber + " Source=" + row.SourceId +
                                ": no verified rectangular opening covers the required position and size " +
                                row.Source.CutWidthMm.ToString("0.#") + " x " + row.Source.CutHeightMm.ToString("0.#") +
                                " mm including clearance. Missing, undersized or displaced cut; profile/void cuts require separate review.");
                    }
                    if (row.Status != "ACTUAL FIT PREVIEW" && row.Source.ExistingOpeningStatus != "MANAGED")
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
                    if (CompoundOpeningService.ExistingCutCoversPair(doc, plan, a, b, line.Direction)) continue;
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
                bool ignored = currentIssues.Any(x => x.FoundationUniqueId == foundation.UniqueId && x.Status == "IGNORED");
                return (ignored ? "IGNORED by user (same reasons):\n" : "Still requires REVIEW:\n") + string.Join("\n", reasons) +
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

    }
}
