namespace SmartCalFrames.Equipment {

    /// <summary>Minimal projection of whatever NINA's IImageStatistics/IImageData expose - keeps the rest of the plugin decoupled from the exact SDK type.
    /// ROUND 94 - dropped StdDev (code review found it was set once, at the one call site that built this
    /// object, and never read anywhere) - Mean and FullWellADU are the only fields this plugin's convergence
    /// math and saturation checks actually consume.</summary>
    public class ImageStatistics {
        public double Mean { get; set; }
        /// <summary>2^BitDepth - 1, i.e. the ADU value that represents full well/saturation for this camera's readout.</summary>
        public double FullWellADU { get; set; }
    }
}
