using System.Text;
using EpiInfo.Data.Sql;

namespace EpiInfo.Data.Tests.Sql;

/// <summary>
/// Unit tests of the quoting rule of <see cref="SqlIdentifier"/>: <c>[</c>, the name with every <c>]</c> doubled,
/// then <c>]</c>. They call the internal <c>QuoteName</c> directly and need no database. The public
/// <c>Quote</c> overloads accept only reader-built schemas, so they are exercised by
/// <c>ReservedWordIdentifierTests</c>, and the provenance of those schemas by <c>IdentifierProvenanceTests</c>.
/// </summary>
public sealed class SqlIdentifierTests
{
    // Legacy InsertInEscape skipped a bracket the name already had and never doubled ']'. The "[x", "x]" and "[x]"
    // rows pin the departure: a name is always wrapped and every ']' is always doubled, so the text cannot close
    // the identifier early.
    [Theory]
    [InlineData("CaseReport", "[CaseReport]")]
    [InlineData("a]b", "[a]]b]")]
    [InlineData("]]", "[]]]]]")]
    [InlineData("Order", "[Order]")]
    [InlineData("[x", "[[x]")]
    [InlineData("x]", "[x]]]")]
    [InlineData("[x]", "[[x]]]")]
    [InlineData("O'Brien x", "[O'Brien x]")]
    [InlineData(" x ", "[ x ]")]
    [InlineData("Fiebre_Ñandú", "[Fiebre_Ñandú]")]
    [InlineData("患者", "[患者]")]
    [InlineData("名]前", "[名]]前]")]
    // Combining marks stay as written: the name is not Unicode-normalised.
    [InlineData("N\u0303andu\u0301", "[N\u0303andu\u0301]")]
    public void QuoteName_WrapsInBracketsAndDoublesClosingBrackets(string name, string expected)
    {
        Assert.Equal(expected, SqlIdentifier.QuoteName(name));
    }

    [Theory]
    [InlineData("CaseReport")]
    [InlineData("a]b")]
    [InlineData("]]")]
    [InlineData("x]")]
    [InlineData("[x]")]
    [InlineData("O'Brien x")]
    [InlineData(" x ")]
    [InlineData("名]前")]
    public void QuoteName_OutputParsesBackToTheOriginalName(string name)
    {
        string quoted = SqlIdentifier.QuoteName(name);

        Assert.Equal('[', quoted[0]);
        (string parsed, int end) = ReadDelimitedIdentifier(quoted);
        Assert.Equal(quoted.Length, end);
        Assert.Equal(name, parsed);
    }

    // The 128-character sysname limit applies to the name, not to the quoted text, which can reach 258 characters.
    [Theory]
    [InlineData('a', 128)]
    [InlineData(']', 256)]
    public void QuoteName_AcceptsNamesUpToTheSysnameLength(char character, int quotedBodyLength)
    {
        string name = new(character, 128);

        Assert.Equal("[" + new string(character, quotedBodyLength) + "]", SqlIdentifier.QuoteName(name));
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    public void QuoteName_NullOrEmpty_ThrowsTheDocumentedException(string? name, Type expectedType)
    {
        Exception exception = Assert.Throws(expectedType, () => SqlIdentifier.QuoteName(name!));

        Assert.Equal("name", Assert.IsAssignableFrom<ArgumentException>(exception).ParamName);
    }

    [Fact]
    public void QuoteName_LongerThanSysname_ThrowsArgumentExceptionWithoutTheName()
    {
        const string sentinel = "SentinelIdentifier";
        string name = sentinel + new string('x', 129 - sentinel.Length);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => SqlIdentifier.QuoteName(name));

        Assert.Equal("name", exception.ParamName);
        Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
    }

    // Reads text that starts with '[' the way SQL Server reads a delimited identifier: "]]" is one literal ']' and a
    // lone ']' closes the identifier. Returns the name read and the index just past the closing bracket, or -1 when
    // the text never closes it.
    private static (string Name, int End) ReadDelimitedIdentifier(string text)
    {
        var name = new StringBuilder(text.Length);
        int index = 1;
        while (index < text.Length)
        {
            char character = text[index];
            if (character != ']')
            {
                name.Append(character);
                index++;
            }
            else if (index + 1 < text.Length && text[index + 1] == ']')
            {
                name.Append(']');
                index += 2;
            }
            else
            {
                return (name.ToString(), index + 1);
            }
        }

        return (name.ToString(), -1);
    }
}
