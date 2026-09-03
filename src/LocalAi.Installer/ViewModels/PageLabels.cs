using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The labels written directly into the markup — group headers, radio buttons, the sentence
/// under each option, the navigation buttons.
///
/// Markup cannot ask which language was chosen, so the choice is made here and the markup binds
/// with <c>{x:Static}</c>. That resolves once, when the window is loaded, which is exactly when
/// it should: the language is settled on the start screen before either wizard window exists.
///
/// Everything that varies with what the run is doing lives in <see cref="WizardText"/> or in the
/// page view model that owns it. This class only holds text that is the same on every run.
/// </summary>
public static class PageLabels
{
    // --- System check -------------------------------------------------------------------
    public static string Probing => InstallerCulture.Pick(
        "Starting winget, git and ollama…",
        "Запускаю winget, git и ollama…");

    public static string InterruptedRun => InstallerCulture.Pick(
        "Interrupted previous run",
        "Прерванный предыдущий запуск");

    public static string RollBackPreviousRun => InstallerCulture.Pick(
        "Roll back previous run",
        "Откатить предыдущий запуск");

    // --- Prerequisites ------------------------------------------------------------------
    public static string NoAutomatedInstaller => InstallerCulture.Pick(
        "No automated installer is available; install it yourself.",
        "Автоматической установки нет; установите его вручную.");

    // --- LocalAi package ----------------------------------------------------------------
    public static string VersionToInstall => InstallerCulture.Pick(
        "Version to install",
        "Версия для установки");

    public static string VersionHint => InstallerCulture.Pick(
        "Leave this at latest to install whatever is newest when you press the button on " +
        "the last page. Type a version number instead to pin one.",
        "Оставьте latest, чтобы установить самый новый релиз на момент нажатия кнопки на " +
        "последней странице. Введите номер версии, чтобы закрепить конкретную.");

    public static string InstallFromFolder => InstallerCulture.Pick(
        "Install from a folder",
        "Установка из папки");

    public static string FolderHint => InstallerCulture.Pick(
        "A folder holding release-manifest.json, release-manifest.sig and " +
        "localai-package.zip. Leave empty to install from GitHub. The manifest is verified " +
        "against the key built into this installer either way; models still come from the " +
        "Ollama registry.",
        "Папка с файлами release-manifest.json, release-manifest.sig и localai-package.zip. " +
        "Оставьте пустым, чтобы установить с GitHub. Манифест в любом случае проверяется " +
        "ключом, встроенным в этот установщик; модели по-прежнему берутся из реестра Ollama.");

    public static string CheckAgain => InstallerCulture.Pick("Check again", "Проверить снова");

    // --- Models and memory --------------------------------------------------------------
    public static string VideoMemory => InstallerCulture.Pick("Video memory", "Видеопамять");

    public static string RequireFullVram => InstallerCulture.Pick(
        "Require the whole model in video memory (recommended)",
        "Требовать всю модель в видеопамяти (рекомендуется)");

    public static string RequireFullVramHint => InstallerCulture.Pick(
        "Refuses to run a model that does not fit. Fastest, and the only rule that never " +
        "degrades an answer.",
        "Отказывается запускать модель, которая не помещается. Самый быстрый вариант и " +
        "единственное правило, которое никогда не ухудшает ответ.");

    public static string AllowPartialOffload => InstallerCulture.Pick(
        "Allow part of the model in system memory — slower",
        "Разрешить часть модели в системной памяти — медленнее");

    public static string AllowPartialOffloadHint => InstallerCulture.Pick(
        "Needs an adapter that holds at least part of the model.",
        "Нужен адаптер, вмещающий хотя бы часть модели.");

    public static string AllowCpu => InstallerCulture.Pick(
        "Allow running on the processor — much slower",
        "Разрешить работу на процессоре — намного медленнее");

    public static string AllowCpuHint => InstallerCulture.Pick(
        "Works without a usable adapter.",
        "Работает без пригодного адаптера.");

