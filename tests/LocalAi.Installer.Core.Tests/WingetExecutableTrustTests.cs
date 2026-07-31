using LocalAi.Installer.Core.Dependencies;

namespace LocalAi.Installer.Core.Tests;

public sealed class WingetExecutableTrustTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string WindowsApps = @"C:\Program Files\WindowsApps";
    private const string PackageFullName =
        "Microsoft.DesktopAppInstaller_1.29.279.0_x64__8wekyb3d8bbwe";
    private const string PackageFamilyName =
        "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    private const string PackageRoot =
        @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.279.0_x64__8wekyb3d8bbwe";
    private const string PhysicalWinget = PackageRoot + @"\winget.exe";
    private const string Alias =
        @"C:\Users\test\AppData\Local\Microsoft\WindowsApps\winget.exe";

    [Fact]
    public void Microsoft_signed_copy_in_user_writable_location_is_rejected()
    {
        const string copiedWinget = @"C:\Users\test\Downloads\winget.exe";
        var platform = TrustedPlatform();
        platform.Inspections[copiedWinget] = TrustedInspection(copiedWinget);
        var trust = new WindowsWingetExecutableTrust(platform);

        var result = trust.Resolve(copiedWinget);

        Assert.Equal(ExecutableTrustStatus.InvalidPath, result.Status);
        Assert.Null(result.Executable);
        Assert.Empty(platform.InspectedPaths);
    }

    [Theory]
    [InlineData(@"C:\Users\test\FakeWindowsApps")]
    [InlineData(@"D:\WindowsApps")]
    public void Registry_package_root_outside_canonical_ProgramFiles_WindowsApps_is_rejected(
        string spoofedRoot)
    {
        var platform = TrustedPlatform();
        platform.PackageSets =
        [
            [new RegisteredWingetPackage(
                PackageFullName,
                PackageFamilyName,
                spoofedRoot)],
        ];
        var trust = new WindowsWingetExecutableTrust(platform);

        var result = trust.Resolve(Alias);

        Assert.Equal(ExecutableTrustStatus.InvalidPath, result.Status);
        Assert.Null(result.Executable);
        Assert.Empty(platform.InspectedPaths);
    }

    [Theory]
    [InlineData("Microsoft.DesktopAppInstaller_1.29.279.0_x64__evil")]
    [InlineData("Microsoft.DesktopAppInstaller_1.29.279.0_neutral__8wekyb3d8bbwe")]
    [InlineData("Microsoft.DesktopAppInstaller_1.29_x64__8wekyb3d8bbwe")]
    public void Spoofed_package_full_name_or_publisher_identity_is_rejected(
        string packageFullName)
    {
        var platform = TrustedPlatform();
        platform.PackageSets =
        [
            [new RegisteredWingetPackage(
                packageFullName,
                PackageFamilyName,
                PackageRoot)],
        ];
        var trust = new WindowsWingetExecutableTrust(platform);

        var result = trust.Resolve(Alias);

        Assert.Equal(ExecutableTrustStatus.InvalidPath, result.Status);
        Assert.Null(result.Executable);
    }

    [Theory]
    [InlineData(WindowsApps)]
    [InlineData(PackageRoot)]
    [InlineData(PhysicalWinget)]
    public void Writable_load_root_package_or_executable_is_rejected(
        string writablePath)
    {
        var platform = TrustedPlatform();
        platform.ProtectedPaths[writablePath] = false;
        var trust = new WindowsWingetExecutableTrust(platform);

        var result = trust.Resolve(Alias);

        Assert.Equal(ExecutableTrustStatus.UntrustedAcl, result.Status);
        Assert.Null(result.Executable);
    }

    [Fact]
    public void Trusted_registered_package_binds_exact_canonical_identity()
    {
        var platform = TrustedPlatform();
        var trust = new WindowsWingetExecutableTrust(platform);

        var result = trust.Resolve(Alias);

        Assert.Equal(ExecutableTrustStatus.Trusted, result.Status);
        Assert.Equal(PhysicalWinget, result.Executable!.CanonicalPath);
        Assert.Equal("SHA256:ABC;SIGNER:DEF", result.Executable.Identity);
        Assert.Equal("Microsoft Corporation", result.Executable.Publisher);
        Assert.Equal([PhysicalWinget], platform.InspectedPaths);
        Assert.Equal(1, platform.SignatureVerificationCount);
    }

    [Fact]
    public void Changed_registered_package_root_is_rejected_on_revalidation()
    {
        const string changedFullName =
            "Microsoft.DesktopAppInstaller_1.30.1.0_x64__8wekyb3d8bbwe";
        const string changedRoot =
            @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.30.1.0_x64__8wekyb3d8bbwe";
        var platform = TrustedPlatform();
        platform.PackageSets =
        [
            [RegisteredPackage()],
            [new RegisteredWingetPackage(
                changedFullName,
                PackageFamilyName,
                changedRoot)],
        ];
        platform.ProtectedPaths[changedRoot] = true;
        platform.ProtectedPaths[changedRoot + @"\winget.exe"] = true;
        platform.Inspections[changedRoot + @"\winget.exe"] =
            TrustedInspection(changedRoot + @"\winget.exe");
        var trust = new WindowsWingetExecutableTrust(platform);
        var initial = trust.Resolve(Alias);

        var result = trust.Revalidate(initial.Executable!);

        Assert.Equal(ExecutableTrustStatus.Changed, result.Status);
        Assert.Null(result.Executable);
    }

    private static FakeWindowsWingetTrustPlatform TrustedPlatform()
    {
        var platform = new FakeWindowsWingetTrustPlatform
        {
            PackageSets = [[RegisteredPackage()]],
        };
        platform.ProtectedPaths[WindowsApps] = true;
        platform.ProtectedPaths[PackageRoot] = true;
        platform.ProtectedPaths[PhysicalWinget] = true;
        platform.Inspections[PhysicalWinget] = TrustedInspection(PhysicalWinget);
        return platform;
    }

    private static RegisteredWingetPackage RegisteredPackage() =>
        new(PackageFullName, PackageFamilyName, PackageRoot);

    private static WingetExecutableInspection TrustedInspection(string path) =>
        new(
            ExecutableTrustStatus.Trusted,
            path,
            "SHA256:ABC;SIGNER:DEF",
            "Microsoft Corporation");

    private sealed class FakeWindowsWingetTrustPlatform : IWindowsWingetTrustPlatform
    {
        private int _packageSetIndex;

        public string ProgramFilesPath => ProgramFiles;
        public string LocalAppDataPath => @"C:\Users\test\AppData\Local";
        public IReadOnlyList<IReadOnlyList<RegisteredWingetPackage>> PackageSets { get; set; } = [];
        public Dictionary<string, bool> ProtectedPaths { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, WingetExecutableInspection> Inspections { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<string> InspectedPaths { get; } = [];
        public int SignatureVerificationCount { get; private set; }

        public IReadOnlyList<RegisteredWingetPackage> GetRegisteredPackages()
        {
            var index = Math.Min(_packageSetIndex, PackageSets.Count - 1);
            _packageSetIndex++;
            return PackageSets[index];
        }

        public bool IsProtectedPath(string path) =>
            ProtectedPaths.TryGetValue(path, out var isProtected) &&
            isProtected;

        public WingetExecutableInspection InspectExecutable(string path)
        {
            InspectedPaths.Add(path);
            SignatureVerificationCount++;
            return Inspections[path];
        }
    }
}
