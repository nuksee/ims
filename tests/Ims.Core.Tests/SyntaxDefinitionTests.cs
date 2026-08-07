using System.Xml;
using FluentAssertions;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// Guards the syntax highlighting definition against the fault that silently
/// disabled it (PR-3.1).
/// </summary>
/// <remarks>
/// <para>
/// The definition documented Informix's comment forms in its own header, and wrote
/// the double-hyphen one literally. XML forbids that inside a comment, so the file
/// was not well-formed. <c>HighlightingLoader</c> threw,
/// <c>MainWindow.LoadSyntaxHighlighting</c> caught and discarded the exception on
/// purpose — highlighting is not worth refusing to start over — and the editor
/// showed plain black text with nothing anywhere explaining why.
/// </para>
/// <para>
/// The catch is still right; a broken definition should not stop the app. What was
/// wrong is that nothing else noticed, and a file of keywords is easy to edit
/// without ever launching the app. This test is the thing that notices.
/// </para>
/// <para>
/// Deliberately checks well-formedness with <see cref="XmlReader"/> rather than
/// loading it through AvalonEdit: that is the fault that actually occurred, and it
/// keeps this project free of a WPF dependency.
/// </para>
/// </remarks>
public class SyntaxDefinitionTests
{
    private static string DefinitionPath
    {
        get
        {
            // Walk up to the repository root: the test runs from bin/<cfg>/<tfm>.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                directory = directory.Parent;
            }

            directory.Should().NotBeNull("the test must be able to find the repository root");

            return Path.Combine(
                directory!.FullName, "src", "Ims.App", "Resources", "InformixSql.xshd");
        }
    }

    [Fact]
    public void The_highlighting_definition_is_well_formed_xml()
    {
        File.Exists(DefinitionPath).Should().BeTrue("PR-3.1 needs the definition to exist");

        Action parse = () =>
        {
            using XmlReader reader = XmlReader.Create(DefinitionPath);
            while (reader.Read())
            {
                // Reading to the end is the assertion: XmlReader throws on the first
                // malformed construct it meets.
            }
        };

        parse.Should().NotThrow(
            "an ill-formed definition leaves the editor with no highlighting, and the "
            + "loader is written to fail quietly, so nothing at runtime would say so");
    }

    [Fact]
    public void The_definition_still_declares_the_colours_the_editor_expects()
    {
        string xshd = File.ReadAllText(DefinitionPath);

        // Well-formed but empty would pass the parse test and still leave a plain
        // editor, so check the definition actually defines something.
        foreach (string colour in new[] { "Comment", "String", "Keyword", "DataType" })
        {
            xshd.Should().Contain(
                $"name=\"{colour}\"",
                "PR-3.1 asks for Informix SQL and SPL to be highlighted, not merely parsed");
        }
    }
}
