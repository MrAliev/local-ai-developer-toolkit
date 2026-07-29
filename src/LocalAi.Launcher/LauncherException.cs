namespace LocalAi.Launcher;

public sealed class LauncherException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
