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
        public bool NewNumber { get; set; }
    }

    internal sealed class ManholeNumberingPlan
    {
        public List<ManholeNumberingRow> Rows { get; } =
            new List<ManholeNumberingRow>();
        public List<string> Errors { get; } = new List<string>();
        public int NewlyNumbered => Rows.Count(r => r.NewNumber);
        public int ExistingPreserved => Rows.Count(r => !r.NewNumber);
        public string CsvPath { get; set; }
    }

    // Internal tool numbering, independent from ALL consultant/project
    // identity fields. Only the dedicated foundation ExtensibleStorage
    // is ever written: native Revit Mark, saved fabrication/carrier name,
    // and project naming parameters remain completely untouched.
    internal static class ManholeNumberingService
    {
        private static readonly HashSet<string> FoundationTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HTC_ST_PRECAST_FN_200mm_MH",
                "HTC_ST_PRECAST_FN_300mm_MH"
            };

        private static List<Element> GetFoundations(Document doc)
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

            foreach (Element foundation in GetFoundations(doc))
            {
                // An existing INTERNAL number is always preserved.
                // Revit Mark and other naming schemes are reference only.
                string internalName =
                    (ManholeIdentityStore.Read(foundation) ?? "").Trim();
                // Legacy storage was copied with the base. Keep the earliest
                // foundation's number and assign fresh numbers to later duplicates.

                var mark = foundation.get_Parameter(
                    BuiltInParameter.ALL_MODEL_MARK);
                plan.Rows.Add(new ManholeNumberingRow
                {
                    FoundationId = foundation.Id.IntegerValue,
                    FoundationUniqueId = foundation.UniqueId,
                    OldMark = (mark?.AsString() ?? "").Trim(),
                    PreviousName = internalName,
                    ProposedName = internalName,
                    NewNumber = internalName.Length == 0
                });
            }

            AllocateNames(plan);
            return plan;
        }

        internal static void AllocateNames(ManholeNumberingPlan plan)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in plan.Rows.OrderBy(r => r.FoundationId))
            {
                row.NewNumber = string.IsNullOrWhiteSpace(row.PreviousName) || !used.Add(row.PreviousName);
                row.ProposedName = row.NewNumber ? "" : row.PreviousName;
            }
            // ElementId is ONLY an initial sorting key. Once assigned,
            // numbers persist against the foundation's stable UniqueId.
            int next = 1;
            foreach (ManholeNumberingRow row in plan.Rows
                .Where(x => x.NewNumber).OrderBy(x => x.FoundationId))
            {
                string candidate;
                do
                {
                    candidate = "MH-" +
                        next.ToString("D3",
                            CultureInfo.InvariantCulture);
                    next++;
                } while (!used.Add(candidate));
                row.ProposedName = candidate;
            }
        }

        public static string ExportPreview(ManholeNumberingPlan plan)
        {
            string path = Path.Combine(
                OutputPathService.GetLogsFolder(),
                "Internal_Manhole_Numbers_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff",
                        CultureInfo.InvariantCulture) + ".csv");
            var sb = new StringBuilder();
            sb.AppendLine("FoundationId,FoundationUniqueId," +
                "InternalBefore,InternalAfter,Action,ProjectMark_REFERENCE_ONLY");
            foreach (ManholeNumberingRow row in plan.Rows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(row.FoundationId.ToString()),
                    Csv(row.FoundationUniqueId),
                    Csv(row.PreviousName),
                    Csv(row.ProposedName),
                    Csv(row.NewNumber ? "ASSIGN" : "KEEP"),
                    Csv(row.OldMark)
                }));
            }
            if (plan.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("INTERNAL NUMBERING CONFLICTS");
                foreach (string error in plan.Errors)
                    sb.AppendLine(Csv(error));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            plan.CsvPath = path;
            return path;
        }

        public static void Apply(Document doc,
            ManholeNumberingPlan approved, DiagnosticLogger log)
        {
            if (doc.IsReadOnly || doc.IsLinked)
                throw new InvalidOperationException(
                    "An editable host RVT is required.");
            if (approved.Errors.Count != 0)
                throw new InvalidOperationException(
                    "Fix duplicate internal manhole numbers first.");
            ManholeNumberingPlan current = Preview(doc);
            if (current.Errors.Count != 0 ||
                current.Rows.Count != approved.Rows.Count)
                throw new InvalidOperationException(
                    "Model changed after preview. Re-run numbering.");
            for (int i = 0; i < approved.Rows.Count; i++)
            {
                ManholeNumberingRow a = approved.Rows[i];
                ManholeNumberingRow b = current.Rows[i];
                if (a.FoundationUniqueId != b.FoundationUniqueId ||
                    a.ProposedName != b.ProposedName ||
                    a.OldMark != b.OldMark)
                    throw new InvalidOperationException(
                        "Names or foundations changed after preview.");
            }

            using (var transaction = new Transaction(doc,
                "HATCO - Save Internal Manhole Numbers"))
            {
                transaction.Start();
                var failureOptions = transaction.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(new OpeningFailurePreprocessor(log));
                failureOptions.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(failureOptions);
                try
                {
                    foreach (ManholeNumberingRow row in approved.Rows)
                    {
                        Element foundation = doc.GetElement(
                            new ElementId(row.FoundationId));
                        if (foundation == null ||
                            foundation.UniqueId != row.FoundationUniqueId)
                            throw new InvalidOperationException(
                                "Foundation no longer matches numbering " +
                                row.FoundationId);
                        if (!row.NewNumber)
                        {
                            ManholeIdentityStore.Write(foundation, row.ProposedName);
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(row.PreviousName) || ManholeIdentityStore.HasForeignOwner(foundation))
                        {
                            BatchSheetLayoutService.ForgetCopiedReservation(foundation);
                            log.Warn("COPIED BASE REIDENTIFIED Foundation=" + row.FoundationId + " Previous=" + row.PreviousName + " New=" + row.ProposedName);
                        }

                        // Capture the previous auto-generated view title
                        // source before persisting our new internal name.
                        string previousAutoName =
                            ManholeViewTitleService.Name(
                                doc, foundation, log);
                        ManholeIdentityStore.Write(
                            foundation, row.ProposedName);
                        ManholeViewTitleService.RefreshGeneratedTitles(
                            doc, foundation, row.ProposedName,
                            previousAutoName, log);
                        log.Info("INTERNAL MH NUMBER Foundation=" +
                            row.FoundationId + " UniqueId=" +
                            row.FoundationUniqueId +
                            " Name=" + row.ProposedName +
                            " ProjectMarkUnchanged=" + row.OldMark);
                    }
                    if (transaction.Commit() !=
                        TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Could not commit internal manhole numbers.");
                }
                catch
                {
                    if (transaction.GetStatus() ==
                        TransactionStatus.Started)
                        transaction.RollBack();
                    throw;
                }
            }
            log.Info("INTERNAL MH NUMBERING COMMITTED: " +
                approved.Rows.Count + " total, " +
                approved.NewlyNumbered + " assigned, " +
                approved.ExistingPreserved + " unchanged. " +
                "No native Mark or project number was modified.");
        }

        private static string Csv(string text)
        {
            return "\"" + (text ?? "").Replace("\"", "\"\"")
                .Replace("\r", " ").Replace("\n", " ") + "\"";
        }
    }
}
