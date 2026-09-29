using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class BatchManholeResult
    {
        public int Selected { get; set; }
        public int Valid { get; set; }
        public int NeedsReview { get; set; }
        public int Failed { get; set; }
        public int Penetrations { get; set; }
        public int ManualSufficient { get; set; }
        public int ManualTooSmall { get; set; }
        public int OpeningsCreated { get; set; }
        public int OpeningsUpdated { get; set; }
        public int OpeningsUnchanged { get; set; }
        public int OpeningsRemoved { get; set; }
        public int OpeningReviews { get; set; }
        public int CarriersSaved { get; set; }
        public int OpeningsLinked { get; set; }
        public string LogPath { get; set; }

        public override string ToString()
        {
            return
                "Foundations: " + Selected +
                " | Valid: " + Valid +
                " | Review: " + NeedsReview +
                " | Failed: " + Failed +
                "\nPenetrations: " + Penetrations +
                " | Manual OK: " + ManualSufficient +
                " | Manual Too Small: " + ManualTooSmall +
                "\nOpenings Created: " + OpeningsCreated +
                " | Updated: " + OpeningsUpdated +
                " | Unchanged: " + OpeningsUnchanged +
                " | Removed: " + OpeningsRemoved +
                " | Opening Review: " + OpeningReviews +
                "\nCarriers Saved: " + CarriersSaved +
                " | Openings Linked: " + OpeningsLinked;
        }
    }

    internal static class BatchManholeProcessor
    {
        public static BatchManholeResult Process(
            Document doc,
            IList<Element> foundations,
            DiagnosticLogger log)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var result = new BatchManholeResult
            {
                Selected = foundations?.Count ?? 0,
                LogPath = log?.LogPath
            };

            int totalLinks;
            int loadedLinks;
            int loadedMepLinks;
            EvaluateLinkAvailability(doc, out totalLinks, out loadedLinks, out loadedMepLinks);

            log?.Info(
                "Batch link preflight: TotalLinks=" + totalLinks +
                " LoadedLinks=" + loadedLinks +
                " LoadedMEPLinks=" + loadedMepLinks);

            if (loadedMepLinks == 0)
            {
                throw new InvalidOperationException(
                    "Batch processing stopped: no loaded Revit link containing Pipes, Ducts, Cable Trays, or Conduits is available. " +
                    "Load the required MEP links before running Batch Selected / Batch All.");
            }

            List<ManholeDataRecord> existingManholes = ManholeDataCarrierService.ReadAll(doc);
            HashSet<string> usedNumbers = new HashSet<string>(
                existingManholes
                    .Select(x => x.ManholeNumber)
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            int nextNumber = ResolveNextNumber(usedNumbers);

            log?.WriteHeader("BATCH MANHOLE PROCESSING");
            log?.Info("Foundations queued: " + result.Selected);

            foreach (Element foundation in foundations ?? new List<Element>())
            {
                log?.WriteHeader("BATCH FOUNDATION " + foundation.Id.IntegerValue);

                try
                {
                    var detector = new ManholeDetectionService(doc, log);
                    ManholeDetectionResult manhole = detector.Detect(foundation);

                    if (!manhole.IsValid || !string.IsNullOrWhiteSpace(manhole.Warning))
                    {
                        result.NeedsReview++;
                        log?.Warn(
                            "Foundation " + foundation.Id.IntegerValue +
                            " skipped. Valid=" + manhole.IsValid +
                            " Warning='" + (manhole.Warning ?? string.Empty) + "'.");
                        continue;
                    }

                    var scanner = new MepPenetrationScanner(doc, log);
                    List<PenetrationRecord> penetrations = scanner.Scan(manhole);

                    ExistingOpeningDetectionService.Apply(
                        doc,
                        manhole.Walls.Select(w => w.Wall.Id.IntegerValue),
                        penetrations,
                        log);

                    result.Penetrations += penetrations.Count;
                    result.ManualSufficient += penetrations.Count(x =>
                        x.ExistingOpeningStatus == "EXISTING SUFFICIENT");
                    result.ManualTooSmall += penetrations.Count(x =>
                        x.ExistingOpeningStatus == "EXISTING TOO SMALL");

                    ManholeDataRecord existing = ManholeDataCarrierService.ReadForFoundation(
                        doc,
                        foundation.UniqueId,
                        foundation.Id.IntegerValue);

                    string manholeNumber = existing?.ManholeNumber;
                    if (string.IsNullOrWhiteSpace(manholeNumber))
                    {
                        manholeNumber = NextAvailableNumber(usedNumbers, ref nextNumber);
                        usedNumbers.Add(manholeNumber);
                    }

                    ManholeDataRecord data = BuildDataRecord(foundation, manhole, manholeNumber);

                    using (var tx = new Transaction(doc, "HATCO - Batch Precast Manhole " + manholeNumber))
                    {
                        var failurePreprocessor = new OpeningFailurePreprocessor(log);
                        FailureHandlingOptions failureOptions = tx.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(failurePreprocessor);
                        failureOptions.SetClearAfterRollback(true);
                        tx.SetFailureHandlingOptions(failureOptions);

                        tx.Start();

                        BatchOpeningSyncResult sync = BatchOpeningSyncService.Sync(
                            doc,
                            manhole.Walls.Select(w => w.Wall.Id.IntegerValue).ToList(),
                            penetrations,
                            log);

                        DirectShape carrier = ManholeDataCarrierService.CreateOrUpdate(doc, data);

                        int linked = OpeningManholeLinkService.LinkManagedOpenings(
                            doc,
                            data.ManholeNumber,
                            data.FoundationId,
                            new[] { data.Wall1Id, data.Wall2Id, data.Wall3Id, data.Wall4Id });

                        TransactionStatus commitStatus = tx.Commit();

                        if (commitStatus != TransactionStatus.Committed)
                        {
                            result.NeedsReview++;
                            result.OpeningReviews += Math.Max(1, failurePreprocessor.UnresolvedTargetCount);
                            log?.Warn(
                                "BATCH ROLLBACK " + manholeNumber +
                                " Foundation=" + foundation.Id.IntegerValue +
                                " due to unresolved Revit opening/join failure.");
                            continue;
                        }

                        result.Valid++;
                        result.OpeningReviews += failurePreprocessor.ResolvedCount;
                        result.OpeningsCreated += sync.Created;
                        result.OpeningsUpdated += sync.Updated;
                        result.OpeningsUnchanged += sync.Unchanged;
                        result.OpeningsRemoved += sync.Removed;
                        result.OpeningReviews += sync.Review;
                        result.Failed += sync.Failed;
                        result.CarriersSaved++;
                        result.OpeningsLinked += linked;

                        log?.Info(
                            "BATCH OK " + manholeNumber +
                            " Foundation=" + foundation.Id.IntegerValue +
                            " Carrier=" + carrier.Id.IntegerValue +
                            " Penetrations=" + penetrations.Count +
                            " Linked=" + linked +
                            " Created=" + sync.Created +
                            " Updated=" + sync.Updated +
                            " Unchanged=" + sync.Unchanged +
                            " Review=" + sync.Review +
                            " Failed=" + sync.Failed);
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    log?.Error("Batch failed for Foundation " + foundation.Id.IntegerValue, ex);
                }
            }

            log?.WriteHeader("BATCH SUMMARY");
            log?.Info(result.ToString());

            return result;
        }

        private static void EvaluateLinkAvailability(
            Document doc,
            out int totalLinks,
            out int loadedLinks,
            out int loadedMepLinks)
        {
            List<RevitLinkInstance> links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            totalLinks = links.Count;
            loadedLinks = 0;
            loadedMepLinks = 0;

            BuiltInCategory[] categories =
            {
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_Conduit
            };

            foreach (RevitLinkInstance link in links)
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null)
                    continue;

                loadedLinks++;

                var filter = new ElementMulticategoryFilter(categories);
                bool hasMep = new FilteredElementCollector(linkDoc)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .Take(1)
                    .Any();

                if (hasMep)
                    loadedMepLinks++;
            }
        }

        private static ManholeDataRecord BuildDataRecord(
            Element foundation,
            ManholeDetectionResult manhole,
            string number)
        {
            return new ManholeDataRecord
            {
                ManholeNumber = number,
                FoundationId = foundation.Id.IntegerValue,
                FoundationUniqueId = foundation.UniqueId,
                Wall1Id = manhole.Walls.First(w => w.Number == 1).Wall.Id.IntegerValue,
                Wall2Id = manhole.Walls.First(w => w.Number == 2).Wall.Id.IntegerValue,
                Wall3Id = manhole.Walls.First(w => w.Number == 3).Wall.Id.IntegerValue,
                Wall4Id = manhole.Walls.First(w => w.Number == 4).Wall.Id.IntegerValue,
                CenterXmm = UnitUtil.FtToMm(manhole.Center.X),
                CenterYmm = UnitUtil.FtToMm(manhole.Center.Y),
                BaseTopZmm = UnitUtil.FtToMm(manhole.FoundationTopZ),
                BaseThicknessMm = UnitUtil.FtToMm(manhole.FoundationThicknessFt),
                ClearW1W4Mm = UnitUtil.FtToMm(manhole.ClearW1W4Ft),
                ClearW2W3Mm = UnitUtil.FtToMm(manhole.ClearW2W3Ft),
                OuterW1W4Mm = UnitUtil.FtToMm(manhole.OuterW1W4Ft),
                OuterW2W3Mm = UnitUtil.FtToMm(manhole.OuterW2W3Ft),
                WallHeightMm = UnitUtil.FtToMm(manhole.WallHeightFt)
            };
        }

        private static int ResolveNextNumber(IEnumerable<string> numbers)
        {
            int max = 0;

            foreach (string value in numbers ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                string digits = new string(value.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
                int parsed;
                if (int.TryParse(digits, out parsed))
                    max = Math.Max(max, parsed);
            }

            return max + 1;
        }

        private static string NextAvailableNumber(HashSet<string> used, ref int next)
        {
            while (true)
            {
                string candidate = "MH-" + next.ToString("000");
                next++;

                if (!used.Contains(candidate))
                    return candidate;
            }
        }
    }
}
