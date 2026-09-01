using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The shell of the installation wizard: the rail down the left, the heading and the paragraph
/// under it, in whichever language was chosen on the start screen.
///
/// Pure functions of the page rather than properties of the wizard, because two of the eight
/// pages — the one that runs the installation and the one that reports it — are only reachable
/// by running an installation. Text nobody can assert is text that quietly stays English, and
/// the finish page is exactly where a half-translated run would show.
/// </summary>
public static class WizardText
{
    /// <summary>The rail entry for each page, in order.</summary>
    public static IReadOnlyList<(InstallerPage Page, string Title)> Steps() =>
    [
        (InstallerPage.Diagnose, InstallerCulture.Pick("System check", "Проверка системы")),
        (InstallerPage.Dependencies,
            InstallerCulture.Pick("Prerequisites", "Необходимые компоненты")),
        (InstallerPage.Package, InstallerCulture.Pick("LocalAi package", "Пакет LocalAi")),
        (InstallerPage.Models, InstallerCulture.Pick("Models and memory", "Модели и память")),
        (InstallerPage.Agents,
            InstallerCulture.Pick("Client apps", "Клиентские приложения")),
        (InstallerPage.Confirm, InstallerCulture.Pick("Confirm", "Подтверждение")),
        (InstallerPage.Progress, InstallerCulture.Pick("Install", "Установка")),
        (InstallerPage.Finish, InstallerCulture.Pick("Finished", "Готово")),
    ];

    public static string Title(InstallerPage page, bool isUpdate, bool hasRunError) =>
        page switch
        {
            InstallerPage.Diagnose =>
                InstallerCulture.Pick("System check", "Проверка системы"),
            InstallerPage.Dependencies =>
                InstallerCulture.Pick("Prerequisites", "Необходимые компоненты"),
            InstallerPage.Package =>
                InstallerCulture.Pick("LocalAi package", "Пакет LocalAi"),
            InstallerPage.Models => InstallerCulture.Pick(
                "How models run on this computer",
                "Как модели работают на этом компьютере"),
            InstallerPage.Agents =>
                InstallerCulture.Pick("Client applications", "Клиентские приложения"),
            InstallerPage.Confirm => isUpdate
                ? InstallerCulture.Pick("Ready to update", "Готово к обновлению")
                : InstallerCulture.Pick("Ready to install", "Готово к установке"),
            InstallerPage.Progress => isUpdate
                ? InstallerCulture.Pick("Updating", "Обновление")
                : InstallerCulture.Pick("Installing", "Установка"),
            _ => (hasRunError, isUpdate) switch
            {
                (true, true) =>
                    InstallerCulture.Pick("Update not completed", "Обновление не завершено"),
                (true, false) =>
                    InstallerCulture.Pick("Installation not completed", "Установка не завершена"),
                (false, true) =>
                    InstallerCulture.Pick("Update complete", "Обновление завершено"),
                (false, false) =>
                    InstallerCulture.Pick("Installation complete", "Установка завершена"),
            },
        };

    /// <summary>
    /// <paramref name="actionText"/> is the label on the button that applies the run — "Install"
    /// or "Update". The confirm page used to spell it out, which was already wrong on an update
    /// run before any translation, and in Russian the word also has to move to the end of the
    /// sentence.
    /// </summary>
    public static string Description(
        InstallerPage page,
        bool isUpdate,
        string actionText,
        bool hasRunError,
        bool isChecking = false) =>
        page switch
        {
            // The shipped line stopped being true the moment results appeared, so it says one
            // thing while the probe runs and another once there is a table to read.
            InstallerPage.Diagnose when isChecking => InstallerCulture.Pick(
                "Checking this computer. Starting winget, git and ollama to see what is " +
                "installed — this takes a few seconds.",
                "Проверяю этот компьютер. Запускаю winget, git и ollama, чтобы увидеть, что " +
                "установлено, — это займёт несколько секунд."),
            InstallerPage.Diagnose => InstallerCulture.Pick(
                "What was found on this computer. Items marked as a warning still allow " +
                "installation.",
                "Что найдено на этом компьютере. Пункты с предупреждением не мешают установке."),
            InstallerPage.Dependencies => InstallerCulture.Pick(
                "Choose which prerequisites to install. Nothing is selected for you.",
                "Выберите, какие компоненты установить. За вас ничего не отмечено."),
            InstallerPage.Package => InstallerCulture.Pick(
                "Choose the LocalAi release to install.",
                "Выберите релиз LocalAi для установки."),
            InstallerPage.Models => InstallerCulture.Pick(
                "Video memory decides which models fit. Choose the rule first; the list below " +
                "follows it.",
                "Что поместится, решает видеопамять. Сначала выберите правило — список ниже " +
                "следует за ним."),
            InstallerPage.Agents => InstallerCulture.Pick(
                "Choose how each client application should be integrated.",
                "Выберите, как интегрировать каждое клиентское приложение."),
            InstallerPage.Confirm => InstallerCulture.Pick(
                "Review what is about to happen. To change anything click Back; to apply it " +
                $"click {actionText}.",
                "Прочитайте, что сейчас произойдёт. Чтобы что-то изменить, нажмите «Назад»; " +
                $"чтобы применить — «{actionText}»."),
            InstallerPage.Progress => InstallerCulture.Pick(
                "Applying the selected actions.",
                "Применяю выбранные действия."),
            _ => hasRunError
                ? InstallerCulture.Pick(
                    "Some actions did not complete. The log below shows what happened.",
                    "Часть действий не завершилась. Что произошло, показывает журнал ниже.")
                : InstallerCulture.Pick(
                    "All selected actions completed.",
                    "Все выбранные действия выполнены."),
        };
}
