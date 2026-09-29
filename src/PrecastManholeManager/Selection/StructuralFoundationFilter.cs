using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace Hatco.PrecastManholeManager.Selection
{
    internal sealed class StructuralFoundationFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem?.Category != null &&
                   elem.Category.Id.IntegerValue == (int)BuiltInCategory.OST_StructuralFoundation;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
