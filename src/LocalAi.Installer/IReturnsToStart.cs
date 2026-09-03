namespace LocalAi.Installer;

/// <summary>
/// A wizard that can hand control back to the screen that opened it, rather than ending the
/// run. The start window needs to tell that apart from every other way a wizard closes: one
/// brings the screen back, the others take it with them.
/// </summary>
public interface IReturnsToStart
{
    event EventHandler? ReturnToStart;
}
