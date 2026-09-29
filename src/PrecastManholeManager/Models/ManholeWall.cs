using Autodesk.Revit.DB;

namespace Hatco.PrecastManholeManager.Models
{
    internal sealed class ManholeWall
    {
        public Wall Wall { get; set; }
        public int Number { get; set; }
        public Line Axis { get; set; }
        public XYZ MidPoint { get; set; }
        public XYZ Direction { get; set; }
        public double LengthFt { get; set; }

        public override string ToString() =>
            $"W{Number} | WallId={Wall?.Id.IntegerValue} | Length={LengthFt:F4} ft | Mid=({MidPoint?.X:F4},{MidPoint?.Y:F4},{MidPoint?.Z:F4})";
    }
}
