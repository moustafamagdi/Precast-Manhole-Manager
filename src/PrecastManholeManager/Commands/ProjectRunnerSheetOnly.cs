using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        private static void PrepareSelectedSheet(UIDocument uidoc, SimpleManholeItem item, DiagnosticLogger log)
        {
            var doc = uidoc.Document;
            WorkflowPreflightService.Require(doc, log, true, false, false, 0);
            var foundation = Resolve(doc, item);
            if (string.IsNullOrWhiteSpace(ManholeIdentityStore.Read(foundation)))
                throw new InvalidOperationException("Assign Internal MH IDs in Setup & Advanced first, then prepare this sheet.");
            var footprint = new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
            if (!footprint.Accepted) throw new InvalidOperationException("Cannot identify the four walls: " + footprint.Reason);
            var slot = BatchSheetLayoutService.Find(doc, foundation);
            bool ready = slot != null && BatchSheetLayoutService.HasPreparedViews(doc, foundation, slot);
            if (!ready)
            {
                var layoutWarnings = new List<string>();
                var titleblock = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .OrderByDescending(t => t.Name.IndexOf("A0", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 :
                        t.Name.IndexOf("A1", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0).FirstOrDefault();
                if (titleblock == null) throw new InvalidOperationException("Load an A0/A1 titleblock first.");
                var foundations = SimpleProjectScanService.LoadFast(doc).Select(x => Resolve(doc, x)).ToList();
                using (var tx = new Transaction(doc, "HATCO - Selected Manhole Sheet Only"))
                {
                    tx.Start(); TransactionFailureHandling.Configure(tx, log);
                    BatchSheetLayoutService.Reserve(doc, foundations, titleblock, log,
                        new HashSet<string> { foundation.UniqueId });
                    slot = BatchSheetLayoutService.Find(doc, foundation);
                    var views = DraftManholeSheetService.Generate(doc, foundation, footprint, log, forProduction: true);
                    layoutWarnings = BatchSheetLayoutService.Place(doc, foundation, slot, views.Views, new List<UnifiedOpeningReviewRow>(), log);
                    BatchSheetLayoutService.SetStatus(doc, foundation, slot, "DOCUMENTATION ONLY - opening status not checked.");
                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Could not commit the selected sheet.");
                }
                if (layoutWarnings.Count > 0) ManholeReviewRegistry.Upsert(doc, foundation,
                    string.Join("; ", layoutWarnings), null, "LAYOUT REVIEW", log, ReviewDomain.Layout);
                else ManholeReviewRegistry.Resolve(doc, foundation, ReviewDomain.Layout);
            }
            ManholeReviewRegistry.Resolve(doc, foundation, ReviewDomain.Views);
            uidoc.RequestViewChange(slot.Sheet);
            TaskDialog.Show("Prepare Sheet", (ready ? "Existing views and row reused." : "PLAN and W1-W4 prepared in the reserved six-row sheet.") +
                "\nNo opening detection or cutting was required.\nSheet: " + slot.Sheet.SheetNumber +
                "\nUse Dimensions when ready to dimension the body/base and existing tool openings.\nSave the RVT to retain the output.");
        }
    }
}
