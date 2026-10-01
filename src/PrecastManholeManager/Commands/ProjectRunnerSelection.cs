using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.Commands
{
    public sealed partial class ProjectRunnerCommand
    {
        private static List<SimpleManholeItem> SelectOpeningTargets(UIDocument uidoc, bool currentView)
        {
            var doc = uidoc.Document;
            var known = SimpleProjectScanService.LoadFast(doc);
            var allowed = new HashSet<int>(known.Select(x => x.FoundationId));
            HashSet<int> selected;
            if (currentView)
            {
                var view = uidoc.ActiveView;
                if (view == null || view.IsTemplate || view is ViewSheet ||
                    !FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id))
                    throw new InvalidOperationException("Open a model plan, section or 3D view first. Sheets are not a model-view scope.");
                selected = new HashSet<int>(new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                    .WhereElementIsNotElementType().ToElementIds().Select(x => x.IntegerValue));
            }
            else
            {
                var existing = uidoc.Selection.GetElementIds();
                if (existing.Count > 0)
                {
                    selected = new HashSet<int>(existing.Select(x => x.IntegerValue));
                    if (!selected.Any(allowed.Contains))
                        throw new InvalidOperationException("The current selection contains no recognized manhole bases. Clear it and use Pick Bases, or preselect the bases themselves.");
                }
                else
                {
                    try
                    {
                        var picked = uidoc.Selection.PickObjects(ObjectType.Element,
                            new ManholeBaseSelectionFilter(allowed),
                            "Select one or more manhole bases, then Finish. Esc cancels.");
                        selected = new HashSet<int>(picked.Select(x => x.ElementId.IntegerValue));
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
                }
            }
            var result = known.Where(x => selected.Contains(x.FoundationId)).ToList();
            if (result.Count == 0)
                throw new InvalidOperationException("No recognized host-model manhole bases found in this scope. Linked bases and walls are not selected as foundations.");
            uidoc.Selection.SetElementIds(result.Select(x => new ElementId(x.FoundationId)).ToList());
            return result;
        }

        private sealed class ManholeBaseSelectionFilter : ISelectionFilter
        {
            private readonly HashSet<int> allowed;
            public ManholeBaseSelectionFilter(HashSet<int> allowed) { this.allowed = allowed; }
            public bool AllowElement(Element element) => allowed.Contains(element.Id.IntegerValue);
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