    public static string LocalModels => InstallerCulture.Pick("Local models", "Локальные модели");

    public static string ChooseAutomatically => InstallerCulture.Pick(
        "Choose automatically for this computer",
        "Выбрать автоматически для этого компьютера");

    public static string ChooseExactly => InstallerCulture.Pick("Choose exactly", "Выбрать точно");

    public static string Model => InstallerCulture.Pick("Model", "Модель");

    public static string ContextSize => InstallerCulture.Pick("Context size", "Размер контекста");

    public static string ContextSizeHint => InstallerCulture.Pick(
        "Only the sizes this model permits are offered. A smaller context needs less video " +
        "memory.",
        "Предлагаются только те размеры, которые допускает эта модель. Меньший контекст " +
        "требует меньше видеопамяти.");

    public static string SkipModelSetup => InstallerCulture.Pick(
        "Skip model setup",
        "Пропустить настройку моделей");

    public static string SkipModelSetupHint => InstallerCulture.Pick(
        "Nothing is downloaded now. Models can be set up later.",
        "Сейчас ничего не скачивается. Модели можно настроить позже.");

    // --- Confirm ------------------------------------------------------------------------
    public static string ChooseARelease => InstallerCulture.Pick(
        "Choose a release…",
        "Выбрать релиз…");

    public static string ChangeSettings => InstallerCulture.Pick(
        "Change settings…",
        "Изменить настройки…");

    public static string AfterThisInstallation => InstallerCulture.Pick(
        "After this installation",
        "После этой установки");

    public static string EnableUpdateCheck => InstallerCulture.Pick(
        "After it is installed, let LocalAi look up whether a newer release exists",
        "После установки разрешить LocalAi проверять, вышел ли более новый релиз");

    public static string UpdateCheckHint => InstallerCulture.Pick(
        "A newer version is then mentioned in localai doctor and in the index status your " +
        "assistant reads. Nothing pops up, and nothing installs itself.",
        "Тогда о более новой версии сообщат localai doctor и статус индекса, который " +
        "читает ваш ассистент. Ничего не всплывает и ничего не устанавливается само.");

    // --- Progress, finish, navigation ---------------------------------------------------
    public static string Log => InstallerCulture.Pick("Log", "Журнал");

    public static string RollBackChanges => InstallerCulture.Pick(
        "Roll back changes",
        "Откатить изменения");

    public static string Rollback => InstallerCulture.Pick("Rollback", "Откат");

    public static string Back => InstallerCulture.Pick("Back", "Назад");

    public static string Next => InstallerCulture.Pick("Next", "Далее");

    // --- The theme control, on the start screen beside the language ------------------
    public static string ThemeSystem => InstallerCulture.Pick("System", "Системная");

    public static string ThemeLight => InstallerCulture.Pick("Light", "Светлая");

    public static string ThemeDark => InstallerCulture.Pick("Dark", "Тёмная");

    // --- The removal wizard -------------------------------------------------------------
    public static string RemoveLocalAi => InstallerCulture.Pick(
        "Remove LocalAi",
        "Удаление LocalAi");

    public static string StartFrom => InstallerCulture.Pick("Start from", "С чего начать");

    public static string WhatGoes => InstallerCulture.Pick("What goes", "Что удаляется");

    public static string ConnectedRepositories => InstallerCulture.Pick(
        "Connected repositories",
        "Подключённые репозитории");

    public static string RemovalConsent => InstallerCulture.Pick(
        "I have read what will be removed and want to continue.",
        "Я понимаю, что будет удалено, и хочу продолжить.");

    public static string Report => InstallerCulture.Pick("Report", "Отчёт");

    public static string ContinueToInstall => InstallerCulture.Pick(
        "Continue to install",
        "Перейти к установке");

    public static string Uninstall => InstallerCulture.Pick("Uninstall", "Удалить");
}
