using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class SystemDiskProbe : IDiskProbe
{
    public DiskSnapshot Observe(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return new DiskSnapshot(
                    ObservationState.Unknown,
                    null,
                    $"Could not determine a drive for '{path}'.");
            }

            var drive = new DriveInfo(root);
            return drive.IsReady
                ? new DiskSnapshot(
                    ObservationState.Available,
                    drive.AvailableFreeSpace,
                    null)
                : new DiskSnapshot(
                    ObservationState.Unknown,
                    null,
                    $"Drive '{root}' is not ready.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DiskSnapshot(ObservationState.Failed, null, exception.Message);
        }
    }
}
