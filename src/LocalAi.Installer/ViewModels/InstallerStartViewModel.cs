using System.Collections.ObjectModel;
using System.IO;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>What the person came here to do. One executable, four errands.</summary>
public enum StartChoice
{
    Install,

    /// <summary>Put the current release in place over what is already there, keeping everything.</summary>
    UpdateOrRepair,

    /// <summary>
    /// Clear the binaries and the transient state, keep the indexes and settings a fresh
    /// install would honour, and install again. This is the reinstall-friendly row of the
    /// removal matrix followed by an installation — not a separate mechanism.
    /// </summary>
    CleanReinstall,

    Remove,
}

/// <summary>
/// One option on the start page: what it does, and — when it cannot be done here — why not.
/// A greyed-out button with no explanation is a worse answer than no button at all.
/// </summary>
public sealed class StartActionOption(
    StartChoice Choice,
    string Title,
    string Description,
    bool IsAvailable,
    string UnavailableReason) : ObservableObject
{
    private bool isSelected;

    public StartChoice Choice { get; } = Choice;

    public string Title { get; } = Title;

    public string Description { get; } = Description;

    public bool IsAvailable { get; } = IsAvailable;

    public string UnavailableReason { get; } = UnavailableReason;

    public bool IsUnavailable => !IsAvailable;

    /// <summary>
    /// Whether this is the errand the button at the bottom would run. One row at a time: the
    /// view model releases the others, which is what makes four rows one question.
    /// </summary>
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    /// <summary>
    /// What a screen reader announces. A disabled row's reason is not optional information, and
    /// help text is only spoken at some verbosity levels, so it goes into the name itself.
    /// </summary>
    public string AccessibleName => IsAvailable
        ? Title
        : Title + ". " + UnavailableReason;
}

/// <summary>
/// The first thing the installer shows, because it is also the uninstaller, the updater and
/// the repair tool. Which of those it can be depends on what is already on the machine, so it
/// looks first and offers second.
/// </summary>
public sealed class InstallerStartViewModel : ObservableObject
{
    private readonly ExistingLocalAiSnapshot existing;

    /// <summary>
    /// Which release is installed, as opposed to which directory holds it. The inspector reports
    /// the directory from the pointer and never asks — so this screen said
    /// "LocalAi 467ed5f0f9bf is installed" while the next window, doctor and update all said
    /// 0.1.51, each reading the release record this one did not.
    /// </summary>
    private readonly InstalledVersion installed;

    private readonly InstallerPreferencesStore preferences;

    public InstallerStartViewModel(
        string? localAppData = null,
        IExistingLocalAiInspector? inspector = null,
        Func<InstalledVersion>? readInstalledVersion = null,
        InstallerPreferencesStore? preferencesStore = null)
    {
        var root = localAppData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Rooted where the caller said its state lives. Defaulting to the real profile even
        // when the rest of the view model was redirected let the test suite rewrite the
        // language of the installer actually installed on the machine, once per run.
        preferences = preferencesStore ?? new InstallerPreferencesStore(
            Path.Combine(root, RemovalMatrix.JournalDirectoryName));
        existing = (inspector ?? new ExistingLocalAiInspector(new SystemFileSystemProbe()))
            .Inspect(root);
        installed = readInstalledVersion is null
            ? InstalledVersionReader.Read(Path.Combine(root, "LocalAi"))
            : readInstalledVersion();
        Theme = preferences.ReadTheme();
        foreach (var option in BuildOptions())
        {
            Actions.Add(option);
        }

        SelectTheOnlyErrandIfThereIsOne();
    }

    public ObservableCollection<StartActionOption> Actions { get; } = [];

    public ExistingLocalAiState State => existing.State;

    public string? InstalledVersion => existing.Version;

    /// <summary>
    /// The release this installation came from, with a leading space, or nothing when it did
    /// not record one. The build id is not a substitute: it answers a question nobody has asked
    /// yet, in the sentence that has to be legible at a glance.
    /// </summary>
    private string Release =>
        installed.ReleaseVersion is { Length: > 0 } release ? " " + release : string.Empty;

    public string Headline => existing.State switch
    {
        ExistingLocalAiState.Compatible => InstallerCulture.Pick(
            "LocalAi" + Release + " is installed on this computer.",
            "LocalAi" + Release + " установлен на этом компьютере."),
        ExistingLocalAiState.Unrecognized => InstallerCulture.Pick(
            "There is a LocalAi directory here, but it is not a working installation.",
            "Каталог LocalAi здесь есть, но рабочей установкой он не является."),
        _ => InstallerCulture.Pick(
            "LocalAi is not installed on this computer.",
            "LocalAi не установлен на этом компьютере."),
    };

    /// <summary>
    /// Takes effect here, on this screen, rather than from the next window: the alternative is
    /// an installer whose first act after being told which language you read is to keep using
    /// the other one.
    /// </summary>
    public void ChooseLanguage(InstallerLanguage language)
    {
        InstallerCulture.Current = language;
        preferences.WriteLanguage(language);
        // The rows are rebuilt in the other language, so the choice has to be carried across
        // them: somebody who picked an errand and then switched language would otherwise watch
        // it un-choose itself.
        var chosen = Selected;
        Selected = null;
        Actions.Clear();
        foreach (var option in BuildOptions())
        {
            Actions.Add(option);
        }

        if (chosen is { } errand)
        {
            Select(errand);
        }
        else
        {
            SelectTheOnlyErrandIfThereIsOne();
        }

        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(ActionsGroupName));
        OnPropertyChanged(nameof(ThemeSystemText));
        OnPropertyChanged(nameof(ThemeLightText));
        OnPropertyChanged(nameof(ThemeDarkText));
    }

    /// <summary>
    /// Which of the three the person has chosen. "System" is the default and is not a third
    /// colour scheme: it means the installer keeps following Windows, including a change made
    /// while it is open.
    /// </summary>
    /// <summary>
    /// The errand the button at the bottom would run, or null while nobody has chosen. The
    /// screen used to carry a button per row, so there were four primary actions on one page
    /// and no way to read the four descriptions without one of them a click from happening.
    /// </summary>
    public StartChoice? Selected { get; private set; }

    public bool HasSelection => Selected is not null;

    /// <summary>
    /// Chooses one errand and releases the rest. An errand that cannot run is refused rather
    /// than selected: its row is not reachable by mouse or keyboard, but this is what decides,
    /// and a screen reader reaches further than either.
    /// </summary>
    public void Select(StartChoice choice)
    {
        if (Actions.SingleOrDefault(option => option.Choice == choice) is not
            { IsAvailable: true } chosen)
        {
            return;
        }

        foreach (var option in Actions)
        {
            option.IsSelected = ReferenceEquals(option, chosen);
        }

        Selected = choice;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>
    /// Stated by count rather than by state: when exactly one errand can run, choosing it is
    /// not a question and the screen answers it — three greyed rows, nothing selected and a
    /// dead button is a puzzle on the easiest screen in the product. When several can, one of
    /// them deletes things, and that is where "nothing chosen for you" earns its keep.
    /// </summary>
    private void SelectTheOnlyErrandIfThereIsOne()
    {
        if (Actions.Count(option => option.IsAvailable) == 1)
        {
            Select(Actions.Single(option => option.IsAvailable).Choice);
        }
    }

    public InstallerTheme Theme { get; private set; }

    public bool IsSystemTheme => Theme == InstallerTheme.System;

    public bool IsLightTheme => Theme == InstallerTheme.Light;

    public bool IsDarkTheme => Theme == InstallerTheme.Dark;

    /// <summary>
    /// Remembers the choice, repaints the running application, and moves the selection. The
    /// application is asked rather than told: this view model also runs in tests, where there
    /// is no application to repaint.
    /// </summary>
    public void ChooseTheme(InstallerTheme theme)
    {
        Theme = theme;
        preferences.WriteTheme(theme);
        App.Themes?.Choose(theme);
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    public bool IsEnglish => InstallerCulture.Current == InstallerLanguage.English;

    public bool IsRussian => InstallerCulture.Current == InstallerLanguage.Russian;

    public string NextText => PageLabels.Next;

    public string ActionsGroupName => PageLabels.ChooseWhatToDo;

    public string ThemeSystemText => PageLabels.ThemeSystem;

    public string ThemeLightText => PageLabels.ThemeLight;

    public string ThemeDarkText => PageLabels.ThemeDark;

    public string CloseText => InstallerCulture.Pick("Close", "Закрыть");

    public string Detail => existing.State switch
    {
        // The build id lives here rather than in the headline, and only when there is no
        // release to name: somebody has to be able to answer "which one are you running", and
        // an unlabelled hash beside the product name is what read as a version.
        ExistingLocalAiState.Compatible when installed.ReleaseVersion is null &&
            installed.VersionDirectory is { Length: > 0 } build => InstallerCulture.Pick(
            $"Build {build}. This installation does not record which release it came from. " +
            "Choose what to do with it. Nothing is changed until you confirm it on a review " +
            "page.",
            $"Сборка {build}. Эта установка не хранит запись о том, из какого релиза она " +
            "получена. Выберите, что с ним сделать. Ничего не изменится, пока вы это не " +
            "подтвердите на итоговой странице."),
        ExistingLocalAiState.Compatible => InstallerCulture.Pick(
            "Choose what to do with it. Nothing is changed until you confirm it on a review " +
            "page.",
            "Выберите, что с ним сделать. Ничего не изменится, пока вы это не подтвердите на " +
            "итоговой странице."),
        // The reason is machine output, in whatever language Windows produced it, so it is
        // labelled and given its own line rather than glued into a sentence — which also fixes
        // an exception message running into the next clause without a full stop.
        ExistingLocalAiState.Unrecognized => InstallerCulture.Pick(
            (existing.Reason is { Length: > 0 } englishReason
                ? "Reason: " + englishReason
                : "The installation could not be read.") +
            "\nInstalling again repairs it; removing clears it away.",
            (existing.Reason is { Length: > 0 } reason
                ? "Причина: " + reason
                : "Установку не удалось прочитать.") +
            "\nПовторная установка это исправит; удаление — уберёт."),
        _ => InstallerCulture.Pick(
            "Install it to give your assistants local code search and local models.",
            "Установите его, чтобы дать вашим ассистентам локальный поиск по коду и локальные " +
            "модели."),
    };

    public bool HasProblem => existing.State == ExistingLocalAiState.Unrecognized;

    /// <summary>The preset the removal wizard opens on for each errand that goes there.</summary>
    public static RemovalPreset PresetFor(StartChoice choice) =>
        choice == StartChoice.CleanReinstall
            ? RemovalPreset.ReinstallFriendly
            : RemovalPreset.FullUninstall;

    public StartActionOption Option(StartChoice choice) =>
        Actions.Single(action => action.Choice == choice);

    private IEnumerable<StartActionOption> BuildOptions()
    {
        var installed = existing.State != ExistingLocalAiState.Absent;
        // The release, not the directory: the row says why Install is off, and "already
        // installed" is the answer — naming a build id there repeats the headline's old
        // mistake in smaller type.
        var version = Release;
        // The row this sentence sends the reader to is titled "Repair this installation" on an
        // unrecognised installation, so naming "Update or repair" there sent them to a label
        // that was not on the screen. Hoisted because BuildOptions yields Install first.
        var repairTitle = installed && existing.State == ExistingLocalAiState.Unrecognized
            ? InstallerCulture.Pick("Repair this installation", "Восстановить эту установку")
            : InstallerCulture.Pick("Update or repair", "Обновить или восстановить");
        yield return new StartActionOption(
            StartChoice.Install,
            InstallerCulture.Pick("Install LocalAi", "Установить LocalAi"),
            InstallerCulture.Pick(
                "Sets up the prerequisites, the runtime and the client integrations.",
                "Устанавливает необходимые компоненты, рантайм и интеграции с клиентскими " +
                "приложениями."),
            !installed,
            existing.State == ExistingLocalAiState.Compatible
                ? string.Format(
                    InstallerCulture.Pick(
                        "LocalAi{0} is already installed — choose “{1}”.",
                        "LocalAi{0} уже установлен — выберите «{1}»."),
                    version,
                    repairTitle)
                : string.Format(
                    InstallerCulture.Pick(
                        "There is already a LocalAi directory here — choose “{0}”, which " +
                        "installs over it.",
                        "Каталог LocalAi здесь уже есть — выберите «{0}»: этот вариант " +
                        "устанавливает поверх."),
                    repairTitle));
        yield return new StartActionOption(
            StartChoice.UpdateOrRepair,
            repairTitle,
            // Not "the release you choose": this path folds the release page away and resolves
            // it behind the first screen. The sentence advertised a question the wizard
            // deliberately stopped asking, which leaves the reader waiting for it.
            InstallerCulture.Pick(
                "Installs the current release over the one that is there. Indexes, settings " +
                "and client integrations are kept.",
                "Устанавливает текущий релиз поверх имеющегося. Индексы, настройки и " +
                "интеграции с клиентскими приложениями сохраняются."),
            installed,
            InstallerCulture.Pick(
                "Nothing is installed to update.",
                "Нечего обновлять — ничего не установлено."));
        yield return new StartActionOption(
            StartChoice.CleanReinstall,
            InstallerCulture.Pick("Clean reinstall", "Чистая переустановка"),
            InstallerCulture.Pick(
                "Removes the binaries and the transient state, keeps the repository indexes " +
                "and the settings a fresh install would honour, then installs again.",
                "Удаляет бинарные файлы и временное состояние, сохраняет индексы репозиториев " +
                "и настройки, которые учла бы чистая установка, и устанавливает заново."),
            installed,
            InstallerCulture.Pick(
                "Nothing is installed to reinstall.",
                "Нечего переустанавливать — ничего не установлено."));
        yield return new StartActionOption(
            StartChoice.Remove,
            InstallerCulture.Pick("Remove LocalAi", "Удалить LocalAi"),
            InstallerCulture.Pick(
                "Choose what goes and what stays, then take it off this computer.",
                "Выберите, что удалить, а что оставить, и уберите его с этого компьютера."),
            installed,
            InstallerCulture.Pick(
                "Nothing is installed to remove.",
                "Нечего удалять — ничего не установлено."));
    }
}
