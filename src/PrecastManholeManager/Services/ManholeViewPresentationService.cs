using System;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ManholeViewPresentationService
    {
        private static readonly Regex PlanName = new Regex(@"^MH_\d+_(?:PROD|DRAFT)_2D_PLAN$");
        private static readonly Regex ViewName = new Regex(@"^MH_\d+_(?:PROD|DRAFT)_2D_(?:PLAN|OUT_W[1-4])$");
        internal static bool InScope(string name, System.Collections.Generic.ISet<int> scope)
        {
            if (scope == null) return true;
            int id;
            var parts = (name ?? "").Split('_');
            return parts.Length > 1 && parts[0] == "MH" && int.TryParse(parts[1], out id) && scope.Contains(id);
        }
        public static bool IsManagedPlan(string name) => PlanName.IsMatch(name ?? "");
        public static bool IsOwnSection(string planName, string markerName) => IsManagedPlan(planName) &&
            Enumerable.Range(1, 4).Any(n => markerName == planName.Substring(0, planName.Length - 5) + "_OUT_W" + n);

        public static ElementType RequiredViewportType(Document doc, Viewport port)
        {
            // Viewport system types have no Category in Revit 2024.
            return port.GetValidTypes().Select(id => doc.GetElement(id)).OfType<ElementType>()
                .FirstOrDefault(t => t.Name == "NO BUBBLE NTS")
                ?? throw new InvalidOperationException("Load viewport type NO BUBBLE NTS and run again.");
        }

        // Caller owns the transaction. Markers are OST_Viewers elements, not ViewSection IDs.
        public static void CleanPlan(Document doc, ViewPlan plan, DiagnosticLogger log, System.Collections.Generic.IList<Element> markers = null)
        {
            if (!IsManagedPlan(plan.Name)) return;
            var own = (markers ?? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Viewers)
                .WhereElementIsNotElementType().ToElements())
                .Where(e => IsOwnSection(plan.Name, e.Name) && e.IsHidden(plan)).Select(e => e.Id).ToList();
            if (own.Count > 0) plan.UnhideElements(own);
            var foreign = new FilteredElementCollector(doc, plan.Id).OfCategory(BuiltInCategory.OST_Viewers)
                .WhereElementIsNotElementType().ToElements()
                .Where(e => !IsOwnSection(plan.Name, e.Name) && !e.IsHidden(plan) && e.CanBeHidden(plan))
                .Select(e => e.Id).ToList();
            if (foreign.Count > 0) plan.HideElements(foreign);
            log.Info("PLAN SECTION MARKS " + plan.Name + " Hidden=" + foreign.Count + " Restored=" + own.Count);
        }

        // Run after all sections exist: later-created sections can appear in earlier plans.
        // Each plan commits independently so one uneditable view cannot stop the others.
        public static string ApplyAll(Document doc, DiagnosticLogger log, Func<int, int, bool> proceed = null, System.Collections.Generic.ISet<int> scope = null)
        {
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && IsManagedPlan(v.Name) && InScope(v.Name, scope)).ToList();
            var markers = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Viewers)
                .WhereElementIsNotElementType().ToElements();
            int completed = 0, failed = 0, changed = 0;
            foreach (var plan in plans)
            {
                if (proceed != null && !proceed(completed + failed, plans.Count)) return "Presentation cleanup stopped; committed changes retained.";
                try
                {
                    using (var tx = new Transaction(doc, "HATCO - Manhole Plan Section Marks"))
                    {
                        tx.Start(); TransactionFailureHandling.Configure(tx, log);
                        CleanPlan(doc, plan, log, markers);
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Plan transaction rejected.");
                    }
                    completed++;
                }
                catch (Exception ex)
                {
                    failed++; log.Error("PLAN MARKS REVIEW " + plan.Name, ex);
                    RegisterPresentationIssue(doc, plan.Name, "Plan marks: " + ex.Message, log);
                }
            }
            var allPorts = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .Where(p => ViewName.IsMatch(doc.GetElement(p.ViewId)?.Name ?? "") && InScope(doc.GetElement(p.ViewId)?.Name, scope)).ToList();
            ElementType type;
            try { type = allPorts.Count == 0 ? null : RequiredViewportType(doc, allPorts[0]); }
            catch (InvalidOperationException)
            {
                log.Warn("VIEWPORT TYPE REVIEW: Missing NO BUBBLE NTS.");
                foreach (var port in allPorts) RegisterPresentationIssue(doc, doc.GetElement(port.ViewId)?.Name,
                    "Missing viewport type NO BUBBLE NTS.", log);
                return "Plans processed: " + completed + "; failed: " + failed + ". Load viewport type NO BUBBLE NTS and run again.";
            }
            var ports = allPorts.Where(p => p.GetTypeId() != type.Id).ToList();
            foreach (var group in ports.GroupBy(p => p.SheetId.IntegerValue))
            {
                if (proceed != null && !proceed(changed, ports.Count)) return "Presentation cleanup stopped; committed changes retained.";
                try
                {
                    using (var tx = new Transaction(doc, "HATCO - Manhole Viewport Type"))
                    {
                        tx.Start(); TransactionFailureHandling.Configure(tx, log);
                        foreach (var port in group) port.ChangeTypeId(type.Id);
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Viewport transaction rejected.");
                    }
                    changed += group.Count();
                }
                catch (Exception ex)
                {
                    failed++; log.Error("VIEWPORT TYPE REVIEW Sheet=" + group.Key, ex);
                    foreach (var port in group) RegisterPresentationIssue(doc, doc.GetElement(port.ViewId)?.Name,
                        "Viewport type: " + ex.Message, log);
                }
            }
            string result = "Plans processed: " + completed + "; viewports changed to NO BUBBLE NTS: " + changed + "; failures: " + failed + ".";
            log.Info(result);
            return result;
        }

        private static void RegisterPresentationIssue(Document doc, string viewName, string reason, DiagnosticLogger log)
        {
            try
            {
                int id;
                var parts = (viewName ?? "").Split('_');
                if (parts.Length < 2 || !int.TryParse(parts[1], out id)) return;
                var foundation = doc.GetElement(new ElementId(id));
                if (foundation != null) ManholeReviewRegistry.Upsert(doc, foundation, reason, null,
                    "PRESENTATION REVIEW", log, ReviewDomain.Presentation);
            }
            catch (Exception ex) { log.Error("Presentation issue retained in log; register unavailable", ex); }
        }
    }
}
