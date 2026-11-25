using EpiInfo.CheckCode.Evaluation;
using EpiInfo.Domain.Diagnostics;
using EpiInfo.Domain.Events;
using EpiInfo.Domain.Values;

namespace EpiInfo.CheckCode.Tests.Evaluation;

/// <summary>
/// Pins the field-event membership and ordering rules that <see cref="EffectAccumulator"/> alone applies: a field
/// sits in at most one list per axis, the last call decides which, and each list is ordered by the first time each
/// field entered it. Dialogs keep execution order and diagnostics keep source order.
/// </summary>
public sealed class EffectAccumulatorTests
{
    [Fact]
    public void EnableEnableDisableEnable_KeepsFirstEntryOrder()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Enable("A");
        accumulator.Enable("B");
        accumulator.Disable("A");
        accumulator.Enable("A");
        var response = accumulator.ToResponse();

        // A first entered "enabled" before B; moving it out and back decides membership, never order.
        Assert.Equal(new[] { "A", "B" }, response.Enabled);
        Assert.Empty(response.Disabled);
        AssertNoValueEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    [Fact]
    public void DisableEnableEnable_OrdersEnabledByFirstEnabledEntry()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Disable("A");
        accumulator.Enable("B");
        accumulator.Enable("A");
        var response = accumulator.ToResponse();

        // A's earlier entry into "disabled" does not count towards its place in "enabled".
        Assert.Equal(new[] { "B", "A" }, response.Enabled);
        Assert.Empty(response.Disabled);
        AssertNoValueEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    [Fact]
    public void ClearClearAssignClear_KeepsClearedInFirstEntryOrder()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Clear("X");
        accumulator.Clear("Y");
        accumulator.Assign("X", N(1));
        accumulator.Clear("X");
        var response = accumulator.ToResponse();

        Assert.Equal(new[] { "X", "Y" }, response.Cleared);
        Assert.Empty(response.Assigned);
        AssertNoEnablementEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    [Fact]
    public void AssignAssignClearAssign_KeepsAssignedKeysInFirstEntryOrder()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Assign("X", N(1));
        accumulator.Assign("Y", N(2));
        accumulator.Clear("X");
        accumulator.Assign("X", N(3));
        var response = accumulator.ToResponse();

        Assert.Equal(new[] { "X", "Y" }, response.Assigned.Keys);
        Assert.Equal(N(3), response.Assigned["X"]);
        Assert.Equal(N(2), response.Assigned["Y"]);
        Assert.Empty(response.Cleared);
        AssertNoEnablementEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    [Fact]
    public void ClearAssign_ReportsAssignedOnly()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Clear("X");
        accumulator.Assign("X", N(1));
        var response = accumulator.ToResponse();

        var assigned = Assert.Single(response.Assigned);
        Assert.Equal("X", assigned.Key);
        Assert.Equal(N(1), assigned.Value);
        Assert.Empty(response.Cleared);
        AssertNoEnablementEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    [Fact]
    public void AssignClear_ReportsClearedOnly()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Assign("X", N(1));
        accumulator.Clear("X");
        var response = accumulator.ToResponse();

        Assert.Equal(new[] { "X" }, response.Cleared);
        Assert.Empty(response.Assigned);
        AssertNoEnablementEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    [Fact]
    public void DisableEnableDisable_LastCallDecidesMembership()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Disable("A");
        accumulator.Enable("A");
        accumulator.Disable("A");
        var response = accumulator.ToResponse();

        Assert.Equal(new[] { "A" }, response.Disabled);
        Assert.Empty(response.Enabled);
        AssertNoValueEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    [Fact]
    public void DisableDisableEnableDisable_OrdersDisabledByFirstDisabledEntry()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Disable("Zeta");
        accumulator.Disable("Alpha");
        accumulator.Enable("Zeta");
        accumulator.Disable("Zeta");
        var response = accumulator.ToResponse();

        // Zeta first entered "disabled" before Alpha, so it stays first after leaving and re-entering the list.
        // Ordering by name, or by each field's latest entry (Zeta re-entered last), would give [Alpha, Zeta].
        Assert.Equal(new[] { "Zeta", "Alpha" }, response.Disabled);
        Assert.Empty(response.Enabled);
        AssertNoValueEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    // Enablement and value are independent axes, so a field can sit in one list of each. Both call orders are run:
    // a single last-call-wins state shared by the two axes would drop whichever list the earlier call put it in.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnablementAndValueEffectsOnOneField_Coexist(bool enablementFirst)
    {
        var accumulator = new EffectAccumulator();

        InCallOrder(() => accumulator.Disable("X"), () => accumulator.Assign("X", N(1)));
        InCallOrder(() => accumulator.Enable("Y"), () => accumulator.Clear("Y"));
        var response = accumulator.ToResponse();

        Assert.Equal(new[] { "X" }, response.Disabled);
        Assert.Equal(new[] { "Y" }, response.Enabled);
        Assert.Equal(new[] { "Y" }, response.Cleared);
        var assigned = Assert.Single(response.Assigned);
        Assert.Equal("X", assigned.Key);
        Assert.Equal(N(1), assigned.Value);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);

        void InCallOrder(Action enablementCall, Action valueCall)
        {
            if (enablementFirst)
            {
                enablementCall();
                valueCall();
            }
            else
            {
                valueCall();
                enablementCall();
            }
        }
    }

    [Fact]
    public void AssignAssign_LaterAssignReplacesValue()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Assign("X", N(1));
        accumulator.Assign("X", N(2));
        var response = accumulator.ToResponse();

        var assigned = Assert.Single(response.Assigned);
        Assert.Equal("X", assigned.Key);
        Assert.Equal(N(2), assigned.Value);
        Assert.Empty(response.Cleared);
        AssertNoEnablementEffects(response);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    [Fact]
    public void AddDialog_KeepsExecutionOrderAndNullTitle()
    {
        var accumulator = new EffectAccumulator();
        var first = new DialogMessage("first", null);
        var second = new DialogMessage("second", "T");

        accumulator.AddDialog(first);
        accumulator.AddDialog(second);
        var response = accumulator.ToResponse();

        Assert.Equal(new[] { first, second }, response.Dialogs);
        Assert.Null(response.Dialogs[0].Title);
        Assert.Empty(response.Diagnostics);
        AssertNoEnablementEffects(response);
        AssertNoValueEffects(response);
        AssertInvariant(response);
    }

    [Fact]
    public void Diagnostics_AreReportedInSourceLineOrder()
    {
        var accumulator = new EffectAccumulator();

        accumulator.AddDiagnostic(Diag(9));
        accumulator.AddStaticDiagnostics(new[] { Diag(3), Diag(7) });
        var response = accumulator.ToResponse();

        Assert.Equal(new[] { 3, 7, 9 }, response.Diagnostics.Select(diagnostic => diagnostic.Line));
        Assert.Empty(response.Dialogs);
        AssertNoEnablementEffects(response);
        AssertNoValueEffects(response);
        AssertInvariant(response);
    }

    // Each combination of the two entry points adds GOTO then HIDE on line 5, followed by a diagnostic on an earlier
    // line, so the result has to be sorted, and the sort has to be stable to keep GOTO ahead of HIDE.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Diagnostics_OnTheSameLine_KeepInsertionOrder(bool firstIsStatic, bool secondIsStatic)
    {
        var accumulator = new EffectAccumulator();

        Add(accumulator, Diag(5, "GOTO"), firstIsStatic);
        Add(accumulator, Diag(5, "HIDE"), secondIsStatic);
        accumulator.AddDiagnostic(Diag(2, "SET-REQUIRED"));
        var response = accumulator.ToResponse();

        Assert.Equal(new[] { 2, 5, 5 }, response.Diagnostics.Select(diagnostic => diagnostic.Line));
        Assert.Equal(new[] { "SET-REQUIRED", "GOTO", "HIDE" }, response.Diagnostics.Select(diagnostic => diagnostic.Command));

        static void Add(EffectAccumulator target, Diagnostic diagnostic, bool viaStatic)
        {
            if (viaStatic)
            {
                target.AddStaticDiagnostics(new[] { diagnostic });
            }
            else
            {
                target.AddDiagnostic(diagnostic);
            }
        }
    }

    [Fact]
    public void FreshAccumulator_ReturnsTheEmptyEnvelope()
    {
        var response = new EffectAccumulator().ToResponse();

        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);

        // Same content as FieldEventResponse.Empty, compared member by member rather than by reference.
        var empty = FieldEventResponse.Empty;
        Assert.Equal(empty.Enabled, response.Enabled);
        Assert.Equal(empty.Disabled, response.Disabled);
        Assert.Equal(empty.Cleared, response.Cleared);
        Assert.Equal(empty.Assigned, response.Assigned);
        Assert.Equal(empty.Dialogs, response.Dialogs);
        Assert.Equal(empty.Diagnostics, response.Diagnostics);
    }

    // Case folding of program identifiers belongs to the evaluation context; here the canonical names the evaluator
    // passes in must come back unchanged.
    [Fact]
    public void FieldNames_AreReportedExactlyAsGiven()
    {
        var accumulator = new EffectAccumulator();

        accumulator.Enable("Pregnant");
        accumulator.Clear("DateofInterview");
        accumulator.Assign("OnsetWeek", N(23));
        var response = accumulator.ToResponse();

        Assert.Equal("Pregnant", Assert.Single(response.Enabled), StringComparer.Ordinal);
        Assert.Equal("DateofInterview", Assert.Single(response.Cleared), StringComparer.Ordinal);
        Assert.Equal("OnsetWeek", Assert.Single(response.Assigned).Key, StringComparer.Ordinal);
        Assert.Empty(response.Disabled);
        AssertNoDialogsOrDiagnostics(response);
        AssertInvariant(response);
    }

    private static FieldValue N(double n) => FieldValue.FromNumber(n);

    private static Diagnostic Diag(
        int line,
        string command = "GOTO",
        DiagnosticSeverity severity = DiagnosticSeverity.Warning) =>
        new(severity, "TestForm", line, command, "message");

    // A field is in at most one of enabled/disabled and at most one of cleared/assigned.
    private static void AssertInvariant(FieldEventResponse response)
    {
        Assert.Empty(response.Enabled.Intersect(response.Disabled, StringComparer.Ordinal));
        Assert.Empty(response.Cleared.Intersect(response.Assigned.Keys, StringComparer.Ordinal));
    }

    private static void AssertNoEnablementEffects(FieldEventResponse response)
    {
        Assert.Empty(response.Enabled);
        Assert.Empty(response.Disabled);
    }

    private static void AssertNoValueEffects(FieldEventResponse response)
    {
        Assert.Empty(response.Cleared);
        Assert.Empty(response.Assigned);
    }

    private static void AssertNoDialogsOrDiagnostics(FieldEventResponse response)
    {
        Assert.Empty(response.Dialogs);
        Assert.Empty(response.Diagnostics);
    }
}
