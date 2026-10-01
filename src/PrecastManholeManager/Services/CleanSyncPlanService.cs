using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class CleanSyncPlan
    {
        public int FoundationId { get; set; }
        public int UnavailableLinks { get; set; }
        public List<int> WallIds { get; } = new List<int>();
        public Dictionary<int, string> Profiles { get; } = new Dictionary<int, string>();
        public Dictionary<int, List<int>> VoidCutIds { get; } =
            new Dictionary<int, List<int>>();
        public List<int> ManualOpeningIds { get; } = new List<int>();
        public List<int> AdoptedManualOpeningIds { get; } = new List<int>();
        // In-place cutters returned as wall inserts, distinct from API
        // unattached void cut relationships.
        public Dictionary<int, List<int>> InPlaceCutterWallIds { get; } =
            new Dictionary<int, List<int>>();
        public Dictionary<string, int> ManagedOpeningIds { get; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public List<int> UnsupportedSolidCutWallIds { get; } = new List<int>();
        public List<string> SolidCutReviewReasons { get; } = new List<string>();
        public List<UnifiedOpeningReviewRow> ProposedRows { get; } =
            new List<UnifiedOpeningReviewRow>();
        public string BlockReason { get; set; }
        public int ProfileResetCount =>
            Profiles.Count(x => x.Value == "EDITED PROFILE");
        public int VoidCutCount => VoidCutIds.Sum(x => x.Value.Count);
        public int InPlaceCutterCount => InPlaceCutterWallIds.Count;
        public int VirtualCount => ProposedRows.Count(x => x.IsVirtual);
        public int SlopedVirtualCount => ProposedRows.Count(x =>
            x.IsVirtual && (Math.Abs(x.SlopePercent) > 0.1 ||
                            x.ApproachAngleDeg > 0.5));
        public int UnknownSizeCount => ProposedRows.Count(x =>
            x.Source.CutWidthMm <= 0 || x.Source.CutHeightMm <= 0);
    }

    // Purely reads the current model. A plan is rebuilt immediately before
    // starting a write transaction so stale wall/opening identity is rejected.
    internal static class CleanSyncPlanService
    {
        public static CleanSyncPlan Build(Document doc, int foundationId,
            VirtualFoundationResult footprint, UnifiedOpeningReviewResult review,
            DiagnosticLogger log, int? onlyWallId = null)
        {
            if (doc == null || footprint == null || !footprint.Accepted ||
                review == null)
                throw new InvalidOperationException("Valid footprint and review required.");

            var plan = new CleanSyncPlan
            {
                FoundationId = foundationId,
                UnavailableLinks = review.VirtualScan.UnavailableLinks
            };
            var bodyWallIds = footprint.Walls.Select(x => x.Id.IntegerValue).ToList();
            plan.WallIds.AddRange(bodyWallIds.Where(x => !onlyWallId.HasValue || x == onlyWallId.Value));
            plan.ProposedRows.AddRange(review.Rows.Where(x => plan.WallIds.Contains(x.Source.HostWallId)));

            foreach (Wall wall in footprint.Walls.Where(x => plan.WallIds.Contains(x.Id.IntegerValue)))
            {
                int id = wall.Id.IntegerValue;
                WallOpeningAuditInfo audit;
                if (!review.Audit.WallDetails.TryGetValue(id, out audit))
                {
                    plan.BlockReason = "Missing audit for wall " + id;
                    continue;
                }
                plan.Profiles[id] = audit.ProfileStatus;
                if (audit.ProfileStatus == "UNKNOWN")
                    plan.BlockReason = "Cannot classify profile on wall " + id;

                ICollection<ElementId> cutting;
                try { cutting = InstanceVoidCutUtils.GetCuttingVoidInstances(wall); }
                catch (Exception ex)
                {
                    plan.BlockReason = "Cannot inspect void cuts on wall " + id +
                        ": " + ex.Message;
                    continue;
                }
                plan.VoidCutIds[id] = cutting.Select(x => x.IntegerValue)
                    .Distinct().ToList();

                // Preserve verified local joins; unexplained or external cutters
                // remain blockers. Neither kind is removed by this read-only scan.
                try
                {
                    ICollection<ElementId> otherCuts =
                        SolidSolidCutUtils.GetCuttingSolids(wall);
                    foreach (ElementId cutterId in otherCuts ?? new List<ElementId>())
                    {
                        Element cutter = doc.GetElement(cutterId);
                        bool joined = cutter != null && JoinGeometryUtils.AreElementsJoined(doc, wall, cutter);
                        bool cutsWall = joined && JoinGeometryUtils.IsCuttingElementInJoin(doc, cutter, wall);
                        bool localJoin = IsLocalBodyJoin(id, cutterId.IntegerValue,
                            bodyWallIds, foundationId, joined, cutsWall);
                        string identity = "Wall=" + id + " Cutter=" + cutterId.IntegerValue +
                            " Class=" + (cutter?.GetType().Name ?? "MISSING") +
                            " Category=" + (cutter?.Category?.Name ?? "UNKNOWN") +
                            " Name=" + (cutter?.Name ?? "UNKNOWN");
                        log.Info("SOLID CUT CLASSIFICATION " + identity +
                            " Joined=" + joined + " CutterCutsWall=" + cutsWall +
                            " Result=" + (localJoin ? "LOCAL BODY JOIN - PRESERVED" : "REVIEW"));
                        if (localJoin) continue;
                        if (!plan.UnsupportedSolidCutWallIds.Contains(id))
                            plan.UnsupportedSolidCutWallIds.Add(id);
                        string reason = "Solid cut requires review: " + identity +
                            (joined ? " (join outside verified local body scope or inconsistent cut direction)"
                                    : " (not a verified geometry join)");
                        plan.SolidCutReviewReasons.Add(reason);
                        plan.BlockReason = reason;
                    }
                }
                catch (Exception ex)
                {
                    plan.BlockReason = "Cannot verify solid cuts on wall " + id +
                        ": " + ex.Message;
                }
            }

            HashSet<int> walls = new HashSet<int>(plan.WallIds);
            foreach (Opening opening in new FilteredElementCollector(doc)
                .OfClass(typeof(Opening)).Cast<Opening>())
            {
                int wallId = opening.Host?.Id.IntegerValue ?? -1;
                if (!walls.Contains(wallId)) continue;
                ManagedOpeningData managed;
                if (OpeningStorageService.TryRead(opening, out managed))
                {
                    if (managed.HostWallId != wallId)
                        plan.BlockReason = "Copied opening ownership does not match host wall " + wallId + "; review before updating.";
                    if (managed.AdoptedManual)
                    {
                        // A previously ADOPTED hand-made opening is still a
                        // non-tool physical cut and must be re-created under
                        // Clean & Sync, not silently preserved as tool-origin.
                        plan.ManualOpeningIds.Add(opening.Id.IntegerValue);
                        plan.AdoptedManualOpeningIds.Add(opening.Id.IntegerValue);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(managed.SourceKey) ||
                        plan.ManagedOpeningIds.ContainsKey(managed.SourceKey))
                        plan.BlockReason = "Duplicate/missing managed opening SourceKey on selected walls.";
                    else
                        plan.ManagedOpeningIds.Add(managed.SourceKey, opening.Id.IntegerValue);
                }
                else
                    plan.ManualOpeningIds.Add(opening.Id.IntegerValue);
            }

            // Do not delete unexplained hosted inserts (families/windows/
            // doors or other non-native wall cuts). They require review.
            foreach (int wallId in plan.WallIds)
            {
                Wall wall = doc.GetElement(new ElementId(wallId)) as Wall;
                try
                {
                    HashSet<int> known = new HashSet<int>(
                        plan.ManualOpeningIds
                            .Concat(plan.ManagedOpeningIds.Values)
                            .Concat(plan.VoidCutIds.ContainsKey(wallId)
                                ? plan.VoidCutIds[wallId]
                                : new List<int>()));
                    foreach (ElementId id in wall.FindInserts(true, true, true, true))
                    {
                        if (known.Contains(id.IntegerValue)) continue;
                        Element inserted = doc.GetElement(id);
                        FamilyInstance familyInstance = inserted as FamilyInstance;
                        if (familyInstance?.Symbol?.Family != null &&
                            familyInstance.Symbol.Family.IsInPlace)
                        {
                            List<int> hosts;
                            if (!plan.InPlaceCutterWallIds.TryGetValue(
                                id.IntegerValue, out hosts))
                            {
                                hosts = new List<int>();
                                plan.InPlaceCutterWallIds[id.IntegerValue] = hosts;
                            }
                            if (!hosts.Contains(wallId)) hosts.Add(wallId);
                            log.Warn("CLEAN INPLACE CUTTER CANDIDATE Wall=" +
                                wallId + " Instance=" + id.IntegerValue +
                                " Pinned=" + inserted.Pinned +
                                " Family=" + familyInstance.Symbol.Family.Name);
                            continue;
                        }
                        plan.BlockReason =
                            "Unclassified hosted wall insert/cut " +
                            id.IntegerValue + " on wall " + wallId;
                        log.Warn("CLEAN UNSUPPORTED HOST INSERT Wall=" + wallId +
                            " Insert=" + id.IntegerValue +
                            " Class=" + (inserted?.GetType().Name ?? "NULL"));
                    }
                }
                catch (Exception ex)
                {
                    plan.BlockReason = "Unable to inventory wall inserts on " +
                        wallId + ": " + ex.Message;
                }
            }

            // FindInserts can expose in-place void-cutting family instances
            // that do not appear in GetCuttingVoidInstances. Reject instances
            // cutting walls outside this manhole. Do not confuse detection of
            // an insert with proof that deleting its family is universally safe.
            if (plan.InPlaceCutterWallIds.Count > 0)
            {
                var targetWalls = new HashSet<int>(plan.WallIds);
                var elsewhere = new Dictionary<int, List<int>>();
                foreach (Wall other in new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall)).Cast<Wall>())
                {
                    if (targetWalls.Contains(other.Id.IntegerValue)) continue;
                    foreach (ElementId insert in other.FindInserts(
                        true, true, true, true))
                    {
                        if (!plan.InPlaceCutterWallIds.ContainsKey(
                            insert.IntegerValue)) continue;
                        List<int> otherHosts;
                        if (!elsewhere.TryGetValue(insert.IntegerValue,
                            out otherHosts))
                        {
                            otherHosts = new List<int>();
                            elsewhere[insert.IntegerValue] = otherHosts;
                        }
                        otherHosts.Add(other.Id.IntegerValue);
                    }
                }
                foreach (var pair in elsewhere)
                {
                    plan.BlockReason = "In-place cutter " + pair.Key +
                        " also affects external wall(s): " +
                        string.Join(",", pair.Value);
                    log.Warn("CLEAN INPLACE EXTERNAL WALL CUTTER=" + pair.Key +
                        " OtherWalls=" + string.Join(",", pair.Value));
                }
            }

            if (review.VirtualScan.UnavailableLinks > 0)
                log.Warn("Some Revit links unavailable (" +
                    review.VirtualScan.UnavailableLinks +
                    "); never delete unmatched managed openings.");

            log.WriteHeader("CLEAN AND SYNC - READ ONLY PLAN");
            log.Info("Foundation=" + foundationId +
                " Walls=" + string.Join(",", plan.WallIds) +
                " EditedProfiles=" + plan.ProfileResetCount +
                " ManualNativeDeleteCandidates=" + plan.ManualOpeningIds.Count +
                " AdoptedManualAmongThem=" + plan.AdoptedManualOpeningIds.Count +
                " ExistingManaged=" + plan.ManagedOpeningIds.Count +
                " VoidRelations=" + plan.VoidCutCount +
                " InPlaceCandidates=" + plan.InPlaceCutterCount +
                " ProposedActual=" + review.Rows.Count(x => !x.IsVirtual) +
                " ProposedVirtual=" + plan.VirtualCount +
                " UnavailableLinks=" + plan.UnavailableLinks +
                " SlopedOrSkewedVirtual=" + plan.SlopedVirtualCount);
            foreach (var wall in plan.Profiles)
                log.Info("CLEAN WALL " + wall.Key + " Profile=" + wall.Value +
                    " VoidCutters=" + string.Join(",",
                        plan.VoidCutIds.ContainsKey(wall.Key)
                            ? plan.VoidCutIds[wall.Key] : new List<int>()));
            foreach (var cutter in plan.InPlaceCutterWallIds)
                log.Warn("CLEAN INPLACE REVIEW Instance=" + cutter.Key +
                    " SelectedWalls=" + string.Join(",", cutter.Value) +
                    " (verify no additional non-wall cuts before optional test deletion)");
            if (!string.IsNullOrWhiteSpace(plan.BlockReason))
                log.Warn("PLAN BLOCKED: " + plan.BlockReason);
            return plan;
        }

        internal static bool IsLocalBodyJoin(int wallId, int cutterId,
            ICollection<int> wallIds, int foundationId, bool joined, bool cutterCutsWall)
        {
            return wallIds != null && wallIds.Count == 4 && wallIds.Distinct().Count() == 4 &&
                wallIds.Contains(wallId) && cutterId != wallId &&
                (wallIds.Contains(cutterId) || cutterId == foundationId) && joined && cutterCutsWall;
        }
    }
}
