using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace LocalAi.ReleaseSigner;

/// <summary>
/// Custody of the release signing key on the publishing machine (#210/R2).
///
/// The key used to live as an unencrypted PKCS#8 file readable by anything running in the
/// signed-in session, and by anything that could read the profile at rest — a backup, a
/// lifted disk, another account with a misconfigured ACL. The working copy is now a
/// DPAPI-wrapped blob bound to this Windows account on this machine, and every use first
/// verifies that the key directory's ACL grants access to nobody beyond SYSTEM,
/// Administrators and the current user. The offline out-of-band backup that the signing
/// runbook mandates stays the raw PKCS#8: a DPAPI blob does not travel across accounts or
/// machines, so the backup must never be the protected form.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ReleaseSigningKeyStore
{
    public const string RawKeyFileName = "release-signing-private.pkcs8.der";
    public const string ProtectedKeyFileName = "release-signing-private.pkcs8.dpapi";

    // Entropy binds the blob to its purpose: a DPAPI blob lifted from some other
    // application under the same account does not unprotect here, and this blob does not
    // unprotect there.
    private static readonly byte[] Entropy =
        "LocalAi.ReleaseSigner release-signing-private"u8.ToArray();

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi",
        "release-signing");

    /// <summary>
    /// Loads the signing key: an explicit path by its extension, otherwise the protected
    /// working copy, otherwise the raw file with a warning that names the fix.
    /// </summary>
    public static ECDsa Load(string? explicitPath, string directory, TextWriter output)
    {
        var (path, isProtected) = Locate(explicitPath, directory);
        VerifyDirectoryAcl(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var pkcs8 = isProtected
            ? Unprotect(File.ReadAllBytes(path), path)
            : File.ReadAllBytes(path);
        if (!isProtected)
        {
            output.WriteLine(
                $"WARNING: {path} is an unencrypted private key. " +
                "Run `localai-release-signer protect-key` to replace the working copy " +
                "with a DPAPI-wrapped one.");
        }

        try
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(pkcs8, out _);
            }
            catch (CryptographicException error)
            {
                key.Dispose();
                throw new InvalidOperationException(
                    $"The release signing key at {path} does not parse as a PKCS#8 " +
                    "private key.",
                    error);
            }

            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// Converts the raw working copy into the DPAPI-wrapped one: validate, wrap, verify
    /// the round trip, then scrub and delete the raw file. The scrub is best effort — an
    /// SSD or NTFS may keep older copies of the sectors — which is why the runbook's
    /// offline backup, not this file, is the custody copy of record.
    /// </summary>
    public static int Protect(string? explicitRawPath, string directory, TextWriter output)
    {
        var rawPath = explicitRawPath ?? Path.Combine(directory, RawKeyFileName);
        var protectedPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(rawPath))!,
            ProtectedKeyFileName);
        if (File.Exists(protectedPath))
        {
            throw new InvalidOperationException(
                $"{protectedPath} already exists. To rotate, delete it deliberately and " +
                "run protect-key again on the raw key.");
        }

        if (!File.Exists(rawPath))
        {
            throw new InvalidOperationException(
                $"No raw key at {rawPath}. Nothing to protect; the signing runbook " +
                "describes how to generate or restore one.");
        }

        VerifyDirectoryAcl(Path.GetDirectoryName(Path.GetFullPath(rawPath))!);
        var raw = File.ReadAllBytes(rawPath);
        try
        {
            string fingerprint;
            using (var key = ECDsa.Create())
            {
                key.ImportPkcs8PrivateKey(raw, out _);
                fingerprint = Convert.ToHexString(
                    SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
            }

            File.WriteAllBytes(
                protectedPath,
                ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser));

            // Read the wrap back before destroying anything: a blob that does not
            // round-trip must fail while the raw file is still whole.
            var restored = Unprotect(File.ReadAllBytes(protectedPath), protectedPath);
            var same = restored.AsSpan().SequenceEqual(raw);
            CryptographicOperations.ZeroMemory(restored);
            if (!same)
            {
                File.Delete(protectedPath);
                throw new InvalidOperationException(
                    "The DPAPI round trip did not reproduce the key; the raw file was " +
                    "left untouched.");
            }

            File.WriteAllBytes(rawPath, new byte[raw.Length]);
            File.Delete(rawPath);

            output.WriteLine($"key       : {protectedPath}");
            output.WriteLine($"spki-sha256: {fingerprint}");
            output.WriteLine(
                "The raw file was overwritten and deleted. The offline backup remains " +
                "the raw PKCS#8 — a DPAPI blob does not travel to another account or " +
                "machine.");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static (string Path, bool IsProtected) Locate(
        string? explicitPath,
        string directory)
    {
        if (explicitPath is not null)
        {
            return (explicitPath, explicitPath.EndsWith(
                ".dpapi",
                StringComparison.OrdinalIgnoreCase));
        }

        var protectedPath = Path.Combine(directory, ProtectedKeyFileName);
        if (File.Exists(protectedPath))
        {
            return (protectedPath, true);
        }

        var rawPath = Path.Combine(directory, RawKeyFileName);
        if (File.Exists(rawPath))
        {
            return (rawPath, false);
        }

        throw new InvalidOperationException(
            $"No release signing key in {directory} — neither {ProtectedKeyFileName} " +
            $"nor {RawKeyFileName}. The signing runbook describes how to generate or " +
            "restore one.");
    }

    private static byte[] Unprotect(byte[] blob, string path)
    {
        try
        {
            return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException error)
        {
            throw new InvalidOperationException(
                $"Could not unwrap {path}. DPAPI blobs are bound to the Windows account " +
                "and machine that created them — on a new machine or account, restore " +
                "the raw key from the offline backup and run protect-key again.",
                error);
        }
    }

    /// <summary>
    /// Refuses to touch the key while its directory grants access beyond SYSTEM,
    /// Administrators and the current user. An ACL is the easiest thing to widen by
    /// accident — a share, a sync tool, an over-broad grant — and the cheapest to check.
    /// CREATOR OWNER is tolerated: files the current user creates here resolve to the
    /// current user.
    /// </summary>
    private static void VerifyDirectoryAcl(string directory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows identity has no SID; cannot verify key custody.");
        var allowed = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
            currentUser,
        };

        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in new DirectoryInfo(directory)
                     .GetAccessControl(AccessControlSections.Access)
                     .GetAccessRules(
                         includeExplicit: true,
                         includeInherited: true,
                         typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            var identity = (SecurityIdentifier)rule.IdentityReference;
            if (allowed.Any(identity.Equals))
            {
                continue;
            }

            // FullControl, Modify, Read and Write all contain one of these two bits, so
            // this catches every grant that can reach the key's bytes while ignoring
            // trivia like Synchronize-only entries.
            if ((rule.FileSystemRights &
                 (FileSystemRights.ReadData | FileSystemRights.WriteData)) == 0)
            {
                continue;
            }

            offenders.Add(DisplayName(identity));
        }

        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(
                $"The key directory {directory} is readable beyond SYSTEM, " +
                $"Administrators and the current user: {string.Join(", ", offenders)}. " +
                "Refusing to touch the signing key. Restrict it, for example: " +
                $"icacls \"{directory}\" /inheritance:r " +
                "/grant:r \"%USERNAME%:(OI)(CI)F\" \"SYSTEM:(OI)(CI)F\" " +
                "\"Administrators:(OI)(CI)F\"");
        }
    }

    private static string DisplayName(SecurityIdentifier identity)
    {
        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return identity.Value;
        }
    }
}
