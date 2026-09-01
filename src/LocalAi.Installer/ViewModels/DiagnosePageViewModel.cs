using System.Collections.ObjectModel;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Shows what was actually found on the machine. The detector already collects the operating
/// system, disk, network, WinGet, Git, Ollama and every graphics adapter, so the page reports
/// all of it rather than reducing the environment to a single boolean.
/// </summary>
public sealed class DiagnosePageViewModel : ObservableObject
{
    private bool isSupported;
    private string? unsupportedReason;
    private bool hasUsableAdapter;
    private bool hasGitHubSignIn;
    private bool isChecking = true;

    public ObservableCollection<EnvironmentCheck> Checks { get; } = [];

    /// <summary>
    /// True while the environment is being probed. Detection launches winget, git and ollama
    /// and takes several seconds; without this the first page sat empty and looked frozen.
    /// </summary>
    public bool IsChecking
    {
        get => isChecking;
        set
        {
            SetProperty(ref isChecking, value);
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public bool HasResults => !IsChecking;

    public bool IsSupported
    {
        get => isSupported;
        set
        {
            SetProperty(ref isSupported, value);
            OnPropertyChanged(nameof(CanContinue));
        }
    }

    public string? UnsupportedReason
    {
        get => unsupportedReason;
        set => SetProperty(ref unsupportedReason, value);
    }

    /// <summary>
    /// Drives the hint on the residency page. A missing adapter never blocks installation —
    /// it only means the strict residency policy would refuse to load any model.
    /// </summary>
    public bool HasUsableAdapter
    {
        get => hasUsableAdapter;
        private set => SetProperty(ref hasUsableAdapter, value);
    }

    /// <summary>
    /// Whether the GitHub CLI on this machine is signed in. Reported, not required: releases
    /// are public and are read over plain HTTPS, so this only says whether the fallback path
    /// is available — for a network that blocks the release host, or a fork kept private.
    /// </summary>
    public bool HasGitHubSignIn
    {
        get => hasGitHubSignIn;
        private set => SetProperty(ref hasGitHubSignIn, value);
    }

    /// <summary>
    /// Blocked while the check runs: continuing on results that are not in yet would show
    /// the next pages an environment nobody has looked at.
    /// </summary>
    public bool CanContinue => IsSupported && !IsChecking;

    public void SetResult(bool supported, string? reason = null)
    {
        IsSupported = supported;
        UnsupportedReason = supported ? null : reason;
    }

    public void Load(EnvironmentDiagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);

        Checks.Clear();
        Checks.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Operating system", "Операционная система"),
            diagnosis.OperatingSystem.OperatingSystemSupport == SupportStatus.Supported &&
            diagnosis.OperatingSystem.ArchitectureSupport == SupportStatus.Supported
                ? CheckStatus.Ok
                : CheckStatus.Blocking,
            $"{diagnosis.OperatingSystem.ProductName} ({diagnosis.OperatingSystem.Architecture})"));

        Checks.Add(Describe(
            diagnosis.WinGet,
            InstallerCulture.Pick(
                "Needed to install Git and Ollama automatically.",
                "Нужен, чтобы установить Git и Ollama автоматически.")));
        Checks.Add(Describe(
            diagnosis.Git,
            InstallerCulture.Pick(
                "Needed to index repositories.",
                "Нужен, чтобы индексировать репозитории.")));
        Checks.Add(Describe(
            diagnosis.GitHubCli,
            InstallerCulture.Pick(
                "Optional. Releases are downloaded over plain HTTPS; this is only used as "
                + "a fallback, or for a repository kept private.",
                "Необязателен. Релизы скачиваются по обычному HTTPS; он "
                + "нужен только как запасной путь или для закрытого репозитория.")));

