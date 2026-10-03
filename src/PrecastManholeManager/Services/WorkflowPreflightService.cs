using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class WorkflowPreflightService
    {
        // Read model requirements together before the first transaction or checkpoint save.
        internal static void Require(Document doc, DiagnosticLogger log, bool drawings, bool dimensions, bool openings, double clearance)
        {
            var errors = new List<string>();
            if (doc.IsReadOnly || doc.IsLinked || doc.IsModifiable) errors.Add("An editable host model with no open transaction is required.");
            if (string.IsNullOrWhiteSpace(doc.PathName)) errors.Add("Save the current RVT before running.");
            if (openings && (double.IsNaN(clearance) || double.IsInfinity(clearance) || clearance < 0)) errors.Add("Enter a finite, non-negative clearance.");
            if (drawings)
            {
                var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
                foreach (string name in new[] { "MH_PLAN", "MH_SEC" })
                    if (!templates.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) errors.Add("Missing template: " + name);
                if (!new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks).Any()) errors.Add("Load the project titleblock.");
                if (!new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Any()) errors.Add("Load a text note type.");
                var defaultType = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.ViewportType));
                var port = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault();
                bool viewport = port != null ? port.GetValidTypes().Any(id => doc.GetElement(id)?.Name == "NO BUBBLE NTS") :
                    defaultType != null && new FilteredElementCollector(doc).WhereElementIsElementType().Any(e => e.GetType() == defaultType.GetType() && e.Name == "NO BUBBLE NTS");
                if (!viewport) errors.Add("Load viewport type NO BUBBLE NTS.");
            }
            if (dimensions && !new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Any(t => t.StyleType == DimensionStyleType.Linear && t.Name.Equals("HTC_DIM_1.8mm", StringComparison.OrdinalIgnoreCase)))
                errors.Add("Load linear dimension type HTC_DIM_1.8mm.");
            if (drawings || dimensions)
                try { ManholeViewTitleService.RequiredSectionType(doc); } catch (InvalidOperationException ex) { errors.Add(ex.Message); }
            try
            {
                ManholeReviewRegistry.Load(doc); // Validate schema/migration before edits.
                string probe = ManholeReviewRegistry.RegisterPath(doc) + ".probe-" + Guid.NewGuid().ToString("N");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            }
            catch (Exception ex) { errors.Add("Review register is unavailable: " + ex.Message); }
            if (openings)
            {
                var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
                var unloaded = links.Where(x => x.GetLinkDocument() == null).Select(x => x.Name).ToList();
                if (unloaded.Count > 0) log.Warn("PREFLIGHT: unavailable links are outside this scan; coverage is not certified for them: " + string.Join(", ", unloaded));
                if (!links.Any(x => x.GetLinkDocument() != null)) errors.Add("No loaded linked model available for the linked pipe/duct scan.");
            }
            if (errors.Count > 0) throw new InvalidOperationException("Preflight failed before model changes:\n- " + string.Join("\n- ", errors));
            log.Info("PREFLIGHT PASSED: drawings=" + drawings + " dimensions=" + dimensions + " openings=" + openings + "; delivery remains unverified.");
        }
    }
}
