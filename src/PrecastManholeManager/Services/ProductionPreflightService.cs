using System.Collections.Generic;
using System.Linq;

namespace Hatco.PrecastManholeManager.Services
{
    // Shared read-only blockers for Clean Scan and production.
    internal static class ProductionPreflightService
    {
        internal static List<string> PhysicalBlockers(CleanSyncPlan plan)
        {
            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(plan.BlockReason)) reasons.Add(plan.BlockReason);
            if (plan.ProfileResetCount > 0)
                reasons.Add("Edited wall profiles: " + string.Join(",", plan.Profiles
                    .Where(x => x.Value == "EDITED PROFILE").Select(x => x.Key)));
            if (plan.VoidCutCount > 0)
                reasons.Add("Void cuts: " + string.Join(",", plan.VoidCutIds
                    .Where(x => x.Value.Count > 0)
                    .Select(x => x.Key + " => " + string.Join("/", x.Value))));
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
