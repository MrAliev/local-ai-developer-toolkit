using System.Net.NetworkInformation;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class SystemNetworkProbe : INetworkProbe
{
    public Task<NetworkSnapshot> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(
                NetworkInterface.GetIsNetworkAvailable()
                    ? new NetworkSnapshot(ObservationState.Available, null)
                    : new NetworkSnapshot(
                        ObservationState.Unavailable,
                        "No active network interface was reported."));
        }
        catch (NetworkInformationException exception)
        {
            return Task.FromResult(
                new NetworkSnapshot(ObservationState.Failed, exception.Message));
        }
    }
}
