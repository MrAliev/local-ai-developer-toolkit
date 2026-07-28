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
    /// Text. Best-tested general model on this machine.
    ///
    /// CAVEAT: it needs ~17-19GB in VRAM, so it only runs today because a second GPU is present.
    /// Once the 4070 Ti is removed, this default has to drop to something that fits 16.3GB -
    /// `qwen3.5:9b` is the fallback already installed.
    /// </summary>
    public const string Text = "qwen3.6:27b";

    public const string TextFallbackSingleGpu = "qwen3.5:9b";
}
