using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class ManholeNumberingRow
    {
        public int FoundationId { get; set; }
        public string FoundationUniqueId { get; set; }
        public string OldMark { get; set; }
        public string PreviousName { get; set; }
        public string ProposedName { get; set; }
        public string Source { get; set; }
        public bool NewNumber { get; set; }
        public bool SyncMark { get; set; }
    }

    internal sealed class ManholeNumberingPlan
    {
        public List<ManholeNumberingRow> Rows { get; } =
            new List<ManholeNumberingRow>();
        public List<string> Errors { get; } = new List<string>();
        public int NewlyNumbered => Rows.Count(r => r.NewNumber);
        public int ExistingPreserved => Rows.Count(r => !r.NewNumber);
        public int MarkWrites => Rows.Count(r => r.SyncMark);
        public string CsvPath { get; set; }
    }

    // All recognized manholes, including geometrically isolated cases.
    // No geometry scans are required to number them.
    internal static class ManholeNumberingService
    {
        private static readonly HashSet<string> FoundationTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HTC_ST_PRECAST_FN_200mm_MH",
                "HTC_ST_PRECAST_FN_300mm_MH"
            };

        private static string GetString(Parameter parameter)
        {
            if (parameter == null) return "";
            return (parameter.AsString() ?? "").Trim();
        }

        private static string GetProjectNumber(Element foundation)
        {
            foreach (string name in new[]
                { "Manhole Number", "MH Number", "Manhole No" })
            {
                string value = GetString(foundation.LookupParameter(name));
                if (value.Length > 0) return value;
            }
            return "";
        }

        private static List<Element> Foundations(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .WhereElementIsNotElementType().ToElements()
                .Where(e =>
                {
                    Element type = doc.GetElement(e.GetTypeId());
                    return type != null && FoundationTypes.Contains(type.Name);
                }).OrderBy(e => e.Id.IntegerValue).ToList();
        }

        public static ManholeNumberingPlan Preview(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            var plan = new ManholeNumberingPlan();
            List<Element> foundations = Foundations(doc);

            // Read all saved carriers ONCE (not an O(n^2) search for
            // each manhole in a large RVT).
            var carriers = ManholeDataCarrierService.ReadAll(doc)
                .Where(x => !string.IsNullOrWhiteSpace(x.ManholeNumber))
                .GroupBy(x => x.FoundationUniqueId ?? "", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(),
                    StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Element f in foundations)
            {
                var saved = carriers.ContainsKey(f.UniqueId) ?
                    carriers[f.UniqueId] : null;
                string carrierName = saved == null || saved.Count == 0 ?
                    "" : (saved[0].ManholeNumber ?? "").Trim();
                string storedName = (ManholeIdentityStore.Read(f) ?? "").Trim();
                string mark = GetString(f.get_Parameter(
                    BuiltInParameter.ALL_MODEL_MARK));
                string project = GetProjectNumber(f);

                // Conflicts are never silently resolved. A mismatched
                // consultant Mark must not be overwritten by our cache.
                string[] filled = { carrierName, storedName, mark, project };
                var distinct = filled.Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (saved != null &&
                    saved.Select(x => (x.ManholeNumber ?? "").Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    plan.Errors.Add("Foundation " + f.Id.IntegerValue +
                        " has conflicting saved carrier designations.");
                if (distinct.Count > 1)
                    plan.Errors.Add("Foundation " + f.Id.IntegerValue +
                        " has conflicting names: " +
                        string.Join(" vs ", distinct));
                string existing = distinct.FirstOrDefault() ?? "";
                string source = carrierName.Length > 0 ? "SAVED DATA" :
                    storedName.Length > 0 ? "SAVED FOUNDATION" :
                    mark.Length > 0 ? "REVIT MARK" :
                    project.Length > 0 ? "PROJECT PARAMETER" : "GENERATED";

                if (existing.Length > 0 && !used.Add(existing))
                    plan.Errors.Add("Duplicate existing designation " +
                        existing + " (foundation " + f.Id.IntegerValue + ")");
                plan.Rows.Add(new ManholeNumberingRow
                {
                    FoundationId = f.Id.IntegerValue,
                    FoundationUniqueId = f.UniqueId,
                    OldMark = mark,
                    PreviousName = existing,
                    ProposedName = existing,
                    Source = source
                });
            }

            // Reserve existing Mark values from all OTHER foundations as
            // well, so generated MH-### numbers cannot collide in model
            // schedules with another foundation category/type.
            foreach (Element f in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .WhereElementIsNotElementType())
            {
                string mark = GetString(f.get_Parameter(
                    BuiltInParameter.ALL_MODEL_MARK));
                if (mark.Length > 0) used.Add(mark);
            }

            // Stable ordering by current ElementId is used only for the
            // FIRST assignment. Identity is thereafter persisted using
            // UniqueId + foundation ExtensibleStorage + Revit Mark.
            int sequence = 1;
            foreach (ManholeNumberingRow row in plan.Rows
                .Where(x => string.IsNullOrWhiteSpace(x.ProposedName)))
            {
                string next;
                do
                {
                    next = "MH-" + sequence.ToString("D3",
                        CultureInfo.InvariantCulture);
                    sequence++;
                } while (used.Contains(next));
                used.Add(next);
                row.ProposedName = next;
                row.NewNumber = true;
            }
            foreach (var row in plan.Rows)
            {
                row.SyncMark = !string.Equals(row.OldMark,
                    row.ProposedName, StringComparison.Ordinal);
                Element element = doc.GetElement(
                    new ElementId(row.FoundationId));
                Parameter mark = element?.get_Parameter(
                    BuiltInParameter.ALL_MODEL_MARK);
                if (row.SyncMark && (mark == null || mark.IsReadOnly))
                    plan.Errors.Add("Foundation " + row.FoundationId +
                        " cannot write native Revit Mark; check " +
                        "instance parameter and editing permissions.");
            }
            return plan;
        }

        public static string ExportPreview(ManholeNumberingPlan plan)
        {
            string dir = OutputPathService.GetLogsFolder();
            string path = Path.Combine(dir, "Manhole_Numbering_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff",
                    CultureInfo.InvariantCulture) + ".csv");
            var sb = new StringBuilder();
            sb.AppendLine("FoundationId,FoundationUniqueId,ExistingName," +
                "ProposedName,Source,Action,OldMark,MarkUpdate");
            foreach (var r in plan.Rows)
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(r.FoundationId.ToString()),
                    Csv(r.FoundationUniqueId),
                    Csv(r.PreviousName),
                    Csv(r.ProposedName),
                    Csv(r.Source),
                    Csv(r.NewNumber ? "GENERATE" : "PRESERVE"),
                    Csv(r.OldMark),
                    Csv(r.SyncMark ? "YES" : "NO")
                }));
            if (plan.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("PREFLIGHT ERRORS");
                foreach (string problem in plan.Errors)
                    sb.AppendLine(Csv(problem));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            plan.CsvPath = path;
            return path;
        }

        public static void Apply(Document doc, ManholeNumberingPlan approved,
            DiagnosticLogger log)
        {
            if (doc.IsReadOnly || doc.IsLinked)
                throw new InvalidOperationException(
                    "An editable host RVT is required.");
            if (approved.Errors.Count > 0)
                throw new InvalidOperationException(
                    "Fix the numbering conflicts shown in preview CSV.");
            // Detect changes since operator preview; never apply a stale
            // name map to new/deleted/reassigned manholes.
            ManholeNumberingPlan current = Preview(doc);
            if (current.Errors.Count != 0 ||
                current.Rows.Count != approved.Rows.Count)
                throw new InvalidOperationException(
                    "Model changed since numbering preview. Re-run.");
            for (int i = 0; i < current.Rows.Count; i++)
            {
                ManholeNumberingRow a = current.Rows[i];
                ManholeNumberingRow b = approved.Rows[i];
                if (a.FoundationUniqueId != b.FoundationUniqueId ||
                    a.ProposedName != b.ProposedName ||
                    a.OldMark != b.OldMark)
                    throw new InvalidOperationException(
                        "Model numbering changed during preview. Re-run.");
            }

            using (var tx = new Transaction(doc,
                "HATCO - Assign and Persist All Manhole Names"))
            {
                tx.Start();
                try
                {
                    foreach (ManholeNumberingRow row in approved.Rows)
                    {
                        Element element = doc.GetElement(
                            new ElementId(row.FoundationId));
                        if (element == null ||
                            element.UniqueId != row.FoundationUniqueId)
                            throw new InvalidOperationException(
                                "Manhole identity changed: " +
                                row.FoundationId);
                        // The cache and native Mark persist in the RVT.
                        // Never mutate already-approved carrier data.
                        if (!string.Equals(
                            (ManholeIdentityStore.Read(element) ?? "").Trim(),
                            row.ProposedName, StringComparison.Ordinal))
                            ManholeIdentityStore.Write(element,
                                row.ProposedName);
                        if (row.SyncMark)
                        {
                            Parameter mark = element.get_Parameter(
                                BuiltInParameter.ALL_MODEL_MARK);
                            if (mark == null || mark.IsReadOnly)
                                throw new InvalidOperationException(
                                    "Mark cannot be written on " +
                                    row.FoundationId + "; all changes roll back.");
                            mark.Set(row.ProposedName);
                        }
                        log.Info("MH NUMBER Foundation=" +
                            row.FoundationId + " Name=" +
                            row.ProposedName + " Source=" + row.Source +
                            " MarkSync=" + row.SyncMark);
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit rejected the numbering transaction.");
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    throw;
                }
            }
            log.Info("MANHOLE NUMBERING COMMITTED Count=" +
                approved.Rows.Count + " Generated=" +
                approved.NewlyNumbered + " Preserved=" +
                approved.ExistingPreserved);
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"")
                .Replace("\r", " ").Replace("\n", " ") + "\"";
        }
    }
}