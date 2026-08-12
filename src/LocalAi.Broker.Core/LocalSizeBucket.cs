namespace LocalAi.Broker;

/// <summary>
/// How much text a job carried, coarsely enough that the answer is stable.
///
/// This used to sit beside the duration estimator, which is where it is produced, and that was
/// fine while nothing outside the broker process needed it. Telemetry writes it to disk, so it
/// is now part of a format other tools read back, and a persisted vocabulary cannot live in an
/// executable that only the writer links against.
///
/// The values are serialised by name, so they may be added to but not renamed or reordered.
/// </summary>
public enum LocalSizeBucket
{
    Empty,
    Small,
    Medium,
    Large
}
