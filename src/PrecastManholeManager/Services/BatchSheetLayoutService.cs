using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class BatchSheetSlot
    {
        public ViewSheet Sheet;
        public int Index;
        public TextNote Note;
        public int Row => Index % BatchSheetLayoutService.RowsPerSheet;
    }

    internal static class BatchSheetLayoutService
    {
        internal const int RowsPerSheet = 6;
        private static readonly Guid Id = new Guid("EE82A896-0A1D-4D53-A813-85940C28287B");
        private static readonly Guid LegacyThreeRowId = new Guid("C2CA9C23-70BC-493C-9C2D-32701CB0A046");
        internal static void ForgetCopiedReservation(Element foundation)
        {
            foreach (var id in new[] { Id, LegacyThreeRowId })
            {
                var schema = Schema.Lookup(id);
                if (schema != null) foundation.DeleteEntity(schema);
            }
        }
        private static Schema Storage()
        {
            var schema = Schema.Lookup(Id);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(Id);
            builder.SetSchemaName("HatcoBatchManholeSixRowSlot");
            builder.AddSimpleField("Sheet", typeof(string));
            builder.AddSimpleField("Index", typeof(int));
            builder.AddSimpleField("Note", typeof(string));
            return builder.Finish();
        }
        internal static BatchSheetSlot Find(Document doc, Element foundation)
        {
            var legacy = Schema.Lookup(LegacyThreeRowId);
            if (legacy != null && foundation.GetEntity(legacy).IsValid())
                throw new InvalidOperationException("This RVT contains the previous three-row batch layout. Start the six-row run from the original pre-batch RVT; existing reservations have not been moved.");
            var schema = Schema.Lookup(Id);
            if (schema == null) return null;
            var entity = foundation.GetEntity(schema);
            if (!entity.IsValid()) return null;
            var sheet = doc.GetElement(entity.Get<string>(schema.GetField("Sheet"))) as ViewSheet;
            if (sheet == null) throw new InvalidOperationException("Reserved batch sheet is missing for foundation " + foundation.Id.IntegerValue + ". Restore it before rerunning.");
            return new BatchSheetSlot { Sheet = sheet,
                Index = entity.Get<int>(schema.GetField("Index")),
                Note = doc.GetElement(entity.Get<string>(schema.GetField("Note"))) as TextNote };
        }
        internal static int Page(int index) => index / RowsPerSheet;
        internal static int NextSlotIndex(IEnumerable<int> occupied) => occupied.DefaultIfEmpty(-1).Max() + 1;
        internal static int ManholeOrder(string name)
        {
            int n;
            return name != null && name.StartsWith("MH-",StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name.Substring(3),out n) ? n : int.MaxValue;
        }
        internal static void Reserve(Document doc, IList<Element> foundations, FamilySymbol titleblock, DiagnosticLogger log,
            ISet<string> reserveOnly = null)
        {
            var schema = Storage();
            var occupied = new HashSet<int>();
            var pages = new Dictionary<int, ViewSheet>();
            var existing = new Dictionary<string, BatchSheetSlot>();
            int retained = 0, created = 0, repaired = 0;
            int next = 0;
            foreach (Element foundation in foundations)
            {
                var old = Find(doc, foundation);
                if (old == null) continue;
                existing.Add(foundation.UniqueId, old);
                if (old.Index < 0 || !occupied.Add(old.Index))
                    throw new InvalidOperationException("Duplicate or invalid saved batch position.");
                if (pages.ContainsKey(Page(old.Index)) && pages[Page(old.Index)].Id != old.Sheet.Id)
                    throw new InvalidOperationException("Inconsistent saved batch sheet mapping.");
                pages[Page(old.Index)] = old.Sheet;
                if (old.Note != null && old.Note.OwnerViewId != old.Sheet.Id)
                    throw new InvalidOperationException("Reserved row note belongs to another sheet. Review foundation " + foundation.Id);
            }
            next = NextSlotIndex(occupied);
            foreach (Element foundation in foundations)
            {
                BatchSheetSlot slot;
                if (reserveOnly != null && !reserveOnly.Contains(foundation.UniqueId)) continue;
                existing.TryGetValue(foundation.UniqueId, out slot);
                if (slot?.Note != null)
                {
                    // A valid reservation is already persistent. Preserve its status text,
                    // manually adjusted note and storage without triggering regeneration.
                    retained++;
                    continue;
                }
                bool newReservation = slot == null;
                if (slot == null)
                {
                    int index = next++;
                    ViewSheet sheet;
                    if (!pages.TryGetValue(Page(index), out sheet))
                    {
                        sheet = ViewSheet.Create(doc, titleblock.Id);
                        string code = "MH-BATCH-" + (Page(index) + 1).ToString("000");
                        if (new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                            .Any(s => s.Id != sheet.Id && s.SheetNumber == code))
                            throw new InvalidOperationException("Sheet number already in use: " + code);
                        sheet.SheetNumber = code;
                        sheet.Name = "Precast Manholes - " + (Page(index) + 1).ToString("000");
                        pages[Page(index)] = sheet;
                    }
                    slot = new BatchSheetSlot { Sheet = sheet, Index = index };
                }
                doc.Regenerate();
                SetStatus(doc, foundation, slot, "QUEUED - position reserved");
                var entity = new Entity(schema);
                entity.Set(schema.GetField("Sheet"), slot.Sheet.UniqueId);
                entity.Set(schema.GetField("Index"), slot.Index);
                entity.Set(schema.GetField("Note"), slot.Note.UniqueId);
                foundation.SetEntity(entity);
                if (newReservation) created++; else repaired++;
            }
            log.Info("BATCH RESERVATIONS Retained=" + retained + " Created=" + created + " RepairedMissingNotes=" + repaired);
        }
        private static double[] Bounds(BatchSheetSlot slot)
        {
            var outline = slot.Sheet.Outline;
            double left = outline.Min.U + UnitUtil.MmToFt(22);
            double right = outline.Max.U - UnitUtil.MmToFt(165);
            double top = outline.Max.V - UnitUtil.MmToFt(20);
            double height = (top - outline.Min.V - UnitUtil.MmToFt(20)) / RowsPerSheet;
            if (right - left < UnitUtil.MmToFt(520) || height < UnitUtil.MmToFt(75))
                throw new InvalidOperationException("Titleblock is too small for six reserved manhole rows at 1:25.");
            return new[] { left, right, top - height * slot.Row, height };
        }
        internal static void SetStatus(Document doc, Element foundation, BatchSheetSlot slot, string status, DiagnosticLogger log = null)
        {
            var bounds = log == null ? Bounds(slot) : PerformanceMeasurement.Call(log, "Sheet.Bounds", foundation.Id.ToString(), () => Bounds(slot));
            string text = ManholeIdentityStore.Read(foundation) + " | " + status;
            if (text.Length > 500) text = text.Substring(0, 500) + "... See run report.";
            if (slot.Note == null)
            {
                var type = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                    .FirstOrDefault(t=>t.Name == "HATCO_BATCH_1.8mm");
                if (type == null)
                {
                    var source = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault();
                    if (source == null) throw new InvalidOperationException("Load a text note type first.");
                    type = (TextNoteType)source.Duplicate("HATCO_BATCH_1.8mm");
                    type.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(UnitUtil.MmToFt(1.8));
                }
                slot.Note = TextNote.Create(doc, slot.Sheet.Id, new XYZ(bounds[0], bounds[2], 0),
                    bounds[1] - bounds[0], text, new TextNoteOptions(type.Id));
            }
            else if (log == null) slot.Note.Text = text;
            else PerformanceMeasurement.Call(log, "TextNote.Text", foundation.Id.ToString(), () => { slot.Note.Text = text; });
        }
        internal static void Place(Document doc, Element foundation, BatchSheetSlot slot,
            IList<View> views, IList<UnifiedOpeningReviewRow> actual, DiagnosticLogger log)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (views.Count != 5) throw new InvalidOperationException("Five production views required.");
            ElementType viewportType = null;
            var existingPorts = new FilteredElementCollector(doc).OfClass(typeof(Viewport))
                .Cast<Viewport>().ToList();
            for (int col = 0; col < views.Count; col++)
            {
                View view = views[col];
                if (view.Scale != 25) throw new InvalidOperationException("Batch views must use 1:25: " + view.Name);
                var port = existingPorts.SingleOrDefault(p=>p.ViewId == view.Id);
                if (port != null && port.SheetId != slot.Sheet.Id)
                {
                    var old = doc.GetElement(port.SheetId) as ViewSheet;
                    string prefix = "MH_" + foundation.Id.IntegerValue;
                    if (old == null || (old.Name != prefix + "_OPENINGS_R01" && old.Name != prefix + "_OPENINGS_PARTIAL_R01"))
                        throw new InvalidOperationException("View is placed on a separate manual sheet: " + view.Name);
                    doc.Delete(port.Id);
                    port = null;
                }
                if (port == null) port = PerformanceMeasurement.Call(log, "Viewport.Create", view.Name,
                    () => Viewport.Create(doc, slot.Sheet.Id, view.Id, XYZ.Zero));
                if (viewportType == null) viewportType = ManholeViewPresentationService.RequiredViewportType(doc, port);
                if (port.GetTypeId() != viewportType.Id)
                    PerformanceMeasurement.Call(log, "Viewport.ChangeTypeId", view.Name, () => port.ChangeTypeId(viewportType.Id));
                string detail = ManholeIdentityStore.Read(foundation) + (col == 0 ? "-P" : "-W" + col);
                PerformanceMeasurement.Call(log, "Viewport.DetailNumber", view.Name,
                    () => port.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER).Set(detail));
            }
            SetStatus(doc, foundation, slot, "OPENINGS: " + string.Join("; ", actual.OrderBy(r=>r.Source.WallNumber)
                .Select(r=>"W" + r.Source.WallNumber + " " + r.OpeningSize + " / source " + r.SourceId)), log);
            log.Info("PERF VIEWPORT_CREATION_AND_SETUP Seconds=" + timer.Elapsed.TotalSeconds.ToString("0.000"));
            var arrangeTimer = System.Diagnostics.Stopwatch.StartNew();
            foreach (string warning in Arrange(doc, foundation, slot, log))
                log.Warn("BATCH LAYOUT REVIEW: " + warning);
            log.Info("PERF ROW_ARRANGE Seconds=" + arrangeTimer.Elapsed.TotalSeconds.ToString("0.000"));
            log.Info("BATCH ROW PLACED Foundation=" + foundation.Id.IntegerValue + " Sheet=" + slot.Sheet.SheetNumber + " Row=" + (slot.Row + 1));
            log.Info("PERF PLACE_AND_ARRANGE Seconds=" + timer.Elapsed.TotalSeconds.ToString("0.000"));
        }
        internal static bool HasPreparedViews(Document doc, Element foundation, BatchSheetSlot slot)
        {
            string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D";
            var views = slot.Sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport)
                .Where(p => p != null).Select(p => doc.GetElement(p.ViewId) as View).ToList();
            return views.Any(v => v is ViewPlan && v.Name == prefix + "_PLAN") &&
                Enumerable.Range(1, 4).All(n => views.Any(v => v is ViewSection && v.Name == prefix + "_OUT_W" + n));
        }
        internal static List<string> Arrange(Document doc, Element foundation, BatchSheetSlot slot, DiagnosticLogger log)
        {
            var warnings = new List<string>();
            var b = Bounds(slot);
            double cell = (b[1] - b[0]) / 5;
            string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D";
            var ports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .Where(p=>p.SheetId == slot.Sheet.Id).ToList();
            PerformanceMeasurement.Call(log, "Regenerate.BeforeRow", prefix, () => doc.Regenerate());
            var rowPorts = new List<Viewport>();
            for (int col = 0; col < 5; col++)
            {
                string name = prefix + (col == 0 ? "_PLAN" : "_OUT_W" + col);
                var port = ports.SingleOrDefault(p=>doc.GetElement(p.ViewId).Name == name);
                if (port == null) throw new InvalidOperationException("Reserved row is missing view " + name);
                rowPorts.Add(port);
                var box = PerformanceMeasurement.Call(log, "Viewport.GetBoxOutline", name, () => port.GetBoxOutline());
                double width = box.MaximumPoint.X - box.MinimumPoint.X;
                double height = box.MaximumPoint.Y - box.MinimumPoint.Y;
                if (width > cell - UnitUtil.MmToFt(4) || height > b[3] - UnitUtil.MmToFt(24))
                    warnings.Add("View and dimensions exceed reserved row at 1:25: " + name);
                port.SetBoxCenter(new XYZ(b[0] + cell * (col + .5), b[2] - UnitUtil.MmToFt(10) - height / 2, 0));
                port.LabelLineLength = UnitUtil.MmToFt(22);
            }
            // Rebuild once after moving the whole row, rather than once per viewport.
            PerformanceMeasurement.Call(log, "Regenerate.AfterPositions", prefix, () => doc.Regenerate());
            foreach (var port in rowPorts)
            {
                var box = PerformanceMeasurement.Call(log, "Viewport.GetBoxOutline", port.ViewId.ToString(), () => port.GetBoxOutline());
                var label = PerformanceMeasurement.Call(log, "Viewport.GetLabelOutline", port.ViewId.ToString(), () => port.GetLabelOutline());
                if (label != null && label.MaximumPoint.X > label.MinimumPoint.X)
                {
                    var offset = port.LabelOffset;
                    port.LabelOffset = new XYZ(offset.X + (box.MinimumPoint.X + box.MaximumPoint.X - label.MinimumPoint.X - label.MaximumPoint.X) / 2,
                        offset.Y + box.MinimumPoint.Y - UnitUtil.MmToFt(3) - label.MaximumPoint.Y, offset.Z);
                }
            }
            // Labels must be measured after all offsets have been applied.
            PerformanceMeasurement.Call(log, "Regenerate.AfterLabels", prefix, () => doc.Regenerate());
            for (int col = 0; col < rowPorts.Count; col++)
            {
                var port = rowPorts[col];
                var label = PerformanceMeasurement.Call(log, "Viewport.GetLabelOutline", port.ViewId.ToString(), () => port.GetLabelOutline());
                if (label != null && label.MaximumPoint.X > label.MinimumPoint.X)
                {
                    if (label.MinimumPoint.X < b[0] + cell * col || label.MaximumPoint.X > b[0] + cell * (col + 1) ||
                        label.MinimumPoint.Y < b[2] - b[3] + UnitUtil.MmToFt(2))
                        warnings.Add("View title exceeds its reserved cell: " + doc.GetElement(port.ViewId).Name);
                }
            }
            if (slot.Note != null)
            {
                var note = slot.Note.get_BoundingBox(slot.Sheet);
                if (note == null || note.Min.Y < b[2] - UnitUtil.MmToFt(8))
                    warnings.Add("Row header exceeds reserved note band.");
            }
            return warnings;
        }
    }
}
