using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class FirstProductionSheetService
    {
        internal static ViewSheet Build(Document doc, Element foundation,
            DraftSheetResult draft, IList<UnifiedOpeningReviewRow> actual,
            DiagnosticLogger log)
        {
            if (draft.Views.Count != 5)
                throw new InvalidOperationException("Five views required for production.");
            string name = "MH_" + foundation.Id.IntegerValue +
                "_OPENINGS_R01";
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
                ManholeViewTitleService.UpdateDetailNumber(v, i, log);
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
            string schedule = ScheduleText(doc, foundation, actual, log);
            double noteWidth = content - UnitUtil.MmToFt(12);
            var textOptions = new TextNoteOptions(noteType.Id);
            TextNote note = TextNote.Create(doc, sheet.Id,
                new XYZ(left + UnitUtil.MmToFt(5),
                    rowBottom - UnitUtil.MmToFt(28), 0),
                noteWidth, schedule, textOptions);
            MarkTable(note, foundation);
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
                " Scope=OPERATOR SELECTED LOADED LINKS" +
                " Openings=" + actual.Count + " Layout=1 PLAN + 4 EXT SECTIONS");
            return sheet;
        }

        internal static ViewSheet FindExisting(Document doc, Element foundation)
        {
            string prefix = "MH_" + foundation.Id.IntegerValue;
            var matches = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>().Where(x => x.Name == prefix + "_OPENINGS_R01" ||
                    x.Name == prefix + "_OPENINGS_PARTIAL_R01").ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException("Multiple production sheets found for this manhole.");
            return matches.SingleOrDefault();
        }

        internal static void Refresh(Document doc, Element foundation, ViewSheet sheet,
            IList<UnifiedOpeningReviewRow> actual, DiagnosticLogger log)
        {
            // OwnerViewId includes hidden notes, unlike a visible-in-view collector.
            var allNotes = new FilteredElementCollector(doc).OfClass(typeof(TextNote))
                .Cast<TextNote>().Where(n => n.OwnerViewId == sheet.Id).ToList();
            var notes = allNotes.Where(n => IsOwnedTable(n, foundation)).ToList();
            if (notes.Count == 0)
                notes = allNotes.Where(n => !HasTableMarker(n) && IsLegacyTableText(n.Text)).ToList();
            log.Info("OPENING TABLE LOOKUP Sheet=" + sheet.Id.IntegerValue +
                " TextNotes=" + allNotes.Count + " MatchingTables=" + notes.Count);
            foreach (TextNote candidate in allNotes)
                log.Info("SHEET TEXT NOTE Id=" + candidate.Id.IntegerValue +
                    " Text=" + (candidate.Text ?? "").Replace("\r", " ").Replace("\n", " "));

            if (notes.Count == 0)
            {
                TextNoteType type = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>().FirstOrDefault();
                if (type == null)
                    throw new InvalidOperationException("Load a text type for the opening table.");
                double left = sheet.Outline.Min.U + UnitUtil.MmToFt(27);
                double right = sheet.Outline.Max.U - UnitUtil.MmToFt(172);
                double top = sheet.Outline.Min.V +
                    (sheet.Outline.Max.V - sheet.Outline.Min.V) * 0.43 - UnitUtil.MmToFt(28);
                if (right - left < UnitUtil.MmToFt(100))
                    throw new InvalidOperationException("Sheet has insufficient width for an opening table.");
                TextNote created = TextNote.Create(doc, sheet.Id,
                    new XYZ(left, top, 0), right - left,
                    ScheduleText(doc, foundation, actual, log), new TextNoteOptions(type.Id));
                doc.Regenerate();
                // Do not cover existing manual annotations or viewports when recovering a missing table.
                BoundingBoxXYZ box = created.get_BoundingBox(sheet);
                bool overlaps = allNotes.Any(n => Overlaps(box, n.get_BoundingBox(sheet)));
                foreach (Viewport port in new FilteredElementCollector(doc).OfClass(typeof(Viewport))
                    .Cast<Viewport>().Where(v => v.SheetId == sheet.Id))
                {
                    overlaps |= Overlaps(box, port.GetBoxOutline());
                    overlaps |= Overlaps(box, port.GetLabelOutline());
                }
                if (overlaps)
                    throw new InvalidOperationException("Missing opening table: its recovery area contains " +
                        "other sheet content. Free the lower table area and run Generate again.");
                notes.Add(created);
                log.Info("OPENING TABLE RECREATED Id=" + created.Id.IntegerValue);
            }
            // Copied tool tables on this sheet are refreshed consistently; no note is deleted.
            foreach (TextNote note in notes)
            {
                note.Text = ScheduleText(doc, foundation, actual, log);
                MarkTable(note, foundation);
                doc.Regenerate();
                BoundingBoxXYZ bounds = note.get_BoundingBox(sheet);
                if (bounds == null || bounds.Min.X < sheet.Outline.Min.U ||
                    bounds.Max.X > sheet.Outline.Max.U || bounds.Min.Y < sheet.Outline.Min.V ||
                    bounds.Max.Y > sheet.Outline.Max.V)
                    throw new InvalidOperationException("Updated opening table exceeds sheet bounds.");
                log.Info("OPENING TABLE UPDATED Id=" + note.Id.IntegerValue);
            }
            sheet.Name = "MH_" + foundation.Id.IntegerValue + "_OPENINGS_R01";
            log.Info("PRODUCTION SHEET UPDATED " + sheet.Id.IntegerValue +
                " Openings=" + actual.Count + " Existing layout preserved.");
        }

        private static readonly Guid TableSchemaId = new Guid("628949B5-F6E6-49E6-92C0-2C7B169DC592");

        private static void MarkTable(TextNote note, Element foundation)
        {
            Schema schema = Schema.Lookup(TableSchemaId);
            if (schema == null)
            {
                var builder = new SchemaBuilder(TableSchemaId);
                builder.SetSchemaName("HatcoManholeOpeningTable");
                builder.SetReadAccessLevel(AccessLevel.Public);
                builder.SetWriteAccessLevel(AccessLevel.Public);
                builder.AddSimpleField("FoundationUniqueId", typeof(string));
                schema = builder.Finish();
            }
            var entity = new Entity(schema);
            entity.Set<string>(schema.GetField("FoundationUniqueId"), foundation.UniqueId);
            note.SetEntity(entity);
        }

        private static bool HasTableMarker(TextNote note)
        {
            Schema schema = Schema.Lookup(TableSchemaId);
            return schema != null && note.GetEntity(schema).IsValid();
        }

        private static bool IsOwnedTable(TextNote note, Element foundation)
        {
            Schema schema = Schema.Lookup(TableSchemaId);
            if (schema == null) return false;
            Entity entity = note.GetEntity(schema);
            return entity.IsValid() && entity.Get<string>(schema.GetField("FoundationUniqueId")) ==
                foundation.UniqueId;
        }

        internal static bool IsLegacyTableText(string text)
        {
            // Revit text may contain paragraph breaks, tabs and non-breaking spaces.
            string normalized = new string((text ?? "").Where(c => !char.IsWhiteSpace(c))
                .Select(char.ToUpperInvariant).ToArray());
            return normalized.Contains("MEPSOURCE") && normalized.Contains("CLEAROPENING(MM)") &&
                normalized.Contains("BOTTOMABOVEBASE(MM)") &&
                (normalized.Contains("OPENINGSETOUT") || normalized.Contains("PARTIALLINKCOVERAGE"));
        }

        private static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return a != null && b != null && a.Min.X < b.Max.X && a.Max.X > b.Min.X &&
                a.Min.Y < b.Max.Y && a.Max.Y > b.Min.Y;
        }

        private static bool Overlaps(BoundingBoxXYZ a, Outline b)
        {
            return a != null && b != null && a.Min.X < b.MaximumPoint.X &&
                a.Max.X > b.MinimumPoint.X && a.Min.Y < b.MaximumPoint.Y && a.Max.Y > b.MinimumPoint.Y;
        }

        private static string ScheduleText(Document doc, Element foundation,
            IList<UnifiedOpeningReviewRow> actual, DiagnosticLogger log)
        {
            string manholeName = ManholeViewTitleService.Name(
                doc, foundation, log);
            var lines = new List<string>
            {
                manholeName + " | OPENING SETOUT - PRELIMINARY / VERIFY",
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
            return string.Join(Environment.NewLine, lines);
        }
    }
}
