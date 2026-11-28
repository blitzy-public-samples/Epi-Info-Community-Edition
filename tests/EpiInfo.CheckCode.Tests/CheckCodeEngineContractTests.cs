using EpiInfo.Domain.Events;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Tests;

/// <summary>
/// The permanent contract of the <see cref="ICheckCodeEngine"/> seam: <see cref="ICheckCodeEngine.Run"/> for a field
/// with no block for the event returns the empty envelope, and <see cref="ICheckCodeEngine.Analyze"/> and
/// <see cref="ICheckCodeEngine.Run"/> on an empty program return no effects and no diagnostics.
/// </summary>
/// <remarks>
/// These cases hold for an engine that returns empty results and for the full interpreter alike, so they stay green
/// whichever one <see cref="CheckCodeEngine"/> is. No case asserts what a field that has check code produces, or what
/// <see cref="ICheckCodeEngine.Analyze"/> reports for a non-empty program, because that is exactly where the two
/// differ. The tests need no database and read no file.
/// </remarks>
public sealed class CheckCodeEngineContractTests
{
    private const string LastName = "LastName";
    private const string Sex = "Sex";
    private const string Pregnant = "Pregnant";

    // The seeded CaseReport block for Sex (testdata/sql/03_forms.sql, metaViews ViewId 1), minus its "//add code
    // here" comment. Every construct in it belongs to the supported subset, so the interpreter reports nothing for
    // it statically, and only Sex has a block: LastName has none, and Sex has no Before or Click section.
    private const string SupportedProgram = """
        Field Sex
        	After
        		IF Sex = "1" THEN
        			ENABLE Pregnant
        		ELSE
        			DISABLE Pregnant
        			CLEAR Pregnant
        		END-IF
        	End-After
        End-Field
        """;

    /// <summary>
    /// Every field-event kind, none of which has a block for <c>LastName</c>; a fresh set on each access.
    /// </summary>
    public static TheoryData<FieldEventKind> AllEventKinds => new(Enum.GetValues<FieldEventKind>());

    /// <summary>
    /// Programs that hold nothing to run or report: absent, empty, whitespace only, and comments only; a fresh set on
    /// each access, so the theories that share it never share an instance.
    /// </summary>
    public static TheoryData<string?> EmptyPrograms => new()
    {
        null,
        "",
        "   \r\n\t  ",
        "// nothing here\r\n/* block\r\n comment */\r\n",
    };

    [Fact]
    public void Empty_IsTheEmptyEnvelope()
    {
        AssertEmptyEnvelope(FieldEventResponse.Empty);
    }

    [Theory]
    [MemberData(nameof(AllEventKinds))]
    public void Run_FieldWithoutAnyBlock_ReturnsEmptyEnvelope(FieldEventKind fieldEvent)
    {
        ICheckCodeEngine engine = new CheckCodeEngine();

        var response = engine.Run(SupportedProgram, CreateCaseReportForm(), fieldEvent, LastName, CreateValues());

        AssertEmptyEnvelope(response);
    }

    // FieldAfter is deliberately absent: Sex's After block has check code, and what it yields depends on the engine.
    [Theory]
    [InlineData(FieldEventKind.FieldBefore)]
    [InlineData(FieldEventKind.FieldClick)]
    public void Run_FieldWithoutMatchingEventBlock_ReturnsEmptyEnvelope(FieldEventKind fieldEvent)
    {
        ICheckCodeEngine engine = new CheckCodeEngine();

        var response = engine.Run(SupportedProgram, CreateCaseReportForm(), fieldEvent, Sex, CreateValues());

        AssertEmptyEnvelope(response);
    }

    [Theory]
    [MemberData(nameof(EmptyPrograms))]
    public void Run_EmptyProgram_ReturnsEmptyEnvelope(string? program)
    {
        ICheckCodeEngine engine = new CheckCodeEngine();

        var response = engine.Run(program, CreateCaseReportForm(), FieldEventKind.FieldAfter, Sex, CreateValues());

        AssertEmptyEnvelope(response);
    }

