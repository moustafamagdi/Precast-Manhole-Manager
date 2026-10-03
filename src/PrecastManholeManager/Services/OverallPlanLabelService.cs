using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    // Managed text annotations, not family tags: refreshed explicitly from actual
    // viewports, never from the reservation register. Manual annotations are untouched.
    internal static class OverallPlanLabelService
    {
        private static readonly Guid SchemaId = new Guid("BFBDF11A-ED63-4D1C-9CDF-95A5D845BC25");
        private static Schema Storage()
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("HatcoOverallManholeLabel");
            builder.AddSimpleField("Foundation", typeof(string));
            builder.AddSimpleField("Anchor", typeof(string));
            return builder.Finish();
        }

        internal static string Update(Document doc, View view, DiagnosticLogger log)
        {
            if (!(view is ViewPlan) || view.IsTemplate || Math.Abs(view.ViewDirection.Z) < .999)
                throw new InvalidOperationException("Open the overall Floor Plan before running this task.");
            if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_TextNotes)))
                throw new InvalidOperationException("Text Notes are hidden in this plan/template. Show them before creating overall labels.");
            if (view.CropBoxActive)
                using (var crop = view.GetCropRegionShapeManager())
                    if (crop.ShapeSet || crop.NumberOfSplitRegions > 1)
                        throw new InvalidOperationException("Overall labels currently require a rectangular, unsplit plan crop (or crop disabled).");
            var visible = new HashSet<int>(new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation).WhereElementIsNotElementType()
                .Select(e => e.Id.IntegerValue));
            var bases = SimpleProjectScanService.LoadFast(doc).Select(x => doc.GetElement(new ElementId(x.FoundationId)))
                .Where(e => e != null && visible.Contains(e.Id.IntegerValue) && !e.IsHidden(view)).ToList();
            var ports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .Select(p => new { View = doc.GetElement(p.ViewId) as View, Sheet = doc.GetElement(p.SheetId) as ViewSheet })
                .Where(x => x.View != null && x.Sheet != null)
                .GroupBy(x => OverallPlanLabelPolicy.FoundationId(x.View.Name)).ToDictionary(g => g.Key, g => g.ToList());
            var schema = Storage();
            var notes = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>()
                .Where(n => n.OwnerViewId == view.Id && n.GetEntity(schema).IsValid()).ToList();
            var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (textType == ElementId.InvalidElementId) throw new InvalidOperationException("Load a text note type first.");
            int created = 0, updated = 0, stale = 0, failed = 0;
            var processed = new HashSet<string>();
            using (var tx = new Transaction(doc, "Manholes - Overall plan labels"))
            {
                tx.Start(); TransactionFailureHandling.Configure(tx, log);
                foreach (var foundation in bases)
                {
                    var box = foundation.get_BoundingBox(null);
                    if (box == null) { failed++; continue; }
                    var anchor = (box.Min + box.Max) * .5;
                    anchor -= view.ViewDirection * ((anchor - view.Origin).DotProduct(view.ViewDirection));
                    // View collectors may include elements outside the crop. Label only
                    // bases whose centers fall inside its rectangular bounds.
                    if (view.CropBoxActive)
                    {
                        var crop = view.CropBox;
                        var local = crop.Transform.Inverse.OfPoint(anchor);
                        if (local.X < crop.Min.X || local.X > crop.Max.X || local.Y < crop.Min.Y || local.Y > crop.Max.Y) continue;
                    }
                    processed.Add(foundation.UniqueId);
                    using (var sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            string name = ManholeIdentityStore.Read(foundation);
                            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Assign a unique Internal MH ID first.");
                            var matches = notes.Where(n => n.GetEntity(schema).Get<string>(schema.GetField("Foundation")) == foundation.UniqueId).ToList();
                            if (matches.Count > 1) throw new InvalidOperationException("Duplicate tool labels in this view; remove the extra label manually.");
                            var placements = ports.ContainsKey(foundation.Id.IntegerValue) ? ports[foundation.Id.IntegerValue] : null;
                            string text = OverallPlanLabelPolicy.Label(name,
                                placements?.Select(p => p.Sheet.SheetNumber) ?? Enumerable.Empty<string>(),
                                placements?.Select(p => p.View.Name).Distinct().Count() ?? 0);
                            TextNote note = matches.SingleOrDefault();
                            bool isNew = note == null;
                            if (isNew)
                            {
                                var offset = (view.RightDirection + view.UpDirection) * UnitUtil.MmToFt(4 * view.Scale);
                                note = TextNote.Create(doc, view.Id, anchor + offset, text, new TextNoteOptions(textType));
                                note.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L).End = anchor;
                            }
                            else
                            {
                                var previous = Parse(note.GetEntity(schema).Get<string>(schema.GetField("Anchor")));
                                if (previous != null && (anchor - previous).GetLength() > 1e-7)
                                    ElementTransformUtils.MoveElement(doc, note.Id, anchor - previous);
                                if (note.Text != text) note.Text = text;
                                foreach (var leader in note.GetLeaders()) leader.End = anchor;
                            }
                            var entity = new Entity(schema);
                            entity.Set(schema.GetField("Foundation"), foundation.UniqueId);
                            entity.Set(schema.GetField("Anchor"), string.Join(";", new[] { anchor.X, anchor.Y, anchor.Z }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
                            note.SetEntity(entity);
                            if (sub.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Label did not commit.");
                            if (isNew) created++; else updated++;
                            log.Info("OVERALL LABEL Foundation=" + foundation.Id + " " + text.Replace("\n", " / "));
                        }
                        catch (Exception ex)
                        {
                            if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                            failed++; log.Warn("OVERALL LABEL Foundation=" + foundation.Id + ": " + ex.Message);
                        }
                    }
                }
                foreach (var note in notes.Where(n => !processed.Contains(n.GetEntity(schema).Get<string>(schema.GetField("Foundation")))))
                {
                    using (var sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            note.Text = note.Text.Split('\n')[0] + "\nNOT IN CURRENT VIEW / CHECK";
                            sub.Commit(); stale++;
                        }
                        catch (Exception ex) { if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack(); failed++; log.Warn("STALE LABEL " + note.Id + ": " + ex.Message); }
                    }
                }
                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Overall labels were not committed.");
            }
            return "Created: " + created + "; refreshed: " + updated + "; stale labels flagged: " + stale + "; failed: " + failed +
                ".\nLocations read from actual viewports. Adjust text positions for legibility. Run again after moving views between sheets.\nThese are managed text labels, not live family tags.\nLog: " + log.LogPath;
        }

        private static XYZ Parse(string value)
        {
            var parts = (value ?? "").Split(';');
            double x, y, z;
            return parts.Length == 3 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z) ? new XYZ(x, y, z) : null;
        }

        internal static string RenameSheets(Document doc, DiagnosticLogger log)
        {
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => s.SheetNumber.StartsWith("MH-BATCH-", StringComparison.OrdinalIgnoreCase) &&
                    OverallPlanLabelPolicy.SheetName(s.Name) != s.Name).ToList();
            int changed = 0, failed = 0;
            foreach (var sheet in sheets)
            {
                using (var tx = new Transaction(doc, "Manholes - Cast in site sheet name"))
                {
                    try
                    {
                        tx.Start(); TransactionFailureHandling.Configure(tx, log);
                        sheet.Name = OverallPlanLabelPolicy.SheetName(sheet.Name);
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Rename did not commit.");
                        changed++; log.Info("BATCH NAME " + sheet.SheetNumber + " / " + sheet.Name);
                    }
                    catch (Exception ex) { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); failed++; log.Warn("BATCH NAME " + sheet.SheetNumber + ": " + ex.Message); }
                }
            }
            return "Renamed: " + changed + "; failed: " + failed + ". Sheet numbers and viewport locations preserved.";
        }
    }
}
