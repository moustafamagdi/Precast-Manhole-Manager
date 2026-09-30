using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Services;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ManageManholeReviewsCommand : IExternalCommand
    {
        private static readonly HashSet<string> FoundationTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HTC_ST_PRECAST_FN_200mm_MH",
                "HTC_ST_PRECAST_FN_300mm_MH"
            };

        public Result Execute(ExternalCommandData input,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = input.Application.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            if (doc == null) return Result.Failed;

            using (var log = new DiagnosticLogger())
            {
                try
                {
                    string path = ManholeReviewRegistry.RegisterPath(doc);
                    TaskDialog mode = new TaskDialog("Manhole Review Queue")
                    {
                        MainInstruction = "Isolate problematic manholes",
                        MainContent =
                            "Choose whether to run a fresh lightweight audit " +
                            "of all recognized manholes or open the issues " +
                            "already saved for this project.",
                        CommonButtons = TaskDialogCommonButtons.Cancel
                    };
                    mode.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                        "Scan All & update issue register",
                        "Detect ambiguous footprints, edited profiles, " +
                        "in-place cutters and other pre-existing cuts.");
                    mode.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                        "Open saved issue register",
                        "Select saved problematic manholes and create their 3D views.");
                    TaskDialogResult choice = mode.Show();
                    if (choice == TaskDialogResult.Cancel)
                        return Result.Cancelled;
                    if (choice == TaskDialogResult.CommandLink1)
                        ScanAll(doc, log);

                    List<ManholeReviewIssue> issues =
                        ManholeReviewRegistry.Load(doc);
                    if (issues.Count == 0)
                    {
                        TaskDialog.Show("Manhole Review Queue",
                            "No issues registered for this saved RVT yet.\n" +
                            "Register: " + path + "\nLog: " + log.LogPath);
                        return Result.Succeeded;
                    }

                    var window = new ManholeReviewManagerWindow(issues, path);
                    if (window.ShowDialog() != true || !window.CreateViews)
                        return Result.Succeeded;

                    int created = 0, skipped = 0;
                    View3D firstView = null;
                    foreach (ManholeReviewIssue issue in window.SelectedIssues)
                    {
                        Element foundation = doc.GetElement(
                            new ElementId(issue.FoundationId));
                        if (foundation == null ||
                            foundation.UniqueId != issue.FoundationUniqueId)
                        {
                            skipped++;
                            log.Warn("REVIEW VIEW SKIPPED: foundation missing / " +
                                "UniqueId changed, old ID=" + issue.FoundationId);
                            continue;
                        }

                        using (var tx = new Transaction(doc,
                            "HATCO - Manhole Review 3D " + issue.FoundationId))
                        {
                            try
                            {
                                VirtualFoundationResult footprint =
                                    new VirtualFoundationRecoveryService(doc, log)
                                        .Analyze(foundation);
                                tx.Start();
                                View3D view =
                                    ManholeReviewViewService.CreateOrUpdate(
                                        doc, foundation, footprint, issue,
                                        window.MarginMm, log);
                                if (tx.Commit() != TransactionStatus.Committed)
                                {
                                    skipped++;
                                    log.Warn("REVIEW VIEW FAILED commit Foundation=" +
                                        issue.FoundationId);
                                    continue;
                                }
                                created++;
                                if (firstView == null) firstView = view;
                                ManholeReviewRegistry.Save(doc, issues);
                            }
                            catch (Exception ex)
                            {
                                if (tx.GetStatus() == TransactionStatus.Started)
                                    tx.RollBack();
                                skipped++;
                                log.Error("Review 3D failed for Foundation " +
                                    issue.FoundationId, ex);
                            }
                        }
                    }

                    ManholeReviewRegistry.Save(doc, issues);
                    // RequestViewChange must run after the view transaction
                    // is closed. Create all views first, then show the first.
                    if (firstView != null)
                        uiDoc.RequestViewChange(firstView);

                    TaskDialog.Show("Manhole Review Queue",
                        "3D review views created/updated: " + created +
                        "\nSkipped: " + skipped +
                        "\nSaved issues: " + issues.Count +
                        "\nRegister: " + path +
                        "\nLog: " + log.LogPath);
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    log.Error("Manhole review manager failed.", ex);
                    message = ex.Message;
                    TaskDialog.Show("Manhole Review Queue",
                        "Unable to complete review management.\n" +
                        ex.Message + "\nLog: " + log.LogPath);
                    return Result.Failed;
                }
            }
        }

        private static void ScanAll(Document doc, DiagnosticLogger log)
        {
            var foundations = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .WhereElementIsNotElementType().ToElements()
                .Where(e =>
                {
                    Element t = doc.GetElement(e.GetTypeId());
                    return t != null && FoundationTypes.Contains(t.Name);
                }).ToList();

            log.WriteHeader("LIGHTWEIGHT REVIEW AUDIT ALL");
            log.Info("Recognized manhole foundations=" + foundations.Count);
            int flagged = 0, clean = 0;
            foreach (Element foundation in foundations)
            {
                try
                {
                    VirtualFoundationResult footprint =
                        new VirtualFoundationRecoveryService(doc, log)
                            .Analyze(foundation);
                    var reasons = new List<string>();
                    var wallIds = new List<int>();

                    if (!footprint.Accepted)
                        reasons.Add("FOOTPRINT: " + footprint.Reason);
                    else
                    {
                        wallIds.AddRange(footprint.Walls.Select(w =>
                            w.Id.IntegerValue));
                        OpeningResetAuditResult audit =
                            OpeningResetAuditService.Audit(doc,
                                footprint.Walls, log);
                        if (audit.ProfileEditedWalls > 0)
                            reasons.Add(audit.ProfileEditedWalls +
                                " edited wall profiles");
                        if (audit.ProfileUnknownWalls > 0)
                            reasons.Add(audit.ProfileUnknownWalls +
                                " unknown profile status");
                        if (audit.VoidCutRelations > 0)
                            reasons.Add(audit.VoidCutRelations +
                                " unattached void-cut relations");
                        if (audit.VoidUnknownWalls > 0)
                            reasons.Add(audit.VoidUnknownWalls +
                                " unknown void status");
                        if (audit.NativeUnmanaged > 0)
                            reasons.Add(audit.NativeUnmanaged +
                                " non-tool native openings");

                        var wallSet = new HashSet<int>(wallIds);
                        foreach (Wall wall in footprint.Walls)
                            foreach (ElementId id in wall.FindInserts(
                                true, true, true, true))
                            {
                                Element element = doc.GetElement(id);
                                var family = element as FamilyInstance;
                                if (family?.Symbol?.Family != null &&
                                    family.Symbol.Family.IsInPlace)
                                    reasons.Add("INPLACE CUTTER " +
                                        id.IntegerValue + " on wall " +
                                        wall.Id.IntegerValue +
                                        (element.Pinned ? " PINNED" : ""));
                                else if (!(element is Opening))
                                    reasons.Add("OTHER WALL INSERT " +
                                        id.IntegerValue + " on wall " +
                                        wall.Id.IntegerValue);
                            }
                    }

                    if (reasons.Count > 0)
                    {
                        flagged++;
                        ManholeReviewRegistry.Upsert(doc, foundation,
                            string.Join("; ", reasons.Distinct()),
                            wallIds, footprint.Accepted
                                ? "REQUIRES CLEANUP" : "GEOMETRY",
                            log);
                    }
                    else
                        clean++;
                }
                catch (Exception ex)
                {
                    flagged++;
                    ManholeReviewRegistry.Upsert(doc, foundation,
                        "SCAN ERROR: " + ex.Message, null,
                        "ERROR", log);
                }
            }
            log.Info("REVIEW AUDIT SUMMARY Flagged=" + flagged +
                " Clean=" + clean +
                " Register=" + ManholeReviewRegistry.RegisterPath(doc));
        }
    }
}
