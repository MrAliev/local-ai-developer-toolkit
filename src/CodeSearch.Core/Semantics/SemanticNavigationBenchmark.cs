using System.Diagnostics;
using CodeSearch.Core.Indexing;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Core.Semantics;

public enum SemanticBenchmarkOperation : byte
{
    Definition = 1,
    References = 2,
    Implementations = 3,
    RelationshipsIncoming = 4,
    RelationshipsOutgoing = 5,
}

public sealed record SemanticBenchmarkMarker(
    string Path,
    string Marker,
    int Occurrence = 0,
    int CharacterOffset = 0);

public sealed record SemanticBenchmarkCase(
    string Name,
    SemanticBenchmarkOperation Operation,
    SemanticBenchmarkMarker Source,
    IReadOnlyList<SemanticBenchmarkMarker> Expected,
    bool AllowAdditional = false,
    bool IncludeDefinition = true,
    SemanticRelationshipKind? RelationshipKind = null);

public sealed record SemanticBenchmarkSuite(
    int SchemaVersion,
    int Iterations,
    IReadOnlyList<SemanticBenchmarkCase> Cases)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record SemanticBenchmarkCaseResult(
    string Name,
    bool Passed,
    int ExpectedCount,
    int ActualCount,
    double FirstQueryMilliseconds,
    double WarmP50Milliseconds,
    double WarmP95Milliseconds,
    double WarmMaximumMilliseconds,
    IReadOnlyList<string> Missing);

public sealed record SemanticBenchmarkResult(
    int Passed,
    int Total,
    double Correctness,
    IReadOnlyList<SemanticBenchmarkCaseResult> Cases);

/// <summary>Deterministic marker-based correctness and in-process query-latency evaluation.</summary>
public sealed class SemanticNavigationBenchmark(SemanticIndex index)
{
    private readonly SemanticIndex _index =
        index?.NormalizeForUse() ?? throw new ArgumentNullException(nameof(index));

    public SemanticBenchmarkResult Run(string repositoryRoot, SemanticBenchmarkSuite suite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(suite);
        if (suite.SchemaVersion != SemanticBenchmarkSuite.CurrentSchemaVersion ||
            suite.Iterations is < 1 or > 100_000 ||
            suite.Cases.Count is < 1 or > 10_000)
        {
            throw new ArgumentException("Semantic benchmark suite is invalid.", nameof(suite));
        }

        var root = Path.GetFullPath(repositoryRoot);
        var service = new SemanticNavigationService(_index);
        var snapshot = new SemanticSnapshotIdentity(
            _index.RepositoryId,
            _index.GenerationId,
            _index.GitTree,
            _index.DirtyHash);
        var results = suite.Cases.Select(@case =>
            RunCase(root, service, snapshot, @case, suite.Iterations)).ToList();
        var passed = results.Count(result => result.Passed);
        return new SemanticBenchmarkResult(
            passed,
            results.Count,
            (double)passed / results.Count,
            results);
    }

    private static SemanticBenchmarkCaseResult RunCase(
        string root,
        SemanticNavigationService service,
        SemanticSnapshotIdentity snapshot,
        SemanticBenchmarkCase @case,
        int iterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@case.Name);
        var source = ResolveMarker(root, @case.Source);

        var stopwatch = Stopwatch.StartNew();
        var actual = Query(service, snapshot, @case, source.Line, source.Character);
        stopwatch.Stop();
        var first = stopwatch.Elapsed.TotalMilliseconds;

        var expected = @case.Expected
            .Select(marker =>
            {
                var position = ResolveMarker(root, marker);
                return $"{NormalizePath(marker.Path)}:{position.Line}:{position.Character}";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualKeys = actual
            .Select(location =>
                $"{NormalizePath(location.DocumentPath)}:{location.Range.StartLine}:" +
                location.Range.StartCharacter)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = expected.Except(actualKeys, StringComparer.Ordinal).ToArray();
        var passed = missing.Length == 0 && (@case.AllowAdditional || actualKeys.Length == expected.Length);

        var samples = new double[iterations];
        for (var i = 0; i < samples.Length; i++)
        {
            stopwatch.Restart();
            Query(service, snapshot, @case, source.Line, source.Character);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return new SemanticBenchmarkCaseResult(
            @case.Name,
            passed,
            expected.Length,
            actualKeys.Length,
            first,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[^1],
            missing);
    }

    private static IReadOnlyList<SemanticLocation> Query(
        SemanticNavigationService service,
        SemanticSnapshotIdentity snapshot,
        SemanticBenchmarkCase @case,
        int line,
        int character) =>
        @case.Operation switch
        {
            SemanticBenchmarkOperation.Definition => service.GoToDefinition(
                @case.Source.Path, line, character, snapshot),
            SemanticBenchmarkOperation.References => service.FindReferences(
                @case.Source.Path, line, character, @case.IncludeDefinition, snapshot),
            SemanticBenchmarkOperation.Implementations => service.FindImplementations(
                @case.Source.Path, line, character, snapshot),
            SemanticBenchmarkOperation.RelationshipsIncoming => service.FindRelationships(
                @case.Source.Path, line, character,
                SemanticRelationshipDirection.Incoming, @case.RelationshipKind, snapshot)
                .Select(result => result.Location).ToList(),
            SemanticBenchmarkOperation.RelationshipsOutgoing => service.FindRelationships(
                @case.Source.Path, line, character,
                SemanticRelationshipDirection.Outgoing, @case.RelationshipKind, snapshot)
                .Select(result => result.Location).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(@case.Operation)),
        };

    private static (int Line, int Character) ResolveMarker(
        string root,
        SemanticBenchmarkMarker marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker.Marker);
        if (marker.Occurrence < 0 || marker.CharacterOffset < 0 ||
            marker.CharacterOffset >= marker.Marker.Length)
        {
            throw new InvalidDataException(
                $"Benchmark marker '{marker.Path}' has an invalid occurrence or offset.");
        }

        if (!SafeSourcePath.TryResolveFile(root, marker.Path, out var fullPath, out var failure))
        {
            throw new InvalidDataException(
                $"Benchmark marker path '{marker.Path}' is invalid: {failure}.");
        }

        var text = File.ReadAllText(fullPath);
        var offset = -1;
        for (var i = 0; i <= marker.Occurrence; i++)
        {
            offset = text.IndexOf(marker.Marker, offset + 1, StringComparison.Ordinal);
            if (offset < 0)
            {
                throw new InvalidDataException(
                    $"Marker '{marker.Marker}' occurrence {marker.Occurrence} was not found in '{marker.Path}'.");
            }
        }

        var position = SourceText.From(text).Lines.GetLinePosition(offset + marker.CharacterOffset);
        return (position.Line, position.Character);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
