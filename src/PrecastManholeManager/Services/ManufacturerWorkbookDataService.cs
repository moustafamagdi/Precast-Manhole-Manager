using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class ManufacturerWorkbookData
    {
        public List<ManholeDataRecord> Manholes { get; set; } = new List<ManholeDataRecord>();
        public List<ManufacturerOpeningRow> Openings { get; set; } = new List<ManufacturerOpeningRow>();
    }

    internal sealed class ManufacturerOpeningRow
    {
        public string ManholeNumber { get; set; }
        public int FoundationId { get; set; }
        public int WallNumber { get; set; }
        public string OpeningNumber { get; set; }
        public string OpeningCode { get; set; }
        public int OpeningElementId { get; set; }
        public double OpeningWidthMm { get; set; }
        public double OpeningHeightMm { get; set; }
        public string OpeningType { get; set; }
        public bool AdoptedManual { get; set; }
        public double OffsetMm { get; set; }
        public double OpeningBottomFromBaseMm { get; set; }
        public double InvertFromBaseMm { get; set; }
        public double AbsoluteInvertMm { get; set; }
        public double CenterElevationMm { get; set; }
        public string ServiceCategory { get; set; }
        public string SystemName { get; set; }
        public string FamilyType { get; set; }
        public string SourceLink { get; set; }
        public int SourceElementId { get; set; }
        public string SourceUniqueId { get; set; }
        public string Status { get; set; }
    }

    internal static class ManufacturerWorkbookDataService
    {
        public static ManufacturerWorkbookData Collect(Document doc, DiagnosticLogger log)
        {
            var data = new ManufacturerWorkbookData();
            data.Manholes = ManholeDataCarrierService.ReadAll(doc);

            Dictionary<int, ManholeDataRecord> byFoundation = data.Manholes
                .GroupBy(x => x.FoundationId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (Opening opening in new FilteredElementCollector(doc)
                         .OfClass(typeof(Opening))
                         .Cast<Opening>())
            {
                ManagedOpeningData managed;
                if (!OpeningStorageService.TryRead(opening, out managed))
                    continue;

                OpeningManholeLinkData link;
                if (!OpeningManholeLinkService.TryReadLink(opening, out link))
                    continue;

                ManholeDataRecord manhole;
                if (!byFoundation.TryGetValue(link.FoundationId, out manhole))
                {
                    log?.Warn(
                        "Workbook export skipped Opening=" + opening.Id.IntegerValue +
                        " because Foundation=" + link.FoundationId + " has no saved data carrier.");
                    continue;
                }

                string category;
                string systemName;
                string familyType;
                double sourceHeightMm;
                ResolveSourceDetails(
                    doc,
                    managed,
                    out category,
                    out systemName,
                    out familyType,
                    out sourceHeightMm);

                if (sourceHeightMm <= 0)
                    sourceHeightMm = Math.Max(0, managed.CutHeightMm - (2.0 * managed.ClearanceMm));

                // Actual native opening may have an asymmetric clearance trim.
                // Its fabrication position must come from the cut boundary,
                // while service invert continues to use the original MEP center.
                double openingCenterXmm = managed.Xmm;
                double openingCenterYmm = managed.Ymm;
                double openingBottomAbsolute =
                    managed.Zmm - (managed.CutHeightMm / 2.0);

                if (opening.IsRectBoundary && opening.BoundaryRect != null &&
                    opening.BoundaryRect.Count >= 2)
                {
                    XYZ p0 = opening.BoundaryRect[0];
                    XYZ p1 = opening.BoundaryRect[1];
                    openingCenterXmm = UnitUtil.FtToMm((p0.X + p1.X) * 0.5);
                    openingCenterYmm = UnitUtil.FtToMm((p0.Y + p1.Y) * 0.5);
                    openingBottomAbsolute = UnitUtil.FtToMm(
                        Math.Min(p0.Z, p1.Z));
                }
                double openingBottomFromBase =
                    openingBottomAbsolute - manhole.BaseTopZmm;

                double absoluteInvert = managed.Zmm - (sourceHeightMm / 2.0);
                double invertFromBase = absoluteInvert - manhole.BaseTopZmm;
                double offset = CalculateOffsetFromWallStart(
                    doc,
                    managed.HostWallId,
                    openingCenterXmm,
                    openingCenterYmm,
                    managed.Zmm);

                string currentManholeNumber = manhole.ManholeNumber ?? link.ManholeNumber ?? string.Empty;
                string openingNumber = link.OpeningNumber ?? string.Empty;
                string currentOpeningCode =
                    currentManholeNumber +
                    "-W" + link.WallNumber +
                    "-" + openingNumber;

                if (!string.Equals(link.ManholeNumber, currentManholeNumber, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Warn(
                        "Opening " + opening.Id.IntegerValue +
                        " has stale stored ManholeNumber='" + (link.ManholeNumber ?? string.Empty) +
                        "'. Export uses current carrier number='" + currentManholeNumber + "'.");
                }

                data.Openings.Add(new ManufacturerOpeningRow
                {
                    ManholeNumber = currentManholeNumber,
                    FoundationId = link.FoundationId,
                    WallNumber = link.WallNumber,
                    OpeningNumber = openingNumber,
                    OpeningCode = currentOpeningCode,
                    OpeningElementId = opening.Id.IntegerValue,
                    OpeningWidthMm = managed.CutWidthMm,
                    OpeningHeightMm = managed.CutHeightMm,
                    OpeningType = managed.AdoptedManual ? "Adopted Manual" : "Managed",
                    AdoptedManual = managed.AdoptedManual,
                    OffsetMm = offset,
                    OpeningBottomFromBaseMm = openingBottomFromBase,
                    InvertFromBaseMm = invertFromBase,
                    AbsoluteInvertMm = absoluteInvert,
                    CenterElevationMm = managed.Zmm,
                    ServiceCategory = category,
                    SystemName = systemName,
                    FamilyType = familyType,
                    SourceLink = managed.LinkName,
                    SourceElementId = managed.LinkedElementId,
                    SourceUniqueId = managed.LinkedUniqueId,
                    Status = "OK"
                });
            }

            data.Openings = data.Openings
                .OrderBy(x => x.ManholeNumber ?? string.Empty)
                .ThenBy(x => x.WallNumber)
                .ThenBy(x => x.OpeningNumber ?? string.Empty)
                .ToList();

            log?.Info(
                "Workbook data collected: Manholes=" + data.Manholes.Count +
                ", Openings=" + data.Openings.Count);

            return data;
        }

        private static double CalculateOffsetFromWallStart(
            Document doc,
            int wallId,
            double xmm,
            double ymm,
            double zmm)
        {
            Wall wall = doc.GetElement(new ElementId(wallId)) as Wall;
            if (wall == null || !(wall.Location is LocationCurve lc) || !(lc.Curve is Line line))
                return 0;

            XYZ a = line.GetEndPoint(0);
            XYZ b = line.GetEndPoint(1);
            XYZ start =
                (a.X < b.X || (Math.Abs(a.X - b.X) < 1e-9 && a.Y <= b.Y))
                    ? a
                    : b;

            XYZ other = start.IsAlmostEqualTo(a) ? b : a;
            XYZ direction = (other - start).Normalize();

            XYZ point = new XYZ(
                UnitUtil.MmToFt(xmm),
                UnitUtil.MmToFt(ymm),
                UnitUtil.MmToFt(zmm));

            return UnitUtil.FtToMm(Math.Abs((point - start).DotProduct(direction)));
        }

        private static void ResolveSourceDetails(
            Document host,
            ManagedOpeningData managed,
            out string category,
            out string systemName,
            out string familyType,
            out double sourceHeightMm)
        {
            category = string.Empty;
            systemName = string.Empty;
            familyType = string.Empty;
            sourceHeightMm = 0;

            RevitLinkInstance link =
                host.GetElement(new ElementId(managed.LinkInstanceId)) as RevitLinkInstance;
            Document linkDoc = link?.GetLinkDocument();
            if (linkDoc == null)
                return;

            Element source = null;

            if (!string.IsNullOrWhiteSpace(managed.LinkedUniqueId))
            {
                try { source = linkDoc.GetElement(managed.LinkedUniqueId); }
                catch { }
            }

            if (source == null && managed.LinkedElementId > 0)
                source = linkDoc.GetElement(new ElementId(managed.LinkedElementId));

            if (source == null)
                return;

            category = source.Category?.Name ?? string.Empty;
            Element type = source.Document.GetElement(source.GetTypeId());
            familyType = type?.Name ?? source.Name ?? string.Empty;
            systemName = GetParameterText(
                source,
                "System Name",
                "System Type",
                "System Classification");

            double diameterFt = GetDouble(source, BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            if (diameterFt <= 0)
                diameterFt = GetDouble(source, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

            if (diameterFt > 0)
            {
                sourceHeightMm = UnitUtil.FtToMm(diameterFt);
                return;
            }

            double heightFt = GetDouble(source, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            if (heightFt <= 0)
            {
                Parameter hp = source.LookupParameter("Height");
                if (hp != null && hp.StorageType == StorageType.Double)
                    heightFt = hp.AsDouble();
            }

            if (heightFt > 0)
                sourceHeightMm = UnitUtil.FtToMm(heightFt);
        }

        private static double GetDouble(Element e, BuiltInParameter bip)
        {
            Parameter p = e.get_Parameter(bip);
            return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : 0;
        }

        private static string GetParameterText(Element e, params string[] names)
        {
            foreach (string name in names)
            {
                Parameter p = e.LookupParameter(name);
                if (p == null) continue;

                string value = p.AsString() ?? p.AsValueString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }
    }
}
