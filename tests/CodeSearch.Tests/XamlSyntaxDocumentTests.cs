using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class XamlSyntaxDocumentTests
{
    [Fact]
    public void Preserves_utf16_ranges_and_namespace_scope()
    {
        const string text =
            """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:Demo">
              <local:Card x:Name="😀Card" Grid.Row="1" />
            </Window>
            """;

        var document = XamlSyntaxDocument.Parse("Views/Main.xaml", text);
        var card = Assert.Single(document.Elements, element => element.Name == "local:Card");
        var name = Assert.Single(card.Attributes, attribute => attribute.Name == "x:Name");

        Assert.Equal("clr-namespace:Demo", card.Namespaces["local"]);
        Assert.Equal("😀Card", name.Value);
        Assert.Equal(new SourceRange(3, 22, 3, 28), name.ValueRange);
    }

    [Fact]
    public void Ignores_comments_processing_instructions_and_cdata()
    {
        const string text =
            """
            <?xml version="1.0"?>
            <!-- <Fake Value="ignored" /> -->
            <Root><![CDATA[<AlsoFake />]]><Real Value="yes" /></Root>
            """;

        var document = XamlSyntaxDocument.Parse("View.xaml", text);

        Assert.Equal(["Root", "Real"], document.Elements.Select(element => element.Name));
    }

    [Fact]
    public void Rejects_unquoted_attribute_values()
    {
        Assert.Throws<InvalidDataException>(() =>
            XamlSyntaxDocument.Parse("Bad.xaml", "<Root Value=no />"));
    }
}
