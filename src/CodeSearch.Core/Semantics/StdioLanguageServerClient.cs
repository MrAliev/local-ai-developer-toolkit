using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeSearch.Core.Semantics;

public sealed record LanguageServerProcessSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    TimeSpan? RequestTimeout = null,
    TimeSpan? ShutdownTimeout = null,
    int MaximumMessageBytes = 16 * 1024 * 1024,
    int MaximumStandardErrorBytes = 1024 * 1024)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(15);
    public TimeSpan EffectiveShutdownTimeout => ShutdownTimeout ?? TimeSpan.FromSeconds(3);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Executable);
        ArgumentNullException.ThrowIfNull(Arguments);
        if (Arguments.Any(argument => argument is null) ||
            EffectiveRequestTimeout <= TimeSpan.Zero ||
            EffectiveShutdownTimeout <= TimeSpan.Zero ||
            MaximumMessageBytes <= 0 ||
            MaximumStandardErrorBytes <= 0)
        {
            throw new ArgumentException("Language server process specification is invalid.");
        }
    }
}

/// <summary>Process-backed LSP 3.17 client using full-document UTF-16 synchronization.</summary>
public sealed class StdioLanguageServerClient : ILanguageServerClient
{
    private readonly Process? _process;
    private readonly LspJsonRpcConnection _connection;
    private readonly LanguageServerProcessSpec _spec;
    private readonly Task<string> _standardError;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _openText = new(StringComparer.Ordinal);
    private int _textDocumentSyncKind;
    private bool _openClose = true;
    private bool _definitionProvider;
    private bool _referencesProvider;
    private bool _implementationProvider;
    private int _initialized;
    private int _disposed;

    private StdioLanguageServerClient(
        Process? process,
        LspJsonRpcConnection connection,
        LanguageServerProcessSpec spec,
        Task<string> standardError)
    {
        _process = process;
        _connection = connection;
        _spec = spec;
        _standardError = standardError;
    }

    internal static StdioLanguageServerClient CreateForTesting(
        LspJsonRpcConnection connection,
        LanguageServerProcessSpec spec) =>
        new(null, connection, spec, Task.FromResult(string.Empty));

    public static StdioLanguageServerClient Start(
        string workspaceRoot,
        LanguageServerProcessSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();
        var root = Path.GetFullPath(workspaceRoot);
        var executable = ExecutableResolver.Resolve(spec.Executable);
        var commandScript = OperatingSystem.IsWindows() &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        var start = new ProcessStartInfo(
            commandScript
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : executable)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (commandScript)
        {
            start.Arguments = "/d /s /c \"" + CommandScriptInvocation(executable, spec.Arguments) + "\"";
        }
        else
        {
            foreach (var argument in spec.Arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Language server process did not start.");
            }

            var connection = new LspJsonRpcConnection(
                process.StandardOutput.BaseStream,
                process.StandardInput.BaseStream,
                spec.MaximumMessageBytes);
            return new StdioLanguageServerClient(
                process,
                connection,
                spec,
                DrainStandardErrorAsync(
                    process.StandardError.BaseStream,
                    spec.MaximumStandardErrorBytes));
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Language server executable '{spec.Executable}' could not start: {exception.Message}",
                exception);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async Task InitializeAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _initialized) != 0)
            {
                return;
            }

            var rootUri = ToUri(Path.GetFullPath(workspaceRoot)).AbsoluteUri;
            var initializeResult = await _connection.RequestAsync(
                "initialize",
                new
                {
                    processId = Environment.ProcessId,
                    clientInfo = new
                    {
                        name = "LocalAi",
                        version = typeof(StdioLanguageServerClient).Assembly
                            .GetName().Version?.ToString(3) ?? "unknown",
                    },
                    rootUri,
                    capabilities = new
                    {
                        general = new { positionEncodings = new[] { "utf-16" } },
                        textDocument = new
                        {
                            synchronization = new { dynamicRegistration = false },
                            definition = new { dynamicRegistration = false, linkSupport = true },
                            references = new { dynamicRegistration = false },
                        },
                        workspace = new { configuration = false, workspaceFolders = false },
                    },
                    workspaceFolders = new[] { new { uri = rootUri, name = new DirectoryInfo(workspaceRoot).Name } },
                },
                _spec.EffectiveRequestTimeout,
                cancellationToken);
            ConfigureSynchronization(initializeResult);
            ConfigureNavigationCapabilities(initializeResult);
            if (_textDocumentSyncKind == 0)
            {
                throw new LspProtocolException(
                    "Language server does not support text-document synchronization.");
            }

            await _connection.NotifyAsync("initialized", new { }, cancellationToken);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public Task DidOpenAsync(LspTextDocument document, CancellationToken cancellationToken)
    {
        RequireInitialized();
        return OpenAsync(document, cancellationToken);
    }

    public Task DidChangeAsync(LspTextDocument document, CancellationToken cancellationToken)
    {
        RequireInitialized();
        return ChangeAsync(document, cancellationToken);
    }

    public Task DidCloseAsync(Uri documentUri, CancellationToken cancellationToken)
    {
        RequireInitialized();
        return CloseAsync(documentUri, cancellationToken);
    }

    public async Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
        Uri documentUri,
        int line,
        int utf16Column,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        if (!_definitionProvider)
        {
            return [];
        }

        var result = await PositionRequestAsync(
            "textDocument/definition",
            documentUri,
            line,
            utf16Column,
            null,
            cancellationToken);
        return ParseLocations(result, allowLocationLinks: true);
    }

    public async Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
        Uri documentUri,
        int line,
        int utf16Column,
        bool includeDefinition,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        if (!_referencesProvider)
        {
            return [];
        }

        var result = await PositionRequestAsync(
            "textDocument/references",
            documentUri,
            line,
            utf16Column,
            new { includeDeclaration = includeDefinition },
            cancellationToken);
        return ParseLocations(result, allowLocationLinks: false);
    }

    public async Task<IReadOnlyList<LspLocation>> FindImplementationsAsync(
        Uri documentUri,
        int line,
        int utf16Column,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        if (!_implementationProvider)
        {
            return [];
        }

        var result = await PositionRequestAsync(
            "textDocument/implementation",
            documentUri,
            line,
            utf16Column,
            null,
            cancellationToken);
        return ParseLocations(result, allowLocationLinks: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _initialized) != 0 && _process is { HasExited: false })
        {
            try
            {
                await _connection.RequestAsync(
                    "shutdown",
                    null,
                    _spec.EffectiveShutdownTimeout,
                    CancellationToken.None);
                await _connection.NotifyAsync("exit", null, CancellationToken.None);
                using var timeout = new CancellationTokenSource(_spec.EffectiveShutdownTimeout);
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or OperationCanceledException)
            {
            }
        }

        if (_process is not null)
        {
            Kill(_process);
        }

        await _connection.DisposeAsync();
        try
        {
            await _standardError;
        }
        catch (IOException)
        {
        }

        _process?.Dispose();
        _initializeGate.Dispose();
    }

    private async Task<JsonElement> PositionRequestAsync(
        string method,
        Uri documentUri,
        int line,
        int utf16Column,
        object? context,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        if (line < 0 || utf16Column < 0)
        {
            throw new ArgumentOutOfRangeException(line < 0 ? nameof(line) : nameof(utf16Column));
        }

        return await _connection.RequestAsync(
            method,
            context is null
                ? new
                {
                    textDocument = new { uri = documentUri.AbsoluteUri },
                    position = new { line, character = utf16Column },
                }
                : new
                {
                    textDocument = new { uri = documentUri.AbsoluteUri },
                    position = new { line, character = utf16Column },
                    context,
                },
            _spec.EffectiveRequestTimeout,
            cancellationToken);
    }

    private async Task OpenAsync(
        LspTextDocument document,
        CancellationToken cancellationToken)
    {
        if (_openClose)
        {
            await _connection.NotifyAsync(
                "textDocument/didOpen",
                new
                {
                    textDocument = new
                    {
                        uri = document.Uri.AbsoluteUri,
                        languageId = document.LanguageId,
                        version = document.Version,
                        text = document.Text,
                    },
                },
                cancellationToken);
        }

        _openText[document.Uri.AbsoluteUri] = document.Text;
    }

    private async Task ChangeAsync(
        LspTextDocument document,
        CancellationToken cancellationToken)
    {
        if (!_openText.TryGetValue(document.Uri.AbsoluteUri, out var previous))
        {
            throw new InvalidOperationException("Language server document was not opened.");
        }

        object[] changes = _textDocumentSyncKind == 2
            ?
            [
                new
                {
                    range = new
                    {
                        start = new { line = 0, character = 0 },
                        end = EndPosition(previous),
                    },
                    rangeLength = previous.Length,
                    text = document.Text,
                },
            ]
            : [new { text = document.Text }];
        await _connection.NotifyAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = document.Uri.AbsoluteUri, version = document.Version },
                contentChanges = changes,
            },
            cancellationToken);
        _openText[document.Uri.AbsoluteUri] = document.Text;
    }

    private async Task CloseAsync(Uri documentUri, CancellationToken cancellationToken)
    {
        _openText.TryRemove(documentUri.AbsoluteUri, out _);
        if (_openClose)
        {
            await _connection.NotifyAsync(
                "textDocument/didClose",
                new { textDocument = new { uri = documentUri.AbsoluteUri } },
                cancellationToken);
        }
    }

    private void ConfigureSynchronization(JsonElement initializeResult)
    {
        if (!initializeResult.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("textDocumentSync", out var synchronization))
        {
            _textDocumentSyncKind = 0;
            return;
        }

        if (synchronization.ValueKind == JsonValueKind.Number &&
            synchronization.TryGetInt32(out var numericKind))
        {
            _textDocumentSyncKind = numericKind;
            return;
        }

        if (synchronization.ValueKind != JsonValueKind.Object)
        {
            throw new LspProtocolException("Language server textDocumentSync capability is invalid.");
        }

        _openClose = !synchronization.TryGetProperty("openClose", out var openClose) ||
                     openClose.ValueKind == JsonValueKind.True;
        _textDocumentSyncKind = synchronization.TryGetProperty("change", out var change) &&
                                change.TryGetInt32(out var objectKind)
            ? objectKind
            : 0;
        if (_textDocumentSyncKind is < 0 or > 2)
        {
            throw new LspProtocolException("Language server textDocumentSync kind is invalid.");
        }
    }

    private void ConfigureNavigationCapabilities(JsonElement initializeResult)
    {
        if (!initializeResult.TryGetProperty("capabilities", out var capabilities))
        {
            return;
        }

        _definitionProvider = ProviderEnabled(capabilities, "definitionProvider");
        _referencesProvider = ProviderEnabled(capabilities, "referencesProvider");
        _implementationProvider = ProviderEnabled(capabilities, "implementationProvider");
    }

    private static bool ProviderEnabled(JsonElement capabilities, string propertyName)
    {
        if (!capabilities.TryGetProperty(propertyName, out var provider))
        {
            return false;
        }

        return provider.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.Object => true,
            _ => throw new LspProtocolException(
                $"Language server capability '{propertyName}' is invalid."),
        };
    }

    private static object EndPosition(string text)
    {
        var line = 0;
        var character = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                line++;
                character = 0;
            }
            else if (text[index] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return new { line, character };
    }

    internal static IReadOnlyList<LspLocation> ParseLocations(
        JsonElement result,
        bool allowLocationLinks)
    {
        if (result.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        var values = result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().ToArray()
            : new[] { result };
        var locations = new List<LspLocation>(values.Length);
        foreach (var value in values)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new LspProtocolException("Language server location result is invalid.");
            }

            var isLink = value.TryGetProperty("targetUri", out var uriElement);
            if (isLink && !allowLocationLinks)
            {
                throw new LspProtocolException("Language server returned LocationLink where Location was required.");
            }

            if (!isLink && !value.TryGetProperty("uri", out uriElement))
            {
                throw new LspProtocolException("Language server location URI is missing.");
            }

            var rangeName = isLink ? "targetSelectionRange" : "range";
            if (!value.TryGetProperty(rangeName, out var rangeElement) ||
                !Uri.TryCreate(uriElement.GetString(), UriKind.Absolute, out var uri))
            {
                throw new LspProtocolException("Language server location is malformed.");
            }

            locations.Add(new LspLocation(NormalizeFileUri(uri), ParseRange(rangeElement)));
        }

        return locations;
    }

    private static Uri NormalizeFileUri(Uri uri)
    {
        if (!OperatingSystem.IsWindows() || !uri.IsFile)
        {
            return uri;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Length >= 4 && path[0] == '/' && char.IsAsciiLetter(path[1]) &&
            path[2] == ':' && path[3] == '/')
        {
            return new Uri(path[1..].Replace('/', Path.DirectorySeparatorChar), UriKind.Absolute);
        }

        return uri;
    }

    private static SourceRange ParseRange(JsonElement value)
    {
        if (!value.TryGetProperty("start", out var start) ||
            !value.TryGetProperty("end", out var end) ||
            !TryPosition(start, out var startLine, out var startCharacter) ||
            !TryPosition(end, out var endLine, out var endCharacter) ||
            endLine < startLine || endLine == startLine && endCharacter < startCharacter)
        {
            throw new LspProtocolException("Language server range is invalid.");
        }

        return new SourceRange(startLine, startCharacter, endLine, endCharacter);
    }

    private static bool TryPosition(JsonElement value, out int line, out int character)
    {
        line = -1;
        character = -1;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty("line", out var lineElement) && lineElement.TryGetInt32(out line) &&
               value.TryGetProperty("character", out var characterElement) &&
               characterElement.TryGetInt32(out character) &&
               line >= 0 && character >= 0;
    }

    private static async Task<string> DrainStandardErrorAsync(Stream stream, int maximumBytes)
    {
        using var captured = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var remaining = maximumBytes - checked((int)captured.Length);
            if (remaining > 0)
            {
                captured.Write(buffer, 0, Math.Min(read, remaining));
            }
        }

        return Encoding.UTF8.GetString(captured.GetBuffer(), 0, checked((int)captured.Length));
    }

    private static string CommandScriptInvocation(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var values = new[] { executable }.Concat(arguments).ToArray();
        if (values.Any(value => value.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0))
        {
            throw new ArgumentException(
                "Windows language-server command paths and arguments contain unsafe characters.");
        }

        return string.Join(" ", values.Select(value => $"\"{value}\""));
    }

    private static Uri ToUri(string fullPath) => new(fullPath, UriKind.Absolute);

    private static void Kill(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private void RequireInitialized()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new InvalidOperationException("Language server is not initialized.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
