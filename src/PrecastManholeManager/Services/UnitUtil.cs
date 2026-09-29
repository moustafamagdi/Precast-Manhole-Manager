using Autodesk.Revit.DB;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class UnitUtil
    {
        public static double FtToMm(double feet) =>
            UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

        public static double MmToFt(double mm) =>
            UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    }
}
