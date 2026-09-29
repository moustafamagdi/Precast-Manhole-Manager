namespace Hatco.PrecastManholeManager.Models
{
    internal sealed class PenetrationRecord
    {
        public string LinkName { get; set; }
        public int LinkedElementId { get; set; }
        public string Category { get; set; }
        public string FamilyType { get; set; }
        public string SystemName { get; set; }
        public string Size { get; set; }
        public int WallNumber { get; set; }
        public int HostWallId { get; set; }
        public double Xmm { get; set; }
        public double Ymm { get; set; }
        public double Zmm { get; set; }
        public double InvertMm { get; set; }
        public double InvertAboveBaseMm { get; set; }
        public double OffsetFromWallStartMm { get; set; }
        public string Notes { get; set; }
    }
}
