using System.Collections.ObjectModel;
using System.IO;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;

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
public sealed record StartActionOption(
    StartChoice Choice,
    string Title,
    string Description,
    bool IsAvailable,
    string UnavailableReason)
{
    public bool IsUnavailable => !IsAvailable;
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

    private readonly InstallerLanguageStore languages;

    public InstallerStartViewModel(
        string? localAppData = null,
        IExistingLocalAiInspector? inspector = null,
        Func<InstalledVersion>? readInstalledVersion = null,
        InstallerLanguageStore? languageStore = null)
    {
        languages = languageStore ?? InstallerLanguageStore.Default;
        var root = localAppData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        existing = (inspector ?? new ExistingLocalAiInspector(new SystemFileSystemProbe()))
            .Inspect(root);
        installed = readInstalledVersion is null
            ? InstalledVersionReader.Read(Path.Combine(root, "LocalAi"))
            : readInstalledVersion();
        foreach (var option in BuildOptions())
        {
            Actions.Add(option);
        }
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
    /// Said only in Russian, and only until the wizard is translated too. Somebody who picks
    /// Русский has just been told the installer speaks Russian; finding out on the next window
    /// that it does not — with no way back, because this window closes itself — reads as a
    /// broken product. Deleting this line is part of finishing the translation.
    /// </summary>
    public string TranslationNotice => InstallerCulture.Pick(
        string.Empty,
        "Следующие окна пока на английском: перевод до них ещё не дошёл.");

    public bool HasTranslationNotice => TranslationNotice.Length > 0;

    /// <summary>
    /// Takes effect here, on this screen, rather than from the next window: the alternative is
    /// an installer whose first act after being told which language you read is to keep using
    /// the other one.
    /// </summary>
    public void ChooseLanguage(InstallerLanguage language)
    {
        InstallerCulture.Current = language;
        languages.Write(language);
        Actions.Clear();
        foreach (var option in BuildOptions())
        {
            Actions.Add(option);
        }

        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(TranslationNotice));
        OnPropertyChanged(nameof(HasTranslationNotice));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(ChooseText));
        OnPropertyChanged(nameof(CloseText));
    }

    public bool IsEnglish => InstallerCulture.Current == InstallerLanguage.English;

    public bool IsRussian => InstallerCulture.Current == InstallerLanguage.Russian;

    public string ChooseText => InstallerCulture.Pick("Choose", "Выбрать");

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
            (existing.Reason ?? "The installation could not be read.") +
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
        yield return new StartActionOption(
            StartChoice.Install,
            InstallerCulture.Pick("Install LocalAi", "Установить LocalAi"),
            InstallerCulture.Pick(
                "Sets up the prerequisites, the runtime and the client integrations.",
                "Устанавливает необходимые компоненты, рантайм и интеграции с клиентскими " +
                "приложениями."),
            !installed,
            existing.State == ExistingLocalAiState.Compatible
                ? InstallerCulture.Pick(
                    "LocalAi" + version + " is already installed — use Update or repair.",
                    "LocalAi" + version + " уже установлен — выберите «Обновить или " +
                    "восстановить».")
                : InstallerCulture.Pick(
                    "There is already a LocalAi directory here — use Update or repair, which " +
                    "installs over it.",
                    "Каталог LocalAi здесь уже есть — выберите «Обновить или восстановить»: " +
                    "этот вариант устанавливает поверх."));
        yield return new StartActionOption(
            StartChoice.UpdateOrRepair,
            installed && existing.State == ExistingLocalAiState.Unrecognized
                ? InstallerCulture.Pick("Repair this installation", "Восстановить эту установку")
                : InstallerCulture.Pick("Update or repair", "Обновить или восстановить"),
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