    [Theory]
    [MemberData(nameof(EmptyPrograms))]
    public void Analyze_EmptyProgram_ReturnsNoDiagnostics(string? program)
    {
        ICheckCodeEngine engine = new CheckCodeEngine();

        var diagnostics = engine.Analyze(program, CreateCaseReportForm());

        Assert.NotNull(diagnostics);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Run_IsStatelessAcrossCalls()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        var form = CreateCaseReportForm();
        var values = CreateValues();

        var first = engine.Run(SupportedProgram, form, FieldEventKind.FieldAfter, LastName, values);

        // The block run in between is the one that has check code, so an engine that carried its effects or its
        // evaluation state into the next call would show them there. Its own result is not asserted, because it
        // depends on the engine.
        _ = engine.Run(SupportedProgram, form, FieldEventKind.FieldAfter, Sex, values);

        var second = engine.Run(SupportedProgram, form, FieldEventKind.FieldAfter, LastName, values);

        AssertEmptyEnvelope(first);
        AssertEmptyEnvelope(second);
    }

    [Fact]
    public void Run_DoesNotMutateCallerValues()
    {
        ICheckCodeEngine engine = new CheckCodeEngine();
        var values = CreateValues();

        _ = engine.Run(SupportedProgram, CreateCaseReportForm(), FieldEventKind.FieldAfter, LastName, values);

        Assert.Equal(3, values.Count);
        Assert.Equal(FieldValue.FromText("2"), values[Sex]);
        Assert.Equal(FieldValue.FromText("1"), values[Pregnant]);
        Assert.Equal(FieldValue.FromText("Smith"), values[LastName]);
    }

    /// <summary>
    /// Asserts the empty envelope member by member. Comparing with <see cref="FieldEventResponse.Empty"/> would not
    /// do: record equality compares the six collections by reference, so an empty response built separately would
    /// fail it.
    /// </summary>
    private static void AssertEmptyEnvelope(FieldEventResponse response)
    {
        Assert.NotNull(response);
        Assert.NotNull(response.Enabled);
        Assert.Empty(response.Enabled);
        Assert.NotNull(response.Disabled);
        Assert.Empty(response.Disabled);
        Assert.NotNull(response.Cleared);
        Assert.Empty(response.Cleared);
        Assert.NotNull(response.Assigned);
        Assert.Empty(response.Assigned);
        Assert.NotNull(response.Dialogs);
        Assert.Empty(response.Dialogs);
        Assert.NotNull(response.Diagnostics);
        Assert.Empty(response.Diagnostics);
    }

    /// <summary>
    /// A CaseReport-like form holding the seeded <c>LastName</c>, <c>Sex</c> and <c>Pregnant</c> rows of
    /// <c>metaFields</c> on page 1, each with its <c>CaseReport1</c> column, so every field is a real data field.
    /// </summary>
    private static FormMetadata CreateCaseReportForm() => new(
        ViewId: 1,
        Name: "CaseReport",
        BaseTableName: "CaseReport",
        Pages: [new PageMetadata(PageId: 1, Name: "Page 1", Position: 0, TableName: "CaseReport1")],
        Fields:
        [
            DataField(fieldId: 6, LastName, fieldTypeId: 1, "Text", "Patient's Name", tabIndex: 2m, maxLengthBytes: 8000),
            DataField(fieldId: 17, Sex, fieldTypeId: 19, "Comment Legal", "Sex", tabIndex: 16m, maxLengthBytes: 510),
            DataField(
                fieldId: 43,
                Pregnant,
                fieldTypeId: 19,
                "Comment Legal",
                "Was the patient pregnant at the time of illness",
                tabIndex: 41m,
                maxLengthBytes: 510),
        ]);

    private static FieldMetadata DataField(
        int fieldId,
        string name,
        int fieldTypeId,
        string fieldTypeName,
        string prompt,
        decimal tabIndex,
        int maxLengthBytes) => new()
        {
            FieldId = fieldId,
            Name = name,
            FieldTypeId = fieldTypeId,
            FieldTypeName = fieldTypeName,
            Prompt = prompt,
            TabIndex = tabIndex,
            PageId = 1,
            Column = FieldTypeMapper.DescribeColumn(name, "nvarchar", maxLengthBytes),
        };

    /// <summary>A fresh record for each call, so no test can observe another's dictionary.</summary>
    private static IReadOnlyDictionary<string, FieldValue> CreateValues() =>
        new Dictionary<string, FieldValue>(StringComparer.OrdinalIgnoreCase)
        {
            [Sex] = FieldValue.FromText("2"),
            [Pregnant] = FieldValue.FromText("1"),
            [LastName] = FieldValue.FromText("Smith"),
        };
}
