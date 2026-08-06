using System.Collections.Concurrent;

namespace CodeSearch.Core.Semantics;

public sealed record LspTextDocument(
    Uri Uri,
    string LanguageId,
    int Version,
    string Text);

public sealed record LspLocation(Uri Uri, SourceRange Range);

/// <summary>
/// Protocol-independent client boundary. A process/JSON-RPC transport can implement this
/// without leaking server lifetime or document-version rules into semantic navigation.
/// </summary>
public interface ILanguageServerClient : IAsyncDisposable
{
    Task InitializeAsync(string workspaceRoot, CancellationToken cancellationToken);
    Task DidOpenAsync(LspTextDocument document, CancellationToken cancellationToken);
    Task DidChangeAsync(LspTextDocument document, CancellationToken cancellationToken);
    Task DidCloseAsync(Uri documentUri, CancellationToken cancellationToken);
    Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
        Uri documentUri,
        int line,
        int utf16Column,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
        Uri documentUri,
        int line,
        int utf16Column,
        bool includeDefinition,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<LspLocation>> FindImplementationsAsync(
        Uri documentUri,
        int line,
        int utf16Column,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns one initialized language-server session per workspace/language pair and tracks the
/// authoritative in-memory version of every open document.
/// </summary>
public sealed class LanguageServerSessionManager : IAsyncDisposable
{
    private readonly Func<string, string, ILanguageServerClient> _clientFactory;
    private readonly ConcurrentDictionary<SessionKey, Session> _sessions =
        new(SessionKeyEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, Session> _documents = new(PathComparer);
    private int _disposed;

    public LanguageServerSessionManager(
        Func<string, string, ILanguageServerClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public async Task OpenOrUpdateAsync(
        string workspaceRoot,
        string documentPath,
        string languageId,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        ArgumentNullException.ThrowIfNull(text);
        if (version < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        var root = NormalizeRoot(workspaceRoot);
        var fullPath = ResolveDocument(root, documentPath);
        var key = new SessionKey(root, languageId);
        var session = _sessions.GetOrAdd(
            key,
            value => new Session(
                value.WorkspaceRoot,
                value.LanguageId,
                () => _clientFactory(value.WorkspaceRoot, value.LanguageId)));

        if (_documents.TryGetValue(fullPath, out var previous) && previous != session)
        {
            await previous.CloseAsync(fullPath, cancellationToken);
        }

        await session.OpenOrUpdateAsync(fullPath, version, text, cancellationToken);
        _documents[fullPath] = session;
    }

    public Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            static (session, path, queryLine, column, token) =>
                session.GoToDefinitionAsync(path, queryLine, column, token),
            cancellationToken);

    public Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            (session, path, queryLine, column, token) =>
                session.FindReferencesAsync(
                    path,
                    queryLine,
                    column,
                    includeDefinition,
                    token),
            cancellationToken);

    public Task<IReadOnlyList<LspLocation>> FindImplementationsAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            workspaceRoot,
            documentPath,
            line,
            utf16Column,
            static (session, path, queryLine, column, token) =>
                session.FindImplementationsAsync(path, queryLine, column, token),
            cancellationToken);

    public bool IsOpen(string workspaceRoot, string documentPath)
    {
        ThrowIfDisposed();
        var fullPath = ResolveDocument(NormalizeRoot(workspaceRoot), documentPath);
        return _documents.ContainsKey(fullPath);
    }

    public async Task CloseAsync(
        string workspaceRoot,
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fullPath = ResolveDocument(NormalizeRoot(workspaceRoot), documentPath);
        if (_documents.TryRemove(fullPath, out var session))
        {
            await session.CloseAsync(fullPath, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _documents.Clear();
        var sessions = _sessions.Values.ToArray();
        _sessions.Clear();
        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }
    }

    private async Task<IReadOnlyList<LspLocation>> QueryAsync(
        string workspaceRoot,
        string documentPath,
        int line,
        int utf16Column,
        Func<Session, string, int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>> query,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (line < 0 || utf16Column < 0)
        {
            throw new ArgumentOutOfRangeException(
                line < 0 ? nameof(line) : nameof(utf16Column));
        }

        var fullPath = ResolveDocument(NormalizeRoot(workspaceRoot), documentPath);
        if (!_documents.TryGetValue(fullPath, out var session))
        {
            return [];
        }

        return await query(session, fullPath, line, utf16Column, cancellationToken);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static string ResolveDocument(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ArgumentException("LSP document must be inside its workspace.", nameof(path));
        }

        return fullPath;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly record struct SessionKey(string WorkspaceRoot, string LanguageId);

    private sealed class SessionKeyEqualityComparer : IEqualityComparer<SessionKey>
    {
        public static SessionKeyEqualityComparer Instance { get; } = new();

        public bool Equals(SessionKey left, SessionKey right) =>
            PathComparer.Equals(left.WorkspaceRoot, right.WorkspaceRoot) &&
            StringComparer.Ordinal.Equals(left.LanguageId, right.LanguageId);

        public int GetHashCode(SessionKey value) =>
            HashCode.Combine(
                PathComparer.GetHashCode(value.WorkspaceRoot),
                StringComparer.Ordinal.GetHashCode(value.LanguageId));
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly string _workspaceRoot;
        private readonly string _languageId;
        private readonly Func<ILanguageServerClient> _clientFactory;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, LspTextDocument> _documents = new(PathComparer);
        private ILanguageServerClient _client;
        private bool _initialized;
        private bool _disposed;

        public Session(
            string workspaceRoot,
            string languageId,
            Func<ILanguageServerClient> clientFactory)
        {
            _workspaceRoot = workspaceRoot;
            _languageId = languageId;
            _clientFactory = clientFactory;
            _client = CreateClient();
        }

        public async Task OpenOrUpdateAsync(
            string fullPath,
            int version,
            string text,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var document = Document(fullPath, version, text);
                if (_documents.TryGetValue(fullPath, out var previous))
                {
                    if (version <= previous.Version)
                    {
                        throw new InvalidOperationException(
                            $"LSP document version {version} must be greater than {previous.Version}.");
                    }

                    await ExecuteWithRecoveryAsync(
                        client => client.DidChangeAsync(document, cancellationToken),
                        cancellationToken);
                }
                else
                {
                    await ExecuteWithRecoveryAsync(
                        client => client.DidOpenAsync(document, cancellationToken),
                        cancellationToken);
                }

                _documents[fullPath] = document;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CloseAsync(string fullPath, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_documents.ContainsKey(fullPath))
                {
                    await ExecuteWithRecoveryAsync(
                        client => client.DidCloseAsync(ToUri(fullPath), cancellationToken),
                        cancellationToken);
                    _documents.Remove(fullPath);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
            string fullPath,
            int line,
            int column,
            CancellationToken cancellationToken) =>
            QueryAsync(
                fullPath,
                (client, uri, token) => client.GoToDefinitionAsync(uri, line, column, token),
                cancellationToken);

        public Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
            string fullPath,
            int line,
            int column,
            bool includeDefinition,
            CancellationToken cancellationToken) =>
            QueryAsync(
                fullPath,
                (client, uri, token) => client.FindReferencesAsync(
                    uri,
                    line,
                    column,
                    includeDefinition,
                    token),
                cancellationToken);

        public Task<IReadOnlyList<LspLocation>> FindImplementationsAsync(
            string fullPath,
            int line,
            int column,
            CancellationToken cancellationToken) =>
            QueryAsync(
                fullPath,
                (client, uri, token) => client.FindImplementationsAsync(
                    uri,
                    line,
                    column,
                    token),
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var path in _documents.Keys.ToArray())
                {
                    try
                    {
                        await _client.DidCloseAsync(ToUri(path), CancellationToken.None);
                    }
                    catch (Exception exception) when (IsRecoverable(exception, CancellationToken.None))
                    {
                    }
                }

                _documents.Clear();
                _disposed = true;
                try
                {
                    await _client.DisposeAsync();
                }
                catch (Exception exception) when (IsRecoverable(exception, CancellationToken.None))
                {
                }
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private async Task<IReadOnlyList<LspLocation>> QueryAsync(
            string fullPath,
            Func<ILanguageServerClient, Uri, CancellationToken,
                Task<IReadOnlyList<LspLocation>>> query,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_documents.ContainsKey(fullPath))
                {
                    return [];
                }

                return await ExecuteWithRecoveryAsync(
                    client => query(client, ToUri(fullPath), cancellationToken),
                    cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
            {
                await _client.InitializeAsync(_workspaceRoot, cancellationToken);
                _initialized = true;
            }
        }

        private async Task<T> ExecuteWithRecoveryAsync<T>(
            Func<ILanguageServerClient, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            try
            {
                await EnsureInitializedAsync(cancellationToken);
                return await operation(_client);
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
                await RestartAsync(cancellationToken);
                return await operation(_client);
            }
        }

        private async Task ExecuteWithRecoveryAsync(
            Func<ILanguageServerClient, Task> operation,
            CancellationToken cancellationToken) =>
            await ExecuteWithRecoveryAsync(
                async client =>
                {
                    await operation(client);
                    return true;
                },
                cancellationToken);

        private async Task RestartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
            }

            _client = CreateClient();
            _initialized = false;
            await EnsureInitializedAsync(cancellationToken);
            foreach (var document in _documents.Values
                         .OrderBy(value => value.Uri.AbsoluteUri, StringComparer.Ordinal))
            {
                await _client.DidOpenAsync(document, cancellationToken);
            }
        }

        private ILanguageServerClient CreateClient() =>
            _clientFactory() ?? throw new InvalidOperationException(
                "Language-server client factory returned null.");

        private static bool IsRecoverable(
            Exception exception,
            CancellationToken cancellationToken) =>
            exception is IOException or TimeoutException or ObjectDisposedException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

        private LspTextDocument Document(string fullPath, int version, string text) =>
            new(ToUri(fullPath), _languageId, version, text);

        private static Uri ToUri(string fullPath) => new(fullPath, UriKind.Absolute);
    }
}
