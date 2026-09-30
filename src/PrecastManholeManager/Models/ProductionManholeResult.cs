namespace Hatco.PrecastManholeManager.Models
{
    // Control flow must not depend on wording in a user-facing report.
    internal sealed class ProductionManholeResult
    {
        public bool Committed { get; }
        public bool DimensionsComplete { get; }
        public string Summary { get; }

        public ProductionManholeResult(bool committed, bool dimensionsComplete, string summary)
        {
            Committed = committed;
            DimensionsComplete = dimensionsComplete;
            Summary = summary;
        }
    }
}
