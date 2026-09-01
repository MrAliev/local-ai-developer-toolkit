using System.Reflection;
using Xunit.v3;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Every test starts in English, whatever the test before it did.
///
/// Serialising the assembly into one collection stopped two tests running at once, but it did
/// not stop one leaving the language set for the next. That held only because all five classes
/// which choose a language also restore it, which is a convention somebody has to remember —
/// and forgetting it turns unrelated tests red in a way that looks nothing like the cause.
///
/// A test that wants Russian sets it in its own body, which runs after this.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PinInstallerLanguageAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) =>
        InstallerCulture.Current = InstallerLanguage.English;
}
