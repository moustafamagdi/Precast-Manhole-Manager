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
        public List<int> WallIds { get; } = new List<int>();
        public Dictionary<int, string> Profiles { get; } = new Dictionary<int, string>();
        public Dictionary<int, List<int>> VoidCutIds { get; } =
            new Dictionary<int, List<int>>();
        public List<int> ManualOpeningIds { get; } = new List<int>();
        public List<int> AdoptedManualOpeningIds { get; } = new List<int>();
        public Dictionary<string, int> ManagedOpeningIds { get; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        public List<int> UnsupportedSolidCutWallIds { get; } = new List<int>();
        public List<UnifiedOpeningReviewRow> ProposedRows { get; } =
            new List<UnifiedOpeningReviewRow>();
        public string BlockReason { get; set; }
        public int ProfileResetCount =>
            Profiles.Count(x => x.Value == "EDITED PROFILE");
        public int VoidCutCount => VoidCutIds.Sum(x => x.Value.Count);
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
            DiagnosticLogger log)
        {
            if (doc == null || footprint == null || !footprint.Accepted ||
                review == null)
                throw new InvalidOperationException("Valid footprint and review required.");

            var plan = new CleanSyncPlan { FoundationId = foundationId };
            plan.WallIds.AddRange(footprint.Walls.Select(x => x.Id.IntegerValue));
            plan.ProposedRows.AddRange(review.Rows);

            foreach (Wall wall in footprint.Walls)
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

                // In-place/other solid-solid cutting arrangements are not
                // equivalent to unattached void relations. Never silently reset.
                try
                {
                    ICollection<ElementId> otherCuts =
                        SolidSolidCutUtils.GetCuttingSolids(wall);
                    if (otherCuts != null && otherCuts.Count > 0)
                    {
                        plan.UnsupportedSolidCutWallIds.Add(id);
                        plan.BlockReason =
                            "Unclassified solid-solid cuts exist on wall " + id;
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
                        plan.BlockReason =
                            "Unclassified hosted wall insert/cut " +
                            id.IntegerValue + " on wall " + wallId;
                        log.Warn("CLEAN UNSUPPORTED HOST INSERT Wall=" + wallId +
                            " Insert=" + id.IntegerValue);
                    }
                }
                catch (Exception ex)
                {
                    plan.BlockReason = "Unable to inventory wall inserts on " +
                        wallId + ": " + ex.Message;
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
                " ProposedActual=" + review.Rows.Count(x => !x.IsVirtual) +
                " ProposedVirtual=" + plan.VirtualCount +
                " SlopedOrSkewedVirtual=" + plan.SlopedVirtualCount);
            foreach (var wall in plan.Profiles)
                log.Info("CLEAN WALL " + wall.Key + " Profile=" + wall.Value +
                    " VoidCutters=" + string.Join(",",
                        plan.VoidCutIds.ContainsKey(wall.Key)
                            ? plan.VoidCutIds[wall.Key] : new List<int>()));
            if (!string.IsNullOrWhiteSpace(plan.BlockReason))
                log.Warn("PLAN BLOCKED: " + plan.BlockReason);
            return plan;
        }
    }
}
