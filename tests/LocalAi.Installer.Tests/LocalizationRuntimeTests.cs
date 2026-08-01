using System.Globalization;

namespace LocalAi.Installer.Tests;

public sealed class LocalizationRuntimeTests
{
    [Fact]
    public void Runtime_supports_russian_culture()
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");

        Assert.Equal("ru-RU", culture.Name);
    }
}
