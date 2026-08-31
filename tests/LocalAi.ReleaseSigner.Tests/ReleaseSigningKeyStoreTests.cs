using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace LocalAi.ReleaseSigner.Tests;

/// <summary>
/// Custody of the release signing key (#210/R2): the working copy converts to a
/// DPAPI-wrapped blob and the raw file is destroyed; loading prefers the protected copy
/// and warns on a raw one; and nothing touches the key while its directory is readable
/// beyond SYSTEM, Administrators and the current user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReleaseSigningKeyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "localai-keystore-" + Guid.NewGuid().ToString("N"));

    public ReleaseSigningKeyStoreTests()
    {
        // A canonical restricted ACL rather than whatever %TEMP% inherits: on machines
        // where sandbox tooling grants extra groups on the profile, an inherited ACL
        // legitimately fails the custody check — which is the check doing its job, and
        // exactly why these tests must own their directory's ACL explicitly.
        Directory.CreateDirectory(_directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in new[]
                 {
                     WindowsIdentity.GetCurrent().User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(_directory).SetAccessControl(security);
    }

    [Fact]
    public void Protect_wraps_the_key_destroys_the_raw_file_and_load_prefers_the_wrap()
    {
        var fingerprint = WriteRawKey();
        var output = new StringWriter();

        var exitCode = ReleaseSigningKeyStore.Protect(
            explicitRawPath: null,
            _directory,
            output);

        Assert.Equal(0, exitCode);
        Assert.False(
            File.Exists(Path.Combine(_directory, ReleaseSigningKeyStore.RawKeyFileName)));
        Assert.True(
            File.Exists(Path.Combine(
                _directory,
                ReleaseSigningKeyStore.ProtectedKeyFileName)));
        Assert.Contains(fingerprint, output.ToString(), StringComparison.Ordinal);

        var loadOutput = new StringWriter();
        using var loaded = ReleaseSigningKeyStore.Load(null, _directory, loadOutput);
        Assert.Equal(
            fingerprint,
            Convert.ToHexString(SHA256.HashData(loaded.ExportSubjectPublicKeyInfo())));
        Assert.DoesNotContain("WARNING", loadOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_a_raw_key_works_but_names_the_fix()
    {
        var fingerprint = WriteRawKey();
        var output = new StringWriter();

        using var loaded = ReleaseSigningKeyStore.Load(null, _directory, output);

        Assert.Equal(
            fingerprint,
            Convert.ToHexString(SHA256.HashData(loaded.ExportSubjectPublicKeyInfo())));
        Assert.Contains("protect-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_refuses_to_clobber_an_existing_protected_key()
    {
        WriteRawKey();
        File.WriteAllBytes(
            Path.Combine(_directory, ReleaseSigningKeyStore.ProtectedKeyFileName),
            [1, 2, 3]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReleaseSigningKeyStore.Protect(null, _directory, TextWriter.Null));

        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(_directory, ReleaseSigningKeyStore.RawKeyFileName)),
            "the raw key must be left untouched");
    }

    [Fact]
    public void A_blob_from_another_account_points_at_the_offline_backup()
    {
        // Garbage stands in for a blob DPAPI cannot unwrap here — the shape a protected
        // key has after being copied to a different account or machine.
        File.WriteAllBytes(
            Path.Combine(_directory, ReleaseSigningKeyStore.ProtectedKeyFileName),
            new byte[64]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReleaseSigningKeyStore.Load(null, _directory, TextWriter.Null));

        Assert.Contains("offline backup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_key_names_the_runbook_not_a_file_error()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ReleaseSigningKeyStore.Load(null, _directory, TextWriter.Null));

        Assert.Contains("runbook", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_readable_by_everyone_refuses_both_load_and_protect()
    {
        WriteRawKey();
        var info = new DirectoryInfo(_directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        info.SetAccessControl(security);

        var loadError = Assert.Throws<InvalidOperationException>(() =>
            ReleaseSigningKeyStore.Load(null, _directory, TextWriter.Null));
        var protectError = Assert.Throws<InvalidOperationException>(() =>
            ReleaseSigningKeyStore.Protect(null, _directory, TextWriter.Null));

        Assert.Contains("icacls", loadError.Message, StringComparison.Ordinal);
        Assert.Contains("icacls", protectError.Message, StringComparison.Ordinal);
    }

    private string WriteRawKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllBytes(
            Path.Combine(_directory, ReleaseSigningKeyStore.RawKeyFileName),
            key.ExportPkcs8PrivateKey());
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
