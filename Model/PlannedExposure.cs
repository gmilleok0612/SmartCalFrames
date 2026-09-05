namespace SmartCalFrames.Model {

    /// <summary>
    /// ROUND 32 - simplified down to two outcomes now that ConvergeOnTargetAsync no longer searches
    /// brightness or scores against a learned model. Converged means a real frame landed within
    /// Tolerance and this filter's default brightness/exposure were updated to match; Failed means it
    /// didn't, no frame was saved, and the caller reports "calibration failed" rather than accepting a
    /// best-effort result the way earlier rounds did (that "accept the closest we found" behavior is
    /// exactly what let bad-ADU frames get saved - see the Round 32 KB entry for the real log evidence).
    /// </summary>
    public enum PlanKind {
        Converged,
        Failed
    }

    public class PlannedExposure {
        public int Brightness { get; set; }
        public double ExposureSeconds { get; set; }
        public double PredictedADU { get; set; }
        public PlanKind Kind { get; set; }
        public string Reason { get; set; }
    }
}
