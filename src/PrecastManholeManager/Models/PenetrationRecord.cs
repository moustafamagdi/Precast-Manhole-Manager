using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Hatco.PrecastManholeManager.Models
{
    internal sealed class PenetrationRecord : INotifyPropertyChanged
    {
        private bool _accepted = true;
        private double _clearanceMm = 50.0;

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

        public double CutWidthMm => Shape == "Round" ? OpeningDiameterMm : OpeningWidthMm;
        public double CutHeightMm => Shape == "Round" ? OpeningDiameterMm : OpeningHeightMm;
        public string SourceKey => LinkInstanceId.ToString(CultureInfo.InvariantCulture) + "|" +
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
