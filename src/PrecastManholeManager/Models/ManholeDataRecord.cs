namespace Hatco.PrecastManholeManager.Models
{
    internal sealed class ManholeDataRecord
    {
        public string ManholeNumber { get; set; }
        public int FoundationId { get; set; }
        public string FoundationUniqueId { get; set; }

        public int Wall1Id { get; set; }
        public int Wall2Id { get; set; }
        public int Wall3Id { get; set; }
        public int Wall4Id { get; set; }

        public double CenterXmm { get; set; }
        public double CenterYmm { get; set; }
        public double BaseTopZmm { get; set; }
        public double BaseThicknessMm { get; set; }

        public double ClearW1W4Mm { get; set; }
        public double ClearW2W3Mm { get; set; }
        public double OuterW1W4Mm { get; set; }
        public double OuterW2W3Mm { get; set; }
        public double WallHeightMm { get; set; }

        public string Key => FoundationUniqueId ?? FoundationId.ToString();
    }
}
