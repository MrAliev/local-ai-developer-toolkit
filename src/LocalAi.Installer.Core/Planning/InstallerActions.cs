// The transactional installer these types were shaped for was never wired into the wizard, and
// was removed rather than left standing as a promise the product did not keep. These two
// survived it because the shipping code does use them: the wizard translates its own agent
// choice into AgentIntegrationChoice, and model provisioning is described with
// ModelInstallAction. They keep this namespace because changing one costs every caller a using
// directive for no gain.

namespace LocalAi.Installer.Core.Planning;

/// <summary>What an installation should do about one AI client's configuration.</summary>
public enum AgentIntegrationChoice
{
    McpOnly,
    InstructionsOnly,
    McpAndInstructions,
    NoChange,
}

/// <summary>One model an installation was asked to provision, with the consent it was given.</summary>
public sealed record ModelInstallAction(
    string ActionId,
    string Model,
    int ContextSize,
    bool Selected,
    bool ConsentGranted);
