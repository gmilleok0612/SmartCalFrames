using System;
using Newtonsoft.Json;

namespace SmartCalFrames.Model {

    /// <summary>
    /// Flat panel response depends on more than just the filter - binning
    /// and gain both change how many ADU a given amount of light produces,
    /// and offset shifts the bias floor the linear-region check is relative
    /// to. History is only valid within one of these buckets; mixing them
    /// would corrupt the response curve. Sensor temperature is deliberately
    /// NOT part of the key: for a flat panel (not sky flats) temperature
    /// affects camera dark current, not the amount of light hitting the
    /// sensor, and treating it as a key would fragment history for no
    /// benefit.
    /// </summary>
    public class CalibrationBucketKey : IEquatable<CalibrationBucketKey> {

        [JsonProperty] public string FilterName { get; set; }
        [JsonProperty] public int BinningX { get; set; }
        [JsonProperty] public int BinningY { get; set; }
        [JsonProperty] public int Gain { get; set; }
        [JsonProperty] public int Offset { get; set; }

        public CalibrationBucketKey() { }

        public CalibrationBucketKey(string filterName, int binningX, int binningY, int gain, int offset) {
            FilterName = filterName ?? "(unfiltered)";
            BinningX = binningX;
            BinningY = binningY;
            Gain = gain;
            Offset = offset;
        }

        public bool Equals(CalibrationBucketKey other) {
            if (other is null) return false;
            return string.Equals(FilterName, other.FilterName, StringComparison.OrdinalIgnoreCase)
                && BinningX == other.BinningX
                && BinningY == other.BinningY
                && Gain == other.Gain
                && Offset == other.Offset;
        }

        public override bool Equals(object obj) => Equals(obj as CalibrationBucketKey);

        public override int GetHashCode() =>
            HashCode.Combine(FilterName?.ToUpperInvariant(), BinningX, BinningY, Gain, Offset);

        public override string ToString() => $"{FilterName} bin{BinningX}x{BinningY} gain{Gain} off{Offset}";
    }
}
