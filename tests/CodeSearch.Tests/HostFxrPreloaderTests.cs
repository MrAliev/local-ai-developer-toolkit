using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class HostFxrPreloaderTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-hostfxr-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_newest_version_wins()
    {
        WriteHostFxr("9.0.14");
        WriteHostFxr("10.0.11");
        WriteHostFxr("10.0.7");

        var found = HostFxrPreloader.FindNewestHostFxr(root);

        Assert.Equal(HostFxrPath("10.0.11"), found);
    }

    [Fact]
    public void A_stable_version_outranks_its_own_prerelease()
    {
        WriteHostFxr("10.0.11-rc.1.25451.7");
        WriteHostFxr("10.0.11");

        var found = HostFxrPreloader.FindNewestHostFxr(root);

        Assert.Equal(HostFxrPath("10.0.11"), found);
    }

    [Fact]
    public void A_version_directory_without_the_library_is_not_a_candidate()
    {
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", "99.0.0"));
        WriteHostFxr("10.0.7");

        var found = HostFxrPreloader.FindNewestHostFxr(root);

        Assert.Equal(HostFxrPath("10.0.7"), found);
    }

    [Fact]
    public void A_directory_that_is_not_a_version_is_skipped()
    {
        WriteHostFxr("garbage");

        Assert.Null(HostFxrPreloader.FindNewestHostFxr(root));
    }

    [Fact]
    public void A_root_without_host_fxr_yields_nothing()
    {
        Directory.CreateDirectory(root);

        Assert.Null(HostFxrPreloader.FindNewestHostFxr(root));
    }

    [Fact]
    public void Environment_overrides_come_before_the_resolved_dotnet_and_the_default()
    {
        var roots = HostFxrPreloader.CandidateDotnetRoots(
            name => name switch
            {
                "DOTNET_ROOT" => @"C:\env-root",
                _ when name.StartsWith("DOTNET_ROOT_", StringComparison.Ordinal) =>
                    @"C:\arch-root",
                _ => null,
            },
            @"C:\on-path\dotnet.exe",
            @"C:\default\dotnet");

        Assert.Equal(
            [@"C:\arch-root", @"C:\env-root", @"C:\on-path", @"C:\default\dotnet"],
            roots);
    }

    [Fact]
    public void A_root_reachable_two_ways_is_probed_once()
    {
        var roots = HostFxrPreloader.CandidateDotnetRoots(
            name => name == "DOTNET_ROOT" ? @"C:\Program Files\dotnet" : null,
            @"C:\PROGRAM FILES\dotnet\dotnet.exe",
            @"C:\Program Files\dotnet");

        Assert.Equal([@"C:\Program Files\dotnet"], roots);
    }

    /// <summary>
    /// The conservatism #188 promises: on a machine where "hostfxr" already resolves — and
    /// a framework-dependent test host has the module loaded before the first managed
    /// instruction — the preloader must do nothing and say nothing.
    /// </summary>
    [Fact]
    public void A_process_that_already_resolves_hostfxr_is_left_alone()
    {
        var diagnostics = new List<string>();

        HostFxrPreloader.EnsureLoaded(diagnostics.Add);

        Assert.Empty(diagnostics);
    }

    private string HostFxrPath(string version) =>
        Path.Combine(root, "host", "fxr", version, "hostfxr.dll");

    private void WriteHostFxr(string version)
    {
        var directory = Path.Combine(root, "host", "fxr", version);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "hostfxr.dll"), []);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
