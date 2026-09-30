using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class SevenRowSheetResult
    {
        public ViewSheet Sheet { get; set; }
        public int PlacedManholes { get; set; }
        public int SkippedManholes { get; set; }
        public List<int> PlacedIds { get; } = new List<int>();
        public List<string> Problems { get; } = new List<string>();
    }

    // First controlled batch: at most SIX explicitly chosen, clean manholes.
    // Existing user-laid-out sheets are NEVER repositioned or modified.
    internal static class SevenRowManholeSheetService
    {
        private const string SheetName = "HATCO_PRECAST_7MH_TEST_01";
        private const double PaperLeftMm = 22;
        private const double PaperTopMm = 14;
        private const double PaperBottomMm = 14;
        // The right titleblock/key-plan strip seen in the supplied sample.
        // Kept conservative until the user validates the first batch sheet.
        private const double ReserveRightMm = 165;
        private const double GapMm = 5;
        private const double LabelGapMm = 3;
        private const double LabelBandMm = 12;
        private static readonly double[] ColumnCenterFraction =
            { 0.105, 0.305, 0.505, 0.705, 0.905 };

        public static SevenRowSheetResult Generate(Document doc,
            IList<Element> foundations, ViewSheet sampleSheet,
            DiagnosticLogger log)
        {
            if (foundations == null || foundations.Count == 0 ||
                foundations.Count > 12)
                throw new InvalidOperationException(
                    "Select between 1 and 12 candidate manholes; up to 7 fitting rows will be placed.");
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
                    "The seven-row test sheet already exists: " +
                    existing.SheetNumber + ". It is protected from being overwritten.");

            var output = new SevenRowSheetResult();
            // Preflight is read only: never classify a registered OPEN
            // issue or a wall with legacy cuts as production-ready.
            var known = ManholeReviewRegistry.Load(doc)
                .Where(x => x.Status == "OPEN")
                .Select(x => x.FoundationUniqueId)
                .ToHashSetSafe();
            var ready = new List<Tuple<Element, VirtualFoundationResult>>();
            foreach (Element foundation in foundations)
            {
                if (ready.Count >= 12) break;
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
                    log.Warn("7MH PREFLIGHT SKIP Foundation=" +
                        foundation.Id.IntegerValue + " " + ex.Message);
                }
            }
            if (ready.Count == 0)
                return output;

            // One outer transaction creates the sheet. Each manhole
            // uses a subtransaction; an invalid individual row rolls back
            // without discarding completed clean rows.
            using (var tx = new Transaction(doc,
                "HATCO - First seven-row precast sheet"))
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
                            "Selected titleblock is too small for seven rows " +
                            "with a reserved right-hand titleblock strip.");

                    double rowH = usableH / 7.0;
                    double cellW = usableW / 5.0;
                    double allowableW = cellW - UnitUtil.MmToFt(GapMm);
                    double labelBand = UnitUtil.MmToFt(LabelBandMm);
                    double allowableH = rowH - labelBand - UnitUtil.MmToFt(3);
                    log.Info("7MH SHEET SheetSizeMm=" +
                        UnitUtil.FtToMm(sheetW).ToString("0.#") + "x" +
                        UnitUtil.FtToMm(sheetH).ToString("0.#") +
                        " ContentWmm=" + UnitUtil.FtToMm(usableW).ToString("0.#") +
                        " RowHmm=" + UnitUtil.FtToMm(rowH).ToString("0.#") +
                        " CellWmm=" + UnitUtil.FtToMm(cellW).ToString("0.#"));

                    // Prefer the user's tested viewport type, including
                    // its no-bubble title definition. Never change old ports.
                    ElementType viewportType =
                        new FilteredElementCollector(doc)
                        .OfClass(typeof(ElementType)).Cast<ElementType>()
                        .FirstOrDefault(t => t.Category != null &&
                            t.Category.Id.IntegerValue ==
                                (int)BuiltInCategory.OST_Viewports &&
                            string.Equals(t.Name, "NO BUBBLE NTS",
                                StringComparison.OrdinalIgnoreCase));
                    if (viewportType == null)
                        log.Warn("7MH viewport type NO BUBBLE NTS not found; " +
                            "using the project default type.");

                    // Anchor each accepted manhole in its own row,
                    // preserving the user-provided left-to-right order:
                    // PLAN, W1, W2, W3, W4 at 1:25.
                    for (int slot = 0; slot < ready.Count &&
                        output.PlacedManholes < 7; slot++)
                    {
                        Element foundation = ready[slot].Item1;
                        VirtualFoundationResult footprint = ready[slot].Item2;
                        // Compact successfully committed rows upward: a
                        // rejected first manhole must not waste row 1.
                        int rowIndex = output.PlacedManholes;
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            try
                            {
                                string viewPrefix = "MH_" +
                                    foundation.Id.IntegerValue +
                                    "_DRAFT_2D";
                                HashSet<int> previouslyExisting =
                                    new HashSet<int>(
                                        new FilteredElementCollector(doc)
                                            .OfClass(typeof(View))
                                            .Cast<View>()
                                            .Where(v => !v.IsTemplate &&
                                                (v.Name == viewPrefix + "_PLAN" ||
                                                 v.Name == viewPrefix + "_W1" ||
                                                 v.Name == viewPrefix + "_W2" ||
                                                 v.Name == viewPrefix + "_W3" ||
                                                 v.Name == viewPrefix + "_W4" ||
                                                 v.Name == viewPrefix + "_OUT_W1" ||
                                                 v.Name == viewPrefix + "_OUT_W2" ||
                                                 v.Name == viewPrefix + "_OUT_W3" ||
                                                 v.Name == viewPrefix + "_OUT_W4"))
                                            .Select(v => v.Id.IntegerValue));
                                DraftSheetResult views =
                                    DraftManholeSheetService.Generate(
                                        doc, foundation, footprint, log);
                                if (views.Views.Count != 5)
                                    throw new InvalidOperationException(
                                        "Expected exactly five 2D views.");
                                // DraftManholeSheetService now writes the
                                // readable manhole name + W1-W4 title.
                                // Never overwrite it with Element IDs.
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
                                    paperTop - rowH * (rowIndex + 0.5);
                                var ports = new List<Viewport>();
                                for (int col = 0; col < 5; col++)
                                {
                                    View v = views.Views[col];
                                    double cx = left + paperLeft +
                                        usableW * ColumnCenterFraction[col];
                                    Viewport placed = Viewport.Create(doc,
                                        sheet.Id, v.Id,
                                        new XYZ(cx, cy, 0));
                                    if (viewportType != null)
                                        placed.ChangeTypeId(viewportType.Id);
                                    ports.Add(placed);
                                }
                                doc.Regenerate();

                                // Place all drawing boxes on a common
                                // TOP baseline. Leave a dedicated lower
                                // title band, keeping text away from the
                                // next manhole row even when view heights
                                // differ.
                                double rowTop = bottom + sheetH -
                                    paperTop - rowH * rowIndex;
                                var preliminary = ports.Select(p =>
                                    p.GetBoxOutline()).ToList();
                                for (int col = 0; col < 5; col++)
                                {
                                    Outline o = preliminary[col];
                                    double h = o.MaximumPoint.Y -
                                        o.MinimumPoint.Y;
                                    double targetY = rowTop -
                                        UnitUtil.MmToFt(2) - h * 0.5;
                                    XYZ old = ports[col].GetBoxCenter();
                                    ports[col].SetBoxCenter(new XYZ(
                                        old.X, targetY, 0));
                                }
                                doc.Regenerate();

                                var boxes = ports.Select(p =>
                                    p.GetBoxOutline()).ToList();
                                var labels = new List<Outline>();
                                for (int col = 0; col < 5; col++)
                                {
                                    Viewport vp = ports[col];
                                    Outline o = boxes[col];
                                    double w = o.MaximumPoint.X -
                                        o.MinimumPoint.X;
                                    double h = o.MaximumPoint.Y -
                                        o.MinimumPoint.Y;
                                    double cellLeft = left + paperLeft +
                                        usableW * (col / 5.0);
                                    double cellRight = cellLeft + cellW;

                                    // GetLabelOutline is provided in Revit
                                    // 2024. Some viewport types hide labels;
                                    // a missing/empty label needs no offset.
                                    Outline label = vp.GetLabelOutline();
                                    if (label != null &&
                                        label.MaximumPoint.X >
                                            label.MinimumPoint.X &&
                                        label.MaximumPoint.Y >
                                            label.MinimumPoint.Y)
                                    {
                                        // Shrink a longer label line. Actual
                                        // text width is measured separately.
                                        vp.LabelLineLength = UnitUtil.MmToFt(22);
                                        doc.Regenerate();
                                        label = vp.GetLabelOutline();
                                        double dx = (o.MinimumPoint.X +
                                            o.MaximumPoint.X) * 0.5 -
                                            (label.MinimumPoint.X +
                                            label.MaximumPoint.X) * 0.5;
                                        double dy = o.MinimumPoint.Y -
                                            UnitUtil.MmToFt(LabelGapMm) -
                                            label.MaximumPoint.Y;
                                        XYZ offset = vp.LabelOffset;
                                        vp.LabelOffset = new XYZ(offset.X + dx,
                                            offset.Y + dy, offset.Z);
                                        doc.Regenerate();
                                        label = vp.GetLabelOutline();
                                    }
                                    labels.Add(label);
                                    log.Info("7MH VIEW Foundation=" +
                                        foundation.Id.IntegerValue +
                                        " Slot=" + (rowIndex + 1) +
                                        " Name=" + views.Views[col].Name +
                                        " PaperMm=" +
                                        UnitUtil.FtToMm(w).ToString("0.#") +
                                        "x" +
                                        UnitUtil.FtToMm(h).ToString("0.#") +
                                        " Label=" + (label == null ?
                                            "NONE" : "CHECKED"));

                                    if (w > allowableW || h > allowableH ||
                                        o.MinimumPoint.X < cellLeft +
                                            UnitUtil.MmToFt(1) ||
                                        o.MaximumPoint.X > cellRight -
                                            UnitUtil.MmToFt(1) ||
                                        o.MaximumPoint.Y > rowTop -
                                            UnitUtil.MmToFt(1) ||
                                        o.MinimumPoint.Y <
                                            rowTop - rowH + labelBand)
                                        throw new InvalidOperationException(
                                            views.Views[col].Name +
                                            " drawing exceeds 1:25 seven-row " +
                                            "cell: " +
                                            UnitUtil.FtToMm(w).ToString("0.#") +
                                            "x" +
                                            UnitUtil.FtToMm(h).ToString("0.#") +
                                            " mm, max " +
                                            UnitUtil.FtToMm(allowableW)
                                                .ToString("0.#") +
                                            "x" +
                                            UnitUtil.FtToMm(allowableH)
                                                .ToString("0.#") + " mm.");

                                    if (label != null &&
                                        label.MaximumPoint.X >
                                            label.MinimumPoint.X &&
                                        label.MaximumPoint.Y >
                                            label.MinimumPoint.Y)
                                    {
                                        if (label.MinimumPoint.X <
                                                cellLeft + UnitUtil.MmToFt(1) ||
                                            label.MaximumPoint.X >
                                                cellRight - UnitUtil.MmToFt(1) ||
                                            label.MinimumPoint.Y <
                                                rowTop - rowH +
                                                    UnitUtil.MmToFt(1) ||
                                            label.MaximumPoint.Y >
                                                o.MinimumPoint.Y -
                                                    UnitUtil.MmToFt(1))
                                            throw new InvalidOperationException(
                                                "Viewport title does not fit " +
                                                "below " + views.Views[col].Name +
                                                ". Check MH view title length " +
                                                "or template title family.");
                                    }
                                }
                                // Revit outlines after all label edits:
                                // verify title-to-title, title-to-drawing,
                                // and drawing-to-drawing within the row.
                                boxes = ports.Select(p =>
                                    p.GetBoxOutline()).ToList();
                                labels = ports.Select(p =>
                                    p.GetLabelOutline()).ToList();
                                for (int i = 0; i < ports.Count; i++)
                                for (int j = i + 1; j < ports.Count; j++)
                                {
                                    if (Intersects(boxes[i], boxes[j],
                                            UnitUtil.MmToFt(1)) ||
                                        Intersects(labels[i], boxes[j],
                                            UnitUtil.MmToFt(1)) ||
                                        Intersects(labels[j], boxes[i],
                                            UnitUtil.MmToFt(1)) ||
                                        Intersects(labels[i], labels[j],
                                            UnitUtil.MmToFt(1)))
                                        throw new InvalidOperationException(
                                            "Viewport/title overlap between " +
                                            views.Views[i].Name + " and " +
                                            views.Views[j].Name + ".");
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
                                log.Warn("7MH ROW SKIPPED " +
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
                        log.Warn("7MH: no rows fit. Sheet rolled back.");
                        return output;
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit failed to commit the six-row test sheet.");
                    log.Info("7MH COMMITTED SheetId=" +
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

        private static bool Intersects(Outline a, Outline b,
            double gap)
        {
            if (a == null || b == null ||
                a.MaximumPoint.X <= a.MinimumPoint.X ||
                a.MaximumPoint.Y <= a.MinimumPoint.Y ||
                b.MaximumPoint.X <= b.MinimumPoint.X ||
                b.MaximumPoint.Y <= b.MinimumPoint.Y)
                return false;
            return a.MinimumPoint.X < b.MaximumPoint.X + gap &&
                a.MaximumPoint.X + gap > b.MinimumPoint.X &&
                a.MinimumPoint.Y < b.MaximumPoint.Y + gap &&
                a.MaximumPoint.Y + gap > b.MinimumPoint.Y;
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
