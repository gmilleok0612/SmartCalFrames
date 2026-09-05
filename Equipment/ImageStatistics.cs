namespace SmartCalFrames.Equipment {

    /// <summary>Minimal projection of whatever NINA's IImageStatistics/IImageData expose - keeps the rest of the plugin decoupled from the exact SDK type.</summary>
    public class ImageStatistics {
        public double Mean { get; set; }
        public double StdDev { get; set; }
        /// <summary>2^BitDepth - 1, i.e. the ADU value that represents full well/saturation for this camera's readout.</summary>
        public double FullWellADU { get; set; }
    }
}
