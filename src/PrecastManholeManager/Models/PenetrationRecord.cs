using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Hatco.PrecastManholeManager.Models
{
    internal sealed class PenetrationRecord : INotifyPropertyChanged
    {
        private bool _accepted = true;
        private double _clearanceMm = 50.0;
        private string _existingOpeningStatus = "NONE";
        private bool _adoptExistingOpening;

        public string LinkName { get; set; }
        public int LinkInstanceId { get; set; }
        public int LinkedElementId { get; set; }
        public string LinkedUniqueId { get; set; }
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

        public string Shape { get; set; }
        public double DiameterMm { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }

        // Optional per-record opening cut after safe edge fitting.
        // Source sizes/clearance remain unchanged for audit.
        public double CutWidthOverrideMm { get; set; }
        public double CutHeightOverrideMm { get; set; }
        public double? FittedCenterXmm { get; set; }
        public double? FittedCenterYmm { get; set; }
        public double? FittedCenterZmm { get; set; }
        public double EffectiveOpeningXmm => FittedCenterXmm ?? Xmm;
        public double EffectiveOpeningYmm => FittedCenterYmm ?? Ymm;
        public double EffectiveOpeningZmm => FittedCenterZmm ?? Zmm;

        public int ExistingOpeningId { get; set; }
        public double ExistingOpeningWidthMm { get; set; }
        public double ExistingOpeningHeightMm { get; set; }
        public string ExistingOpeningStatus
        {
            get => _existingOpeningStatus;
            set
            {
                if (_existingOpeningStatus == value) return;
                _existingOpeningStatus = value;
                OnPropertyChanged();
            }
        }

        public bool HasManualSufficientOpening =>
            ExistingOpeningId > 0 &&
            ExistingOpeningStatus == "EXISTING SUFFICIENT";

        public bool HasManualTooSmallOpening =>
            ExistingOpeningId > 0 &&
            ExistingOpeningStatus == "EXISTING TOO SMALL";

        public bool AdoptExistingOpening
        {
            get => _adoptExistingOpening;
            set
            {
                if (_adoptExistingOpening == value) return;
                _adoptExistingOpening = value;
                OnPropertyChanged();
            }
        }

        public string ExistingOpeningSize =>
            ExistingOpeningId > 0
                ? ExistingOpeningWidthMm.ToString("0.#", CultureInfo.InvariantCulture) + " x " +
                  ExistingOpeningHeightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm"
                : string.Empty;

        public void RefreshManualOpeningStatus()
        {
            if (ExistingOpeningId <= 0 || ExistingOpeningStatus == "MANAGED")
                return;

            const double sizeToleranceMm = 20.0;
            bool sufficient =
                ExistingOpeningWidthMm + sizeToleranceMm >= CutWidthMm &&
                ExistingOpeningHeightMm + sizeToleranceMm >= CutHeightMm;

            ExistingOpeningStatus = sufficient ? "EXISTING SUFFICIENT" : "EXISTING TOO SMALL";

            if (!sufficient)
                AdoptExistingOpening = false;
        }
        public bool Accepted
        {
            get => _accepted;
            set
            {
                if (_accepted == value) return;
                _accepted = value;
                OnPropertyChanged();
            }
        }

        public double ClearanceMm
        {
            get => _clearanceMm;
            set
            {
                if (System.Math.Abs(_clearanceMm - value) < 0.001) return;
                _clearanceMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OpeningSize));
                RefreshManualOpeningStatus();
                OnPropertyChanged(nameof(ExistingOpeningStatus));
            }
        }

        public double OpeningDiameterMm => DiameterMm > 0 ? DiameterMm + (2.0 * ClearanceMm) : 0;
        public double OpeningWidthMm => WidthMm > 0 ? WidthMm + (2.0 * ClearanceMm) : 0;
        public double OpeningHeightMm => HeightMm > 0 ? HeightMm + (2.0 * ClearanceMm) : 0;

        public string OpeningSize
        {
            get
            {
                if (Shape == "Round" && OpeningDiameterMm > 0)
                    return "Ø" + OpeningDiameterMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm";

                if (Shape == "Rectangular" && OpeningWidthMm > 0 && OpeningHeightMm > 0)
                    return OpeningWidthMm.ToString("0.#", CultureInfo.InvariantCulture) + " x " +
                           OpeningHeightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm";

                return "Review";
            }
        }

        public double ProjectedWidthMm { get; set; }
        public double ProjectedHeightMm { get; set; }
        public double CutWidthMm => CutWidthOverrideMm > 0 ? CutWidthOverrideMm :
            ProjectedWidthMm > 0 ? ProjectedWidthMm + 2 * ClearanceMm :
            (Shape == "Round" ? OpeningDiameterMm : OpeningWidthMm);
        public double CutHeightMm => CutHeightOverrideMm > 0 ? CutHeightOverrideMm :
            ProjectedHeightMm > 0 ? ProjectedHeightMm + 2 * ClearanceMm :
            (Shape == "Round" ? OpeningDiameterMm : OpeningHeightMm);
        public bool EdgeAligned { get; set; }
        public double EdgeShiftMm { get; set; }
        public bool CornerStartAllowed { get; set; }
        public bool CornerEndAllowed { get; set; }
        public string SourceKeyOverride { get; set; }
        public string[] MemberSourceKeys { get; set; }
        public string SourceKey => SourceKeyOverride ?? LinkInstanceId.ToString(CultureInfo.InvariantCulture) + "|" +
                                   (LinkedUniqueId ?? LinkedElementId.ToString(CultureInfo.InvariantCulture)) + "|" +
                                   HostWallId.ToString(CultureInfo.InvariantCulture);

        public string WallLabel => "W" + WallNumber;
        public string OffsetDisplay => OffsetFromWallStartMm.ToString("0.#", CultureInfo.InvariantCulture);
        public string InvertDisplay => InvertMm.ToString("0.#", CultureInfo.InvariantCulture);
        public string InvertAboveBaseDisplay => InvertAboveBaseMm.ToString("0.#", CultureInfo.InvariantCulture);

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
