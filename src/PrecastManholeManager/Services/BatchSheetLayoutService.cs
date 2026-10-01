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
        internal static void Reserve(Document doc, IList<Element> foundations, FamilySymbol titleblock)
        {
            var schema = Storage();
            var occupied = new HashSet<int>();
            var pages = new Dictionary<int, ViewSheet>();
            int next = 0;
            foreach (Element foundation in foundations)
            {
                var old = Find(doc, foundation);
                if (old == null) continue;
                if (old.Index < 0 || !occupied.Add(old.Index))
                    throw new InvalidOperationException("Duplicate or invalid saved batch position.");
                if (pages.ContainsKey(Page(old.Index)) && pages[Page(old.Index)].Id != old.Sheet.Id)
                    throw new InvalidOperationException("Inconsistent saved batch sheet mapping.");
                pages[Page(old.Index)] = old.Sheet;
            }
            next = NextSlotIndex(occupied);
            foreach (Element foundation in foundations)
            {
                var slot = Find(doc, foundation);
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
            }
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
        internal static void SetStatus(Document doc, Element foundation, BatchSheetSlot slot, string status)
        {
            var bounds = Bounds(slot);
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
            else slot.Note.Text = text;
        }
        internal static void Place(Document doc, Element foundation, BatchSheetSlot slot,
            IList<View> views, IList<UnifiedOpeningReviewRow> actual, DiagnosticLogger log)
        {
            if (views.Count != 5) throw new InvalidOperationException("Five production views required.");
            var viewportType = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                .FirstOrDefault(t => t.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_Viewports && t.Name == "NO BUBBLE NTS");
            for (int col = 0; col < views.Count; col++)
            {
                View view = views[col];
                if (view.Scale != 25) throw new InvalidOperationException("Batch views must use 1:25: " + view.Name);
                var port = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                    .SingleOrDefault(p=>p.ViewId == view.Id);
                if (port != null && port.SheetId != slot.Sheet.Id)
                {
                    var old = doc.GetElement(port.SheetId) as ViewSheet;
                    string prefix = "MH_" + foundation.Id.IntegerValue;
                    if (old == null || (old.Name != prefix + "_OPENINGS_R01" && old.Name != prefix + "_OPENINGS_PARTIAL_R01"))
                        throw new InvalidOperationException("View is placed on a separate manual sheet: " + view.Name);
                    doc.Delete(port.Id);
                    port = null;
                }
                if (port == null) port = Viewport.Create(doc, slot.Sheet.Id, view.Id, XYZ.Zero);
                if (viewportType != null) port.ChangeTypeId(viewportType.Id);
                port.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER).Set(
                    ManholeIdentityStore.Read(foundation) + (col == 0 ? "-P" : "-W" + col));
            }
            SetStatus(doc, foundation, slot, "OPENINGS: " + string.Join("; ", actual.OrderBy(r=>r.Source.WallNumber)
                .Select(r=>"W" + r.Source.WallNumber + " " + r.OpeningSize + " / source " + r.SourceId)));
            doc.Regenerate();
            foreach (string warning in Arrange(doc, foundation, slot))
                log.Warn("BATCH LAYOUT REVIEW: " + warning);
            log.Info("BATCH ROW PLACED Foundation=" + foundation.Id.IntegerValue + " Sheet=" + slot.Sheet.SheetNumber + " Row=" + (slot.Row + 1));
        }
        internal static List<string> Arrange(Document doc, Element foundation, BatchSheetSlot slot)
        {
            var warnings = new List<string>();
            var b = Bounds(slot);
            double cell = (b[1] - b[0]) / 5;
            string prefix = "MH_" + foundation.Id.IntegerValue + "_PROD_2D";
            var ports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .Where(p=>p.SheetId == slot.Sheet.Id).ToList();
            doc.Regenerate();
            for (int col = 0; col < 5; col++)
            {
                string name = prefix + (col == 0 ? "_PLAN" : "_OUT_W" + col);
                var port = ports.SingleOrDefault(p=>doc.GetElement(p.ViewId).Name == name);
                if (port == null) throw new InvalidOperationException("Reserved row is missing view " + name);
                var box = port.GetBoxOutline();
                double width = box.MaximumPoint.X - box.MinimumPoint.X;
                double height = box.MaximumPoint.Y - box.MinimumPoint.Y;
                if (width > cell - UnitUtil.MmToFt(4) || height > b[3] - UnitUtil.MmToFt(24))
                    warnings.Add("View and dimensions exceed reserved row at 1:25: " + name);
                port.SetBoxCenter(new XYZ(b[0] + cell * (col + .5), b[2] - UnitUtil.MmToFt(10) - height / 2, 0));
                port.LabelLineLength = UnitUtil.MmToFt(22);
                doc.Regenerate();
                box = port.GetBoxOutline();
                var label = port.GetLabelOutline();
                if (label != null && label.MaximumPoint.X > label.MinimumPoint.X)
                {
                    var offset = port.LabelOffset;
                    port.LabelOffset = new XYZ(offset.X + (box.MinimumPoint.X + box.MaximumPoint.X - label.MinimumPoint.X - label.MaximumPoint.X) / 2,
                        offset.Y + box.MinimumPoint.Y - UnitUtil.MmToFt(3) - label.MaximumPoint.Y, offset.Z);
                    doc.Regenerate(); label = port.GetLabelOutline();
                    if (label.MinimumPoint.X < b[0] + cell * col || label.MaximumPoint.X > b[0] + cell * (col + 1) ||
                        label.MinimumPoint.Y < b[2] - b[3] + UnitUtil.MmToFt(2))
                        warnings.Add("View title exceeds its reserved cell: " + name);
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
