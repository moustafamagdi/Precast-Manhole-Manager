using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class SixRowSheetResult
    {
        public ViewSheet Sheet { get; set; }
        public int PlacedManholes { get; set; }
        public int SkippedManholes { get; set; }
        public List<int> PlacedIds { get; } = new List<int>();
        public List<string> Problems { get; } = new List<string>();
    }

    // First controlled batch: at most SIX explicitly chosen, clean manholes.
    // Existing user-laid-out sheets are NEVER repositioned or modified.
    internal static class SixRowManholeSheetService
    {
        private const string SheetName = "HATCO_PRECAST_6MH_TEST_01";
        private const double PaperLeftMm = 22;
        private const double PaperTopMm = 18;
        private const double PaperBottomMm = 18;
        // The right titleblock/key-plan strip seen in the supplied sample.
        // Kept conservative until the user validates the first batch sheet.
        private const double ReserveRightMm = 165;
        private const double GapMm = 5;
        private static readonly double[] ColumnCenterFraction =
            { 0.105, 0.305, 0.505, 0.705, 0.905 };

        public static SixRowSheetResult Generate(Document doc,
            IList<Element> foundations, ViewSheet sampleSheet,
            DiagnosticLogger log)
        {
            if (foundations == null || foundations.Count == 0 ||
                foundations.Count > 6)
                throw new InvalidOperationException(
                    "Select between 1 and 6 manholes for the first trial.");
            if (foundations.GroupBy(x => x.Id.IntegerValue).Any(g =>
                g.Count() > 1))
                throw new InvalidOperationException("Repeated foundation selection.");

            // Do not clone, change or reuse the user's original manually
            // arranged sheet. Reuse only its TITLEBLOCK FAMILY TYPE.
            FamilySymbol titleType = null;
            if (sampleSheet != null)
            {
                FamilyInstance instance = new FilteredElementCollector(doc,
                    sampleSheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .FirstOrDefault();
                titleType = instance?.Symbol;
            }
            if (titleType == null)
            {
                titleType = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .OrderByDescending(x =>
                        (x.Name ?? "").IndexOf("A0",
                            StringComparison.OrdinalIgnoreCase) >= 0 ? 2 :
                        (x.Name ?? "").IndexOf("A1",
                            StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                    .FirstOrDefault();
            }
            if (titleType == null)
                throw new InvalidOperationException(
                    "No titleblock is loaded in the host RVT.");

            ViewSheet existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(x => x.Name == SheetName);
            if (existing != null)
                throw new InvalidOperationException(
                    "The test batch sheet already exists: " +
                    existing.SheetNumber + ". It is protected from being overwritten.");

            var output = new SixRowSheetResult();
            // Preflight is read only: never classify a registered OPEN
            // issue or a wall with legacy cuts as production-ready.
            var known = ManholeReviewRegistry.Load(doc)
                .Where(x => x.Status == "OPEN")
                .Select(x => x.FoundationUniqueId)
                .ToHashSetSafe();
            var ready = new List<Tuple<Element, VirtualFoundationResult>>();
            foreach (Element foundation in foundations)
            {
                if (known.Contains(foundation.UniqueId))
                {
                    output.SkippedManholes++;
                    output.Problems.Add(foundation.Id.IntegerValue +
                        ": previously isolated for review");
                    continue;
                }
                try
                {
                    VirtualFoundationResult footprint =
                        new VirtualFoundationRecoveryService(doc, log)
                            .Analyze(foundation);
                    if (!footprint.Accepted)
                        throw new InvalidOperationException(footprint.Reason);
                    OpeningResetAuditResult audit =
                        OpeningResetAuditService.Audit(doc,
                            footprint.Walls, log);
                    if (audit.RequiresManualReview)
                        throw new InvalidOperationException(
                            "Existing edited wall profiles, manual openings " +
                            "or void cuts require review.");
                    // Inspect hosted in-place inserts missed by the
                    // unattached void-cut API.
                    foreach (Wall wall in footprint.Walls)
                        foreach (ElementId insert in wall.FindInserts(
                            true, true, true, true))
                        {
                            Element e = doc.GetElement(insert);
                            if (!(e is Opening))
                                throw new InvalidOperationException(
                                    "Unclassified hosted cut " +
                                    insert.IntegerValue + " on wall " +
                                    wall.Id.IntegerValue);
                        }
                    ready.Add(Tuple.Create(foundation, footprint));
                }
                catch (Exception ex)
                {
                    output.SkippedManholes++;
                    output.Problems.Add(foundation.Id.IntegerValue +
                        ": " + ex.Message);
                    log.Warn("6MH PREFLIGHT SKIP Foundation=" +
                        foundation.Id.IntegerValue + " " + ex.Message);
                }
            }
            if (ready.Count == 0)
                return output;

            // One outer transaction creates the sheet. Each manhole
            // uses a subtransaction; an invalid individual row rolls back
            // without discarding completed clean rows.
            using (var tx = new Transaction(doc,
                "HATCO - First six-row precast sheet"))
            {
                tx.Start();
                try
                {
                    ViewSheet sheet = ViewSheet.Create(doc,
                        titleType.Id);
                    sheet.Name = SheetName;
                    doc.Regenerate();
                    output.Sheet = sheet;

                    double left = sheet.Outline.Min.U;
                    double bottom = sheet.Outline.Min.V;
                    double sheetW = sheet.Outline.Max.U - left;
                    double sheetH = sheet.Outline.Max.V - bottom;
                    double paperLeft = UnitUtil.MmToFt(PaperLeftMm);
                    double paperRight = UnitUtil.MmToFt(ReserveRightMm);
                    double paperTop = UnitUtil.MmToFt(PaperTopMm);
                    double paperBottom = UnitUtil.MmToFt(PaperBottomMm);
                    double usableW = sheetW - paperLeft - paperRight;
                    double usableH = sheetH - paperTop - paperBottom;
                    if (usableW < UnitUtil.MmToFt(550) ||
                        usableH < UnitUtil.MmToFt(450))
                        throw new InvalidOperationException(
                            "Selected titleblock is too small for six rows " +
                            "with a reserved right-hand titleblock strip.");

                    double rowH = usableH / 6.0;
                    double cellW = usableW / 5.0;
                    double allowableW = cellW - UnitUtil.MmToFt(GapMm);
                    double allowableH = rowH - UnitUtil.MmToFt(10);
                    log.Info("6MH SHEET SheetSizeMm=" +
                        UnitUtil.FtToMm(sheetW).ToString("0.#") + "x" +
                        UnitUtil.FtToMm(sheetH).ToString("0.#") +
                        " ContentWmm=" + UnitUtil.FtToMm(usableW).ToString("0.#") +
                        " RowHmm=" + UnitUtil.FtToMm(rowH).ToString("0.#") +
                        " CellWmm=" + UnitUtil.FtToMm(cellW).ToString("0.#"));

                    // Anchor each accepted manhole in its own row,
                    // preserving the user-provided left-to-right order:
                    // PLAN, W1, W2, W3, W4 at 1:25.
                    for (int slot = 0; slot < ready.Count; slot++)
                    {
                        Element foundation = ready[slot].Item1;
                        VirtualFoundationResult footprint = ready[slot].Item2;
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            try
                            {
                                DraftSheetResult views =
                                    DraftManholeSheetService.Generate(
                                        doc, foundation, footprint, log);
                                if (views.Views.Count != 5)
                                    throw new InvalidOperationException(
                                        "Expected exactly five 2D views.");
                                // Respect the user's previously placed
                                // views and scales. Never steal views
                                // from another sheet.
                                foreach (View v in views.Views)
                                {
                                    if (v.Scale != 25)
                                        throw new InvalidOperationException(
                                            v.Name +
                                            " is not 1:25. Existing manual views " +
                                            "will not be changed.");
                                    if (!Viewport.CanAddViewToSheet(
                                        doc, sheet.Id, v.Id))
                                        throw new InvalidOperationException(
                                            v.Name +
                                            " is already on another sheet.");
                                }

                                double cy = bottom + sheetH -
                                    paperTop - rowH * (slot + 0.5);
                                var ports = new List<Viewport>();
                                for (int col = 0; col < 5; col++)
                                {
                                    View v = views.Views[col];
                                    double cx = left + paperLeft +
                                        usableW * ColumnCenterFraction[col];
                                    ports.Add(Viewport.Create(doc,
                                        sheet.Id, v.Id,
                                        new XYZ(cx, cy, 0)));
                                }
                                doc.Regenerate();

                                var boxes = ports.Select(p =>
                                    p.GetBoxOutline()).ToList();
                                for (int col = 0; col < 5; col++)
                                {
                                    double cellLeft = left + paperLeft +
                                        usableW * (col / 5.0);
                                    double cellRight = cellLeft + cellW;
                                    Outline outline = boxes[col];
                                    double ww = outline.MaximumPoint.X -
                                        outline.MinimumPoint.X;
                                    double hh = outline.MaximumPoint.Y -
                                        outline.MinimumPoint.Y;
                                    log.Info("6MH VIEW Foundation=" +
                                        foundation.Id.IntegerValue +
                                        " Slot=" + (slot + 1) +
                                        " Name=" + views.Views[col].Name +
                                        " PaperMm=" +
                                        UnitUtil.FtToMm(ww).ToString("0.#") +
                                        "x" +
                                        UnitUtil.FtToMm(hh).ToString("0.#"));
                                    if (ww > allowableW || hh > allowableH ||
                                        outline.MinimumPoint.X <
                                            cellLeft + UnitUtil.MmToFt(1) ||
                                        outline.MaximumPoint.X >
                                            cellRight - UnitUtil.MmToFt(1) ||
                                        outline.MinimumPoint.Y <
                                            cy - rowH * 0.5 +
                                                UnitUtil.MmToFt(3) ||
                                        outline.MaximumPoint.Y >
                                            cy + rowH * 0.5 -
                                                UnitUtil.MmToFt(3))
                                        throw new InvalidOperationException(
                                            "Viewport " +
                                            views.Views[col].Name +
                                            " exceeds its assigned 1:25 " +
                                            "cell. Paper " +
                                            UnitUtil.FtToMm(ww).ToString("0.#") +
                                            "x" +
                                            UnitUtil.FtToMm(hh).ToString("0.#") +
                                            " mm; cell maximum " +
                                            UnitUtil.FtToMm(allowableW).ToString("0.#") +
                                            "x" +
                                            UnitUtil.FtToMm(allowableH).ToString("0.#") +
                                            " mm.");
                                }
                                sub.Commit();
                                output.PlacedManholes++;
                                output.PlacedIds.Add(
                                    foundation.Id.IntegerValue);
                            }
                            catch (Exception ex)
                            {
                                if (sub.GetStatus() == TransactionStatus.Started)
                                    sub.RollBack();
                                output.SkippedManholes++;
                                output.Problems.Add(
                                    foundation.Id.IntegerValue +
                                    ": " + ex.Message);
                                log.Warn("6MH ROW SKIPPED " +
                                    foundation.Id.IntegerValue +
                                    " " + ex.Message);
                            }
                        }
                    }

                    // Never leave a misleading empty test sheet.
                    if (output.PlacedManholes == 0)
                    {
                        tx.RollBack();
                        output.Sheet = null;
                        log.Warn("6MH: no rows fit. Sheet rolled back.");
                        return output;
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit failed to commit the six-row test sheet.");
                    log.Info("6MH COMMITTED SheetId=" +
                        sheet.Id.IntegerValue + " Placed=" +
                        output.PlacedManholes + " Skipped=" +
                        output.SkippedManholes);
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    throw;
                }
            }
            return output;
        }

        // .NET Framework 4.8 replacement for Enumerable.ToHashSet().
        private static HashSet<string> ToHashSetSafe(
            this IEnumerable<string> values)
        {
            return new HashSet<string>(values,
                StringComparer.Ordinal);
        }
    }
}
