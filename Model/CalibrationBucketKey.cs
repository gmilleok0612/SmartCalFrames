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
    ///
    /// ROUND 94 - code review ("review the entire code for unneeded code no longer used and
    /// errors") found this class's IEquatable&lt;T&gt;/Equals/GetHashCode implementation, and its
    /// [JsonProperty] attributes (this type is never passed to JsonConvert anywhere - the codebase's
    /// only real JSON serialization is SmartCalSequenceItem's own persisted fields, unrelated to
    /// this class), were both leftovers with zero live callers - grepped the whole tree, confirmed
    /// this key is only ever constructed once per run and consumed exclusively through ToString() in
    /// log/status lines, never through a Dictionary/HashSet/Distinct()/GroupBy() keyed on it.
    /// Removed both; only ToString() (genuinely used) and the plain data properties remain.
    /// </summary>
    public class CalibrationBucketKey {

        public string FilterName { get; set; }
        public int BinningX { get; set; }
        public int BinningY { get; set; }
        public int Gain { get; set; }
        public int Offset { get; set; }

        public CalibrationBucketKey() { }

        public CalibrationBucketKey(string filterName, int binningX, int binningY, int gain, int offset) {
            FilterName = filterName ?? "(unfiltered)";
            BinningX = binningX;
            BinningY = binningY;
            Gain = gain;
            Offset = offset;
        }

        public override string ToString() => $"{FilterName} bin{BinningX}x{BinningY} gain{Gain} off{Offset}";
    }
}
