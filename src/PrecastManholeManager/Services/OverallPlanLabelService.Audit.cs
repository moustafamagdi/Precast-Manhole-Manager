using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static partial class OverallPlanLabelService
    {
        // No transaction, regeneration, review-register writes or model changes.
        internal static string Audit(Document doc, View view, DiagnosticLogger log)
        {
            if (!(view is ViewPlan) || view.IsTemplate)
                throw new InvalidOperationException("Open the overall plan to check coverage.");
            var items = SimpleProjectScanService.LoadFast(doc);
            var visible = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_TextNotes }))
                .WhereElementIsNotElementType().ToElementIds());
            var schema = Schema.Lookup(SchemaId);
            var notes = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>()
                .Where(n => n.OwnerViewId == view.Id && schema != null && n.GetEntity(schema).IsValid()).ToList();
            var byOwner = notes.GroupBy(n => n.GetEntity(schema).Get<string>(schema.GetField("Foundation")))
                .ToDictionary(g => g.Key, g => g.ToList());
            var placements = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .Select(p => new { View = doc.GetElement(p.ViewId) as View, Sheet = doc.GetElement(p.SheetId) as ViewSheet })
                .Where(p => p.View != null && p.Sheet != null)
                .GroupBy(p => OverallPlanLabelPolicy.FoundationId(p.View.Name)).ToDictionary(g => g.Key, g => g.ToList());
            var duplicateNames = new HashSet<string>(items.GroupBy(i => i.ManholeName)
                .Where(g => g.Count() > 1).Select(g => g.Key));
            bool customCrop = false;
            if (view.CropBoxActive)
                using (var crop = view.GetCropRegionShapeManager()) customCrop = crop.ShapeSet || crop.NumberOfSplitRegions > 1;
            bool temporary = view.IsTemporaryHideIsolateActive();
            var csv = new StringBuilder("Manhole,FoundationId,Plan,LabelIds,Status,Issues\r\n");
            int passed = 0;
            var failures = new List<string>();
            foreach (var item in items)
            {
                var issues = new List<string>();
                var own = byOwner.ContainsKey(item.UniqueId) ? byOwner[item.UniqueId] : new List<TextNote>();
                try
                {
                    var foundation = doc.GetElement(item.UniqueId);
                    string name = foundation == null ? null : ManholeIdentityStore.Read(foundation);
                    if (!OverallPlanLabelPolicy.IsManholeName(name)) issues.Add("INVALID_MH_ID");
                    if (duplicateNames.Contains(item.ManholeName)) issues.Add("DUPLICATE_MH_ID");
                    if (temporary) issues.Add("TEMPORARY_VISIBILITY_UNVERIFIED");
                    if (customCrop) issues.Add("CUSTOM_CROP_UNVERIFIED");
                    if (foundation == null || !visible.Contains(foundation.Id) || foundation.IsHidden(view) ||
                        view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_StructuralFoundation)))
                        issues.Add("BASE_NOT_VISIBLE: check view range, filters, worksets, phase and visibility");
                    var box = foundation?.get_BoundingBox(null);
                    if (box == null) issues.Add("BASE_BOUNDS_UNVERIFIED");
                    else if (view.CropBoxActive && !customCrop && !BoundsInside(view, box, false)) issues.Add("BASE_OUTSIDE_OR_CLIPPED_BY_CROP");
                    if (own.Count == 0) issues.Add("LABEL_MISSING");
                    else if (own.Count != 1) issues.Add("LABEL_DUPLICATE");
                    foreach (var note in own)
                    {
                        if (!visible.Contains(note.Id) || note.IsHidden(view) || view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_TextNotes)))
                            issues.Add("LABEL_NOT_VISIBLE: " + note.Id);
                        var bounds = note.get_BoundingBox(view);
                        if (bounds == null) issues.Add("LABEL_BOUNDS_UNVERIFIED: " + note.Id);
                        else if (view.CropBoxActive && view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.AsInteger() == 1 &&
                            !customCrop && !BoundsInside(view, bounds, true)) issues.Add("LABEL_ANNOTATION_CLIPPED: " + note.Id);
                        if (OverallPlanLabelPolicy.IsManholeName(name))
                        {
                            var actual = placements.ContainsKey(item.FoundationId) ? placements[item.FoundationId] : null;
                            string expected = OverallPlanLabelPolicy.Label(name, actual?.Select(p => p.Sheet.SheetNumber) ?? Enumerable.Empty<string>(),
                                actual?.Select(p => p.View.Name).Distinct().Count() ?? 0);
                            if (Normalize(note.Text) != Normalize(expected)) issues.Add("LABEL_TEXT_OUTDATED: " + note.Id);
                        }
                        if (box != null)
                        {
                            var center = (box.Min + box.Max) * .5;
                            center -= view.ViewDirection * ((center - view.Origin).DotProduct(view.ViewDirection));
                            var anchor = Parse(note.GetEntity(schema).Get<string>(schema.GetField("Anchor")));
                            if (anchor == null || (anchor - center).GetLength() > UnitUtil.MmToFt(2)) issues.Add("LABEL_ANCHOR_OUTDATED: " + note.Id);
                        }
                    }
                }
                catch (Exception ex) { issues.Add("CHECK_ERROR: " + ex.Message); }
                string detail = string.Join("; ", issues.Distinct());
                if (issues.Count == 0) passed++;
                else failures.Add(item.ManholeName + " [" + item.FoundationId + "]: " + detail);
                csv.AppendLine(string.Join(",", new[] { item.ManholeName, item.FoundationId.ToString(), view.Name,
                    string.Join(";", own.Select(n => n.Id.ToString())), issues.Count == 0 ? "CHECKS PASSED" : "REVIEW", detail }.Select(Csv)));
            }
            var ids = new HashSet<string>(items.Select(i => i.UniqueId));
            var orphans = notes.Where(n => !ids.Contains(n.GetEntity(schema).Get<string>(schema.GetField("Foundation")))).ToList();
            foreach (var note in orphans)
                csv.AppendLine(string.Join(",", new[] { "", "", view.Name, note.Id.ToString(), "REVIEW", "ORPHAN_LABEL: not a currently recognized manhole" }.Select(Csv)));
            string path = Path.ChangeExtension(log.LogPath, ".overall-coverage.csv");
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            string summary = "Overall plan coverage: " + view.Name + "\nRecognized manholes: " + items.Count +
                "; automated checks passed: " + passed + "; need review: " + (items.Count - passed) + "; orphan labels: " + orphans.Count +
                "\n" + (items.Count == 0 ? "NO MANHOLES FOUND - coverage not verified." :
                    passed == items.Count && orphans.Count == 0 ? "All recognized manholes passed automated plan/label coverage checks." : "Coverage is incomplete or unverified; see CSV for every affected ID.") +
                "\nVisual check still required for occlusion and overlapping text. No view settings changed.\nCSV: " + path;
            log.Info(summary);
            foreach (var failure in failures) log.Warn("OVERALL COVERAGE " + failure);
            return summary + (failures.Count == 0 ? "" : "\n\n" + string.Join("\n", failures.Take(5)) + (failures.Count > 5 ? "\nMore in CSV..." : ""));
        }

        private static string Normalize(string value) => (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        private static bool BoundsInside(View view, BoundingBoxXYZ bounds, bool annotation)
        {
            var crop = view.CropBox;
            double left = 0, right = 0, bottom = 0, top = 0;
            if (annotation)
                using (var manager = view.GetCropRegionShapeManager())
                {
                    left = manager.LeftAnnotationCropOffset * view.Scale; right = manager.RightAnnotationCropOffset * view.Scale;
                    bottom = manager.BottomAnnotationCropOffset * view.Scale; top = manager.TopAnnotationCropOffset * view.Scale;
                }
            double tolerance = UnitUtil.MmToFt(2);
            return DrawingAuditService.Corners(bounds).Select(p => crop.Transform.Inverse.OfPoint(p))
                .All(p => p.X >= crop.Min.X - left - tolerance && p.X <= crop.Max.X + right + tolerance &&
                    p.Y >= crop.Min.Y - bottom - tolerance && p.Y <= crop.Max.Y + top + tolerance);
        }
    }
}
