namespace LocalAi.Installer.Core.Removal;

/// <summary>
/// One point on the way through a removal: how far along it is, and what it is doing.
///
/// The percentage is the removal's own 0–100 and says nothing about where the removal sits in
/// a larger run. A caller that is running an installation afterwards scales it; a caller that
/// is not passes it through. Keeping the scaling out here is what lets the runner report the
/// same numbers to both.
/// </summary>
public readonly record struct UninstallProgress(int Percent, string Text);