        // Reported, never demanded. Signing in changes nothing for a public release, so
        // this line exists to answer "is my gh usable" for the fallback path and for a
        // private fork — not to make anyone feel unprepared for an ordinary install.
        HasGitHubSignIn = diagnosis.GitHubSignIn.State == DependencyState.Detected;
        Checks.Add(new EnvironmentCheck(
            InstallerCulture.Pick("GitHub sign-in", "Вход в GitHub"),
            CheckStatus.Ok,
            HasGitHubSignIn
                ? diagnosis.GitHubSignIn.Version ??
                    InstallerCulture.Pick("signed in", "выполнен")
                : InstallerCulture.Pick(
                    "not signed in — not required, releases are public",
                    "вход не выполнен — не требуется, релизы публичные")));

        var usableAdapters = diagnosis.Gpu.Adapters
            .Where(adapter => !adapter.IsSoftware)
            .ToArray();
        HasUsableAdapter = usableAdapters.Any(adapter => adapter.DedicatedLocalBytes > 0);
        Checks.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Graphics adapters", "Видеоадаптеры"),
            HasUsableAdapter ? CheckStatus.Ok : CheckStatus.Warning,
            diagnosis.Gpu.Adapters.Count > 0
                ? string.Join("; ", diagnosis.Gpu.Adapters.Select(Describe))
                : diagnosis.Gpu.Reason ??
                    InstallerCulture.Pick("No adapter reported.", "Адаптеры не обнаружены.")));

        Checks.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Free disk space", "Свободное место на диске"),
            diagnosis.Disk.AvailableBytes is > 0 and var free && free >= 8L * 1024 * 1024 * 1024
                ? CheckStatus.Ok
                : CheckStatus.Warning,
            diagnosis.Disk.AvailableBytes is { } bytes
                ? string.Format(
                    InstallerCulture.Pick("{0:N1} GB available", "{0:N1} ГБ свободно"),
                    bytes / (1024d * 1024 * 1024))
                : diagnosis.Disk.Reason ?? InstallerCulture.Pick("unknown", "неизвестно")));

        Checks.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Network", "Сеть"),
            diagnosis.Network.State == ObservationState.Available
                ? CheckStatus.Ok
                : CheckStatus.Warning,
            diagnosis.Network.State == ObservationState.Available
                ? InstallerCulture.Pick("reachable", "доступна")
                : diagnosis.Network.Reason ??
                    InstallerCulture.Pick("not reachable", "недоступна")));

        Checks.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Existing LocalAi", "Установленный LocalAi"),
            diagnosis.ExistingLocalAi.State switch
            {
                ExistingLocalAiState.Absent => CheckStatus.Ok,
                ExistingLocalAiState.Compatible => CheckStatus.Ok,
                _ => CheckStatus.Warning,
            },
            diagnosis.ExistingLocalAi.State switch
            {
                ExistingLocalAiState.Absent => InstallerCulture.Pick(
                    "none — this will be a first installation",
                    "нет — это будет первая установка"),
                ExistingLocalAiState.Compatible => string.Format(
                    InstallerCulture.Pick("version {0} at {1}", "версия {0} в {1}"),
                    diagnosis.ExistingLocalAi.Version,
                    diagnosis.ExistingLocalAi.VersionPath),
                _ => diagnosis.ExistingLocalAi.Reason ?? InstallerCulture.Pick(
                    "present but not recognised",
                    "есть, но не распознан"),
            }));

        SetResult(
            diagnosis.IsSupported,
            diagnosis.UnsupportedReasons.Count == 0
                ? null
                : string.Join("; ", diagnosis.UnsupportedReasons));

        OnPropertyChanged(nameof(Checks));
    }

    private static string Describe(GpuAdapterSnapshot adapter) =>
        GpuAdapterDisplay.Describe(adapter);

    private static EnvironmentCheck Describe(DependencySnapshot snapshot, string purpose) =>
        new(
            snapshot.Name,
            snapshot.State == DependencyState.Detected ? CheckStatus.Ok : CheckStatus.Missing,
            snapshot.State == DependencyState.Detected
                ? string.Join(
                    " — ",
                    new[] { snapshot.Version, snapshot.ExecutablePath }
                        .Where(part => !string.IsNullOrWhiteSpace(part)))
                : snapshot.Reason is { Length: > 0 } reason
                    ? $"{purpose} ({reason})"
                    : purpose);
}
