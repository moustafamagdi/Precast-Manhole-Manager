using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class FirstProductionSheetService
    {
        internal static ViewSheet Build(Document doc, Element foundation,
            DraftSheetResult draft, IList<UnifiedOpeningReviewRow> actual,
            DiagnosticLogger log, bool partialLinkCoverage = false)
        {
            if (draft.Views.Count != 5)
                throw new InvalidOperationException("Five views required for production.");
            string name = "MH_" + foundation.Id.IntegerValue +
                (partialLinkCoverage ? "_OPENINGS_PARTIAL_R01" : "_OPENINGS_R01");
            if (new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>().Any(x => x.Name == name))
                throw new InvalidOperationException(
                    "Production sheet already exists; will not overwrite: " + name);

            var placedIds = new HashSet<int>(new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport)).Cast<Viewport>()
                .Select(x => x.ViewId.IntegerValue));
            if (draft.Views.Any(x => placedIds.Contains(x.Id.IntegerValue)))
                throw new InvalidOperationException(
                    "A required view is already on another sheet. " +
                    "Existing manually arranged views must be preserved.");

            FamilySymbol titleType = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .OrderByDescending(x => (x.Name ?? "").IndexOf(
                    "A0", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 :
                    (x.Name ?? "").IndexOf(
                        "A1", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                .FirstOrDefault();
            if (titleType == null)
                throw new InvalidOperationException("A0/A1 titleblock not loaded.");

            ViewSheet sheet = ViewSheet.Create(doc, titleType.Id);
            sheet.Name = name;
            doc.Regenerate();
            double l = sheet.Outline.Min.U;
            double b = sheet.Outline.Min.V;
            double w = sheet.Outline.Max.U - l;
            double h = sheet.Outline.Max.V - b;
            // Preserve the right-hand titleblock strip. One manhole
            // uses five columns and leaves a large bottom opening table.
            double left = l + UnitUtil.MmToFt(22);
            double right = l + w - UnitUtil.MmToFt(165);
            double content = right - left;
            if (content < UnitUtil.MmToFt(520) ||
                h < UnitUtil.MmToFt(400))
                throw new InvalidOperationException(
                    "Sheet/titleblock too small for one-manhole output.");

            var viewports = new List<Viewport>();
            double top = b + h - UnitUtil.MmToFt(40);
            double rowBottom = b + h * 0.43;
            double cellW = content / 5.0;
            for (int i = 0; i < 5; i++)
            {
                View v = draft.Views[i];
                if (v.Scale != 25)
                    throw new InvalidOperationException(
                        "Production requires 1:25: " + v.Name);
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id))
                    throw new InvalidOperationException(
                        "View cannot be added to sheet: " + v.Name);
                viewports.Add(Viewport.Create(doc, sheet.Id, v.Id,
                    new XYZ(left + (i + 0.5) * cellW,
                        (top + rowBottom) * 0.5, 0)));
            }
            doc.Regenerate();
            for (int i = 0; i < 5; i++)
            {
                Viewport port = viewports[i];
                Outline bounds = port.GetBoxOutline();
                double viewW = bounds.MaximumPoint.X - bounds.MinimumPoint.X;
                double viewH = bounds.MaximumPoint.Y - bounds.MinimumPoint.Y;
                if (viewW > cellW - UnitUtil.MmToFt(3) ||
                    viewH > top - rowBottom - UnitUtil.MmToFt(16))
                    throw new InvalidOperationException(
                        "Production viewport too large at 1:25: " +
                        draft.Views[i].Name + " (" +
                        UnitUtil.FtToMm(viewW).ToString("0.#") + "x" +
                        UnitUtil.FtToMm(viewH).ToString("0.#") + " mm).");
                port.SetBoxCenter(new XYZ(
                    left + (i + 0.5) * cellW,
                    top - UnitUtil.MmToFt(2) - viewH * 0.5, 0));
            }
            doc.Regenerate();
            for (int i = 0; i < 5; i++)
            {
                Viewport port = viewports[i];
                Outline viewBounds = port.GetBoxOutline();
                // Place the viewport title immediately below the drawing.
                // Use the actual label outline rather than assuming its
                // default origin is below/centered.
                Outline label = port.GetLabelOutline();
                if (label != null && label.MaximumPoint.X > label.MinimumPoint.X &&
                    label.MaximumPoint.Y > label.MinimumPoint.Y)
                {
                    double x = (viewBounds.MinimumPoint.X +
                        viewBounds.MaximumPoint.X -
                        label.MinimumPoint.X - label.MaximumPoint.X) / 2.0;
                    double y = viewBounds.MinimumPoint.Y -
                        UnitUtil.MmToFt(4) - label.MaximumPoint.Y;
                    XYZ old = port.LabelOffset;
                    port.LabelOffset = new XYZ(old.X + x, old.Y + y, old.Z);
                    doc.Regenerate();
                    label = port.GetLabelOutline();
                    if (label.MinimumPoint.Y < rowBottom +
                        UnitUtil.MmToFt(5) ||
                        label.MaximumPoint.Y >
                            viewBounds.MinimumPoint.Y -
                                UnitUtil.MmToFt(2) ||
                        label.MinimumPoint.X < left + i * cellW ||
                        label.MaximumPoint.X > left + (i + 1) * cellW)
                        throw new InvalidOperationException(
                            "View title exceeds its reserved cell: " +
                            draft.Views[i].Name);
                }
            }

            // Lightweight but real Revit sheet annotation, using VERIFIED
            // actual opening records only. Not a replacement for model
            // dimension strings; these will be a later production gate.
            TextNoteType noteType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .FirstOrDefault();
            if (noteType == null)
                throw new InvalidOperationException(
                    "Load a Revit text type for the opening schedule.");
            string manholeName = ManholeViewTitleService.Name(
                doc, foundation, log);
            var lines = new List<string>
            {
                manholeName + (partialLinkCoverage
                    ? " | PARTIAL LINK COVERAGE - NOT FOR ISSUE"
                    : " | OPENING SETOUT - PRELIMINARY / VERIFY"),
                "WALL     MEP SOURCE       CLEAR OPENING (mm)    " +
                "OFFSET FROM WALL START (mm)     BOTTOM ABOVE BASE (mm)"
            };
            BoundingBoxXYZ foundationBox = foundation.get_BoundingBox(null);
            double baseTopMm = UnitUtil.FtToMm(foundationBox.Max.Z);
            int sequence = 1;
            foreach (UnifiedOpeningReviewRow row in actual
                .OrderBy(x => x.Source.WallNumber)
                .ThenBy(x => x.Source.OffsetFromWallStartMm))
            {
                var p = row.Source;
                double openingBottomMm = p.EffectiveOpeningZmm -
                    p.CutHeightMm * 0.5 - baseTopMm;
                lines.Add("O" + sequence.ToString("00") + " / W" +
                    p.WallNumber + "   " + p.LinkedElementId +
                    "       " + p.CutWidthMm.ToString("0") + " x " +
                    p.CutHeightMm.ToString("0") + "           " +
                    p.OffsetFromWallStartMm.ToString("0") +
                    "                    " +
                    openingBottomMm.ToString("0"));
                sequence++;
            }
            if (sequence == 1)
                lines.Add("NO APPROVED ACTUAL PENETRATIONS FOUND.");
            string schedule = string.Join(Environment.NewLine, lines);
            double noteWidth = content - UnitUtil.MmToFt(12);
            var textOptions = new TextNoteOptions(noteType.Id);
            TextNote note = TextNote.Create(doc, sheet.Id,
                new XYZ(left + UnitUtil.MmToFt(5),
                    rowBottom - UnitUtil.MmToFt(28), 0),
                noteWidth, schedule, textOptions);
            doc.Regenerate();
            BoundingBoxXYZ noteBounds = note.get_BoundingBox(sheet);
            if (noteBounds == null ||
                noteBounds.Min.X < left ||
                noteBounds.Max.X > right ||
                noteBounds.Max.Y > rowBottom - UnitUtil.MmToFt(5) ||
                noteBounds.Min.Y < b + UnitUtil.MmToFt(16))
                throw new InvalidOperationException(
                    "Opening setout text overflows this sheet. " +
                    "Change the text type/titleblock before issuing.");
            log.Info("PRODUCTION SHEET " + sheet.Id.IntegerValue +
                " Coverage=" + (partialLinkCoverage ? "PARTIAL NOT FOR ISSUE" : "ALL LINKS AVAILABLE") +
                " Openings=" + actual.Count + " Layout=1 PLAN + 4 EXT SECTIONS");
            return sheet;
        }
    }
}