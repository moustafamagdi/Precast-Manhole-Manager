using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        private static ProductionManholeResult GenerateWallOpenings(UIDocument uidoc, SimpleManholeItem item,
            DiagnosticLogger log, double clearance, bool merge, bool resetProfiles = false, bool missingOnly = false, bool deferDimensions = false)
        {
            if (missingOnly) resetProfiles = false;
            var doc = uidoc.Document;
            if (doc.IsReadOnly || doc.IsLinked || double.IsNaN(clearance) || double.IsInfinity(clearance) || clearance < 0)
                throw new InvalidOperationException("Editable host and finite non-negative clearance required.");
            var foundation = Resolve(doc, item);
            var footprint = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
            if (!footprint.Accepted) throw new InvalidOperationException(footprint.Reason);
            var review = UnifiedOpeningReviewService.Collect(doc, foundation, footprint, log, clearance, 150, VirtualMepExtensionScanner.ProductionMaxApproachDeg);
            string csv = UnifiedOpeningReviewService.ExportCsv(review);
            var safeWalls = new HashSet<int>();
            var problems = new List<string>();
            var outcomes = new List<string>();
            int newCuts = 0, updated = 0, unchanged = 0, candidateCount = 0, committedGroups = 0;
            foreach (var bodyWall in UnifiedOpeningReviewService.BuildManhole(foundation, footprint).Walls)
            {
                int wallId = bodyWall.Wall.Id.IntegerValue, number = bodyWall.Number;
                TransactionGroup profileGroup = null;
                int savedNew = newCuts, savedUpdated = updated, savedUnchanged = unchanged;
                int savedGroups = committedGroups, savedOutcomes = outcomes.Count, savedProblems = problems.Count;
                bool groupFailed = false;
                try
                {
                    if (resetProfiles && bodyWall.Wall.SketchId != ElementId.InvalidElementId)
                    {
                        profileGroup = new TransactionGroup(doc, "HATCO - Reset profile and open W" + number);
                        profileGroup.Start();
                        ResetSingleWallProfile(doc, bodyWall.Wall, foundation, number, log);
                    }
                    // Refresh geometry after each individual reset (or prior wall rollback).
                    if (resetProfiles)
                        review = UnifiedOpeningReviewService.Collect(doc, foundation, footprint, log, clearance, 150,
                            VirtualMepExtensionScanner.ProductionMaxApproachDeg);
                    // Audit this wall against the complete body, but authorize writes
                    // to this wall only. Failure cannot roll back earlier wall groups.
                    var plan = CleanSyncPlanService.Build(doc, foundation.Id.IntegerValue, footprint, review, log, wallId);
                    var blockers = ProductionPreflightService.PhysicalBlockers(plan);
                    if (blockers.Count > 0) throw new InvalidOperationException(string.Join("; ", blockers));
                    var rows = plan.ProposedRows.Where(r => !r.IsVirtual || r.EndpointQualified).ToList();
                    if (missingOnly)
                    {
                        var known = new HashSet<string>(plan.ManagedOpeningIds.SelectMany(pair =>
                            CompoundOpeningService.ReadMembers(doc.GetElement(new ElementId(pair.Value)) as Opening, pair.Key)));
                        var cuts = OpeningCoverageService.Read(doc, new[] { wallId }, log);
                        rows = rows.Where(r => !known.Contains(r.Source.SourceKey) &&
                            !OpeningCoverageService.Covers(cuts, r.Source, bodyWall.Direction.X, bodyWall.Direction.Y)).ToList();
                        log.Info("MISSING ONLY W" + number + " New candidates=" + rows.Count + "; existing cuts preserved");
                    }
                    candidateCount += rows.Count;
                    if (rows.Count == 0)
                    {
                        if (profileGroup != null) throw new InvalidOperationException("No eligible replacement openings; original profile retained.");
                        safeWalls.Add(number);
                        outcomes.Add("W" + number + ": no eligible crossing; existing cuts preserved");
                        continue;
                    }
                    double dx = bodyWall.Direction.X, dy = bodyWall.Direction.Y;
                    var oldGroups = plan.ManagedOpeningIds.Select(pair => CompoundOpeningService.ReadMembers(
                        doc.GetElement(new ElementId(pair.Value)) as Opening, pair.Key)).ToList();
                    var components = CompoundOpeningService.Partition(rows.Select(r => r.Source), dx, dy, oldGroups);
                    foreach (var component in components)
                    {
                        string componentLabel = "W" + number + " Sources=" + string.Join("/", component.Select(r => r.LinkedElementId));
                        try
                        {
                            plan = CleanSyncPlanService.Build(doc, foundation.Id.IntegerValue, footprint, review, log, wallId);
                            var componentKeys = new HashSet<string>(component.Select(r => r.SourceKey));
                            var componentRows = rows.Where(r => componentKeys.Contains(r.Source.SourceKey)).ToList();
                            foreach (var row in componentRows)
                            {
                                if (row.Status != "ACTUAL FIT PREVIEW" && row.Source.ExistingOpeningStatus != "MANAGED")
                                    throw new InvalidOperationException("Source " + row.SourceId + ": " + row.Notes);
                                string reason;
                                if (!OpeningFitValidationService.TryValidate(doc, row.Source, out reason))
                                    throw new InvalidOperationException("Source " + row.SourceId + ": " + reason);
                            }
                            var desired = CompoundOpeningService.Combine(component, dx, dy, merge);
                            foreach (var record in desired)
                            {
                                string reason;
                                if (!OpeningFitValidationService.TryValidate(doc, record, out reason))
                                    throw new InvalidOperationException("Combined opening fit: " + reason);
                                log.Info("WALL OPENING PLAN W" + number + " Key=" + record.SourceKey +
                                    " Members=" + string.Join(",", CompoundOpeningService.Members(record)) +
                                    " Size=" + record.CutWidthMm + "x" + record.CutHeightMm);
                            }
                            var replacements = ManagedReplacements(doc, plan, desired, component.Select(r => r.SourceKey), dx, dy, merge);
                            if (missingOnly && (replacements.Count > 0 || desired.Any(r => plan.ManagedOpeningIds.ContainsKey(r.SourceKey))))
                                throw new InvalidOperationException("Missing-only run would replace an existing cut; use Run Openings to update or merge it.");
                            plan.ProposedRows.Clear();
                            plan.ProposedRows.AddRange(desired.Select(r => new UnifiedOpeningReviewRow {
                                Source = r, WallId = wallId, Wall = "W" + number, SourceId = r.LinkedElementId,
                                Status = "ACTUAL FIT PREVIEW", OpeningSize = r.CutWidthMm.ToString("0.#") + " x " + r.CutHeightMm.ToString("0.#")
                            }));
                            using (var group = new TransactionGroup(doc, "HATCO - Open W" + number))
                            {
                                group.Start();
                                try
                                {
                                    using (var tx = new Transaction(doc, "HATCO - Refresh W" + number + " references"))
                                    {
                                        tx.Start(); TransactionFailureHandling.Configure(tx, log);
                                        OpeningDimensionService.RemoveOwned(doc, foundation, number);
                                        foreach (var pair in replacements)
                                        {
                                            var removed = doc.Delete(new ElementId(pair.Value));
                                            if (removed.Any(e => e.IntegerValue == foundation.Id.IntegerValue || footprint.Walls.Any(w => w.Id == e)))
                                                throw new InvalidOperationException("Replacing a tool opening affected the body.");
                                            plan.ManagedOpeningIds.Remove(pair.Key);
                                        }
                                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Opening replacement preparation rejected.");
                                    }
                                    // Do not resolve joins to other walls from a partial-wall transaction.
                                    var applied = CleanSyncAtomicService.Apply(doc, plan, new CleanSyncApplyOptions {
                                        IncludeValidatedEndpoints = true, RequiredLinksVerified = true, ResolveManholeJoinFailures = false
                                    }, log);
                                    if (!applied.Committed) throw new InvalidOperationException(applied.Error);
                                    if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Opening group commit rejected.");
                                    newCuts += applied.NewOpenings; updated += applied.ManagedUpdated; unchanged += applied.ManagedUnchanged;
                                    committedGroups++;
                                    foreach (var shifted in component.Where(r => r.EdgeAligned && Math.Abs(r.EdgeShiftMm) > 0.1))
                                    {
                                        string site = "W" + number + " Source=" + shifted.LinkedElementId + ": OPENING COMMITTED - pipe/duct site adjustment along wall " + shifted.EdgeShiftMm.ToString("0.#") + " mm required.";
                                        problems.Add(site); log.Warn(site);
                                    }
                                    safeWalls.Add(number);
                                    outcomes.Add(componentLabel + ": committed " + desired.Count + " cut(s); merged=" + desired.Count(r => r.MemberSourceKeys != null));
                                }
                                catch
                                {
                                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                                    throw;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            groupFailed = true;
                            problems.Add(componentLabel + ": " + ex.Message);
                            log.Error("OPENING GROUP REVIEW " + componentLabel + "; independent groups retained", ex);
                        }
                    }
                    if (profileGroup != null)
                    {
                        if (groupFailed) throw new InvalidOperationException("Replacement opening failed; profile reset and all cuts on this wall rolled back. " + string.Join("; ", problems.Skip(savedProblems).Where(p => !p.Contains("OPENING COMMITTED"))));
                        if (profileGroup.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Profile/reset wall group commit rejected.");
                        outcomes.Add("W" + number + ": profile reset and replacement openings committed");
                        log.Info("WALL PROFILE RESET COMMITTED Wall=" + wallId + " W" + number);
                    }
                }
                catch (Exception ex)
                {
                    if (profileGroup != null)
                    {
                        if (profileGroup.GetStatus() == TransactionStatus.Started) profileGroup.RollBack();
                        newCuts = savedNew; updated = savedUpdated; unchanged = savedUnchanged;
                        committedGroups = savedGroups; safeWalls.Remove(number);
                        outcomes.RemoveRange(savedOutcomes, outcomes.Count - savedOutcomes);
                        // Remove site-adjustment messages for cuts that were rolled back.
                        problems.RemoveRange(savedProblems, problems.Count - savedProblems);
                        log.Warn("WALL PROFILE RESET ROLLED BACK Wall=" + wallId + "; original profile and pin retained.");
                    }
                    problems.Add("W" + number + " (" + wallId + "): " + ex.Message);
                    log.Error("WALL REVIEW W" + number + " Foundation=" + foundation.Id.IntegerValue + "; other walls retained", ex);
                }
                finally { profileGroup?.Dispose(); }
            }
            if (!missingOnly && candidateCount == 0 && problems.Count == 0) problems.Add("No confirmed crossing or validated end connector within 150 mm; existing openings preserved.");
            bool dimensionsComplete = false, deferred = false;
            string dimensionStatus = "Dimensions unchanged: no opening groups committed.";
            if (deferDimensions) { deferred = true; dimensionStatus = "Dimensions deferred to the dedicated automation phase."; }
            if (committedGroups > 0 && !deferDimensions)
            {
                string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D";
                var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).Select(v => v.Name));
                deferred = !names.Contains(prefix + "_PLAN") || !Enumerable.Range(1, 4).All(n => names.Contains(prefix + "_OUT_W" + n));
                try
                {
                    dimensionStatus = deferred ? "Dimensions deferred: prepare PLAN and W1-W4 when required." :
                        OpeningDimensionService.Generate(doc, foundation, log, ok => dimensionsComplete = ok, safeWalls);
                }
                catch (Exception ex) { dimensionStatus = "Dimension review: " + ex.Message; log.Error(dimensionStatus, ex); }
            }
            string summary = (problems.Count > 0 ? committedGroups > 0 ? "PARTIAL REVIEW" : "REVIEW" : "OPENINGS COMPLETE") +
                " | New=" + newCuts + " Updated=" + updated + " Unchanged=" + unchanged + "\n" + string.Join("\n", outcomes) +
                "\n" + string.Join("\n", problems) + "\n" + dimensionStatus + "\nReview CSV: " + csv;
            if (problems.Count > 0)
                ManholeReviewRegistry.Upsert(doc, foundation, string.Join("; ", problems), footprint.Walls.Select(w => w.Id.IntegerValue),
                    "OPENINGS REVIEW", log, ReviewDomain.Openings,
                    evidence: ReviewEvidence.Capture(doc, foundation, footprint.Walls, clearance, review.Rows));
            // Changed cuts can invalidate earlier dimensions even when the dimension phase is deferred.
            if (committedGroups > 0)
            {
                if (dimensionsComplete) ManholeReviewRegistry.Resolve(doc, foundation, ReviewDomain.Dimensions);
                else ManholeReviewRegistry.Upsert(doc, foundation, dimensionStatus, null, "DIMENSION REVIEW", log, ReviewDomain.Dimensions, replace: true);
            }
            // Sheet-note or registry errors must never undo successfully committed walls.
            try
            {
                var slot = BatchSheetLayoutService.Find(doc, foundation);
                if (slot != null)
                using (var tx = new Transaction(doc, "HATCO - Report Wall Opening Status"))
                {
                    tx.Start(); TransactionFailureHandling.Configure(tx, log);
                    BatchSheetLayoutService.SetStatus(doc, foundation, slot, problems.Count > 0 ?
                        "PARTIAL / REVIEW - " + string.Join("; ", problems) : "OPENINGS COMPLETE - verify dimensions.");
                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Sheet status not updated.");
                }
            }
            catch (Exception ex)
            {
                summary += "\nSheet note review: " + ex.Message; log.Error("Opening sheet note", ex);
                ManholeReviewRegistry.Upsert(doc, foundation, "Sheet note: " + ex.Message, null, "LAYOUT REVIEW", log, ReviewDomain.Layout);
            }
            return new ProductionManholeResult(committedGroups > 0, dimensionsComplete, summary) {
                DimensionsDeferred = deferred, OpeningsNeedReview = problems.Count > 0
            };
        }

        // One native reset per transaction; caller owns this wall's rollback group.
        private static void ResetSingleWallProfile(Document doc, Wall wall, Element foundation,
            int number, DiagnosticLogger log)
        {
            using (var tx = new Transaction(doc, "HATCO - Reset profile W" + number))
            {
                tx.Start(); TransactionFailureHandling.Configure(tx, log);
                bool pinned = wall.Pinned;
                if (pinned) wall.Pinned = false;
                OpeningDimensionService.RemoveOwned(doc, foundation, number);
                wall.RemoveProfileSketch();
                doc.Regenerate();
                if (wall.SketchId != ElementId.InvalidElementId)
                    throw new InvalidOperationException("Wall profile reset did not remove the edited sketch.");
                if (pinned) wall.Pinned = true;
                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Wall profile reset transaction rejected.");
                log.Info("WALL PROFILE RESET STAGED Wall=" + wall.Id.IntegerValue + " W" + number + " Pin=" + pinned);
            }
        }

        private static List<KeyValuePair<string, int>> ManagedReplacements(Document doc, CleanSyncPlan plan,
            List<PenetrationRecord> desired, IEnumerable<string> sourceKeys, double dx, double dy, bool merge)
        {
            var keys = new HashSet<string>(sourceKeys, StringComparer.Ordinal);
            var replacements = new List<KeyValuePair<string, int>>();
            foreach (var pair in plan.ManagedOpeningIds)
            {
                var opening = doc.GetElement(new ElementId(pair.Value)) as Opening;
                ManagedOpeningData data;
                if (opening == null || !OpeningStorageService.TryRead(opening, out data) || data.AdoptedManual)
                    throw new InvalidOperationException("Managed opening identity changed.");
                var members = CompoundOpeningService.ReadMembers(opening, pair.Key);
                bool shared = CompoundOpeningService.AuthorizeReplacement(members, keys, merge);
                if (desired.Any(r => r.SourceKey == pair.Key)) continue;
                if (shared)
                {
                    replacements.Add(pair);
                    continue;
                }
                var old = new PenetrationRecord { HostWallId = data.HostWallId, Xmm = data.Xmm, Ymm = data.Ymm, Zmm = data.Zmm,
                    CutWidthOverrideMm = data.CutWidthMm, CutHeightOverrideMm = data.CutHeightMm };
                if (desired.Any(r => CompoundOpeningService.Overlaps(old, r, dx, dy)))
                    throw new InvalidOperationException("Unmatched existing opening " + pair.Value + " overlaps a proposed cut; preserved for review.");
            }
            return replacements;
        }
    }
}
