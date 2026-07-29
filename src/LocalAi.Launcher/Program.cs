using LocalAi.Launcher;

var launcherPath = Environment.ProcessPath
    ?? throw new InvalidOperationException("The launcher executable path is unavailable.");
var launcherDirectory = AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar,
    Path.AltDirectorySeparatorChar);
var binRoot = Directory.GetParent(launcherDirectory)?.FullName
    ?? throw new InvalidOperationException("The launcher bin root is unavailable.");

return await LauncherProgram.RunAsync(
    args,
    binRoot,
    launcherPath,
    Console.Error,
    CancellationToken.None);
