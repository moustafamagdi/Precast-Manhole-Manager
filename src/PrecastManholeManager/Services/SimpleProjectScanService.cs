using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class SimpleManholeItem
    {
        public int FoundationId { get; set; }
        public string UniqueId { get; set; }
        public string TypeName { get; set; }
        public string State { get; set; }
        public string Problem { get; set; }
        public string ViewName { get; set; }
    }

    // One-time-project tool: lightweight geometry and existing-cut inventory.
    // No MEP rescans or model edits until the operator explicitly reviews one.
    internal static class SimpleProjectScanService
    {
        private static readonly HashSet<string> BaseTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HTC_ST_PRECAST_FN_200mm_MH",
                "HTC_ST_PRECAST_FN_300mm_MH"
            };

        public static List<SimpleManholeItem> Scan(Document doc,
            DiagnosticLogger log)
        {
            List<ManholeReviewIssue> saved =
                ManholeReviewRegistry.Load(doc);
            var items = new List<SimpleManholeItem>();
            foreach (Element baseElement in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .WhereElementIsNotElementType()
                .ToElements().Where(e =>
                {
                    Element type = doc.GetElement(e.GetTypeId());
                    return type != null && BaseTypes.Contains(type.Name);
                }).OrderBy(e => e.Id.IntegerValue))
            {
                var item = new SimpleManholeItem
                {
                    FoundationId = baseElement.Id.IntegerValue,
                    UniqueId = baseElement.UniqueId,
                    TypeName = doc.GetElement(baseElement.GetTypeId()).Name,
                    State = "READY",
                    Problem = ""
                };

                try
                {
                    VirtualFoundationResult footprint =
                        new VirtualFoundationRecoveryService(doc, log)
                            .Analyze(baseElement);
                    var reasons = new List<string>();
                    var walls = new List<int>();
                    if (!footprint.Accepted)
                        reasons.Add("Wall footprint: " + footprint.Reason);
                    else
                    {
                        walls = footprint.Walls.Select(w => w.Id.IntegerValue)
                            .ToList();
                        OpeningResetAuditResult audit =
                            OpeningResetAuditService.Audit(
                                doc, footprint.Walls, log);
                        if (audit.ProfileEditedWalls > 0)
                            reasons.Add(audit.ProfileEditedWalls +
                                " edited wall profiles");
                        if (audit.ProfileUnknownWalls > 0)
                            reasons.Add("Profile status unknown");
                        if (audit.VoidCutRelations > 0 ||
                            audit.VoidUnknownWalls > 0)
                            reasons.Add("Void cuts need review");
                        if (audit.NativeUnmanaged > 0)
                            reasons.Add(audit.NativeUnmanaged +
                                " non-tool native openings");
                        foreach (Wall wall in footprint.Walls)
                        {
                            var knownVoid = new HashSet<int>(
                                InstanceVoidCutUtils.GetCuttingVoidInstances(
                                    wall).Select(x => x.IntegerValue));
                            foreach (ElementId id in wall.FindInserts(
                                true, true, true, true))
                            {
                                Element e = doc.GetElement(id);
                                if (e is Opening || knownVoid.Contains(
                                    id.IntegerValue)) continue;
                                FamilyInstance fi = e as FamilyInstance;
                                reasons.Add(fi?.Symbol?.Family != null &&
                                    fi.Symbol.Family.IsInPlace
                                    ? "In-place cutter " + id.IntegerValue +
                                      (e.Pinned ? " (pinned)" : "")
                                    : "Unknown wall insert " + id.IntegerValue);
                            }
                        }
                    }

                    ManholeReviewIssue existing = saved.FirstOrDefault(x =>
                        x.FoundationUniqueId == item.UniqueId);
                    if (reasons.Count > 0)
                    {
                        item.State = "REVIEW";
                        item.Problem = string.Join("; ", reasons.Distinct());
                        ManholeReviewRegistry.Upsert(doc, baseElement,
                            item.Problem, walls, "REQUIRES REVIEW", log);
                    }
                    else if (existing != null && existing.Status == "OPEN")
                    {
                        // The lightweight scan may miss previously reported
                        // destructive cascade failures: do not clear them.
                        item.State = "REVIEW";
                        item.Problem = existing.Reason;
                    }
                    item.ViewName = existing?.ViewName ?? "";
                }
                catch (Exception ex)
                {
                    item.State = "REVIEW";
                    item.Problem = "Scan error: " + ex.Message;
                    ManholeReviewRegistry.Upsert(doc, baseElement,
                        item.Problem, null, "ERROR", log);
                }
                items.Add(item);
            }
            return items;
        }

        public static List<SimpleManholeItem> LoadFast(Document doc)
        {
            var issues = ManholeReviewRegistry.Load(doc)
                .GroupBy(x => x.FoundationUniqueId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x =>
                    x.UpdatedUtc).First(), StringComparer.Ordinal);
            var items = new List<SimpleManholeItem>();
            foreach (Element el in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .WhereElementIsNotElementType().ToElements()
                .Where(e =>
                {
                    Element type = doc.GetElement(e.GetTypeId());
                    return type != null && BaseTypes.Contains(type.Name);
                }).OrderBy(e => e.Id.IntegerValue))
            {
                ManholeReviewIssue issue;
                bool found = issues.TryGetValue(el.UniqueId, out issue);
                bool flagged = found && issue.Status == "OPEN";
                items.Add(new SimpleManholeItem
                {
                    FoundationId = el.Id.IntegerValue,
                    UniqueId = el.UniqueId,
                    TypeName = doc.GetElement(el.GetTypeId()).Name,
                    State = flagged ? "REVIEW" : "NO ISSUE RECORDED",
                    Problem = flagged ? issue.Reason : "",
                    ViewName = found ? issue.ViewName : ""
                });
            }
            return items;
        }
    }
}
