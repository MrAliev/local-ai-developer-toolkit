using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class RoslynBuildHostLeaseTests
{
    [Fact]
    public void Embedded_build_hosts_are_extracted_and_removed_with_the_lease()
    {
        string root;
        using (var lease = Assert.IsType<RoslynBuildHostLease>(
                   RoslynBuildHostLease.CreateIfNeeded(forceExtraction: true)))
        {
            root = lease.RootPath;
            Assert.True(File.Exists(Path.Combine(
                root,
                "BuildHost-netcore",
                "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll")));
            Assert.True(File.Exists(Path.Combine(
                root,
                "BuildHost-net472",
                "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe")));
        }

        Assert.False(Directory.Exists(root));
    }
}
