namespace LocalLm.Core;

/// <summary>
/// Default model per job type.
/// </summary>
public static class LocalModels
{
    /// <summary>
    /// Vision. Chosen for multilingual OCR (this machine's screenshots and documents are mostly
    /// Russian) and because q8_0 fits a single 16GB card - the bare `qwen3-vl:8b` tag is Q4_K_M.
    /// </summary>
    public const string Vision = "qwen3-vl:8b-instruct-q8_0";

    /// <summary>
    /// Compatibility fallback for callers that still require an explicit text model.
    /// New task-aware calls are routed by the broker catalog.
    /// </summary>
    public const string Text = "qwen3.5:9b";

    public const string TextFallbackSingleGpu = "qwen3.5:9b";
}
