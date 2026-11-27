using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Api.Tests.Records;

/// <summary>
/// The write-permission rule of the record endpoints and of the form-definition flags: which of <c>POST</c>,
/// <c>PUT</c> and <c>DELETE</c> a login's per-table INSERT and UPDATE permissions allow, the 403 ProblemDetails a denied
/// write returns, and that a denied write changes nothing.
/// </summary>
/// <remarks>
/// The class needs no database, so it carries no <c>Integration</c> trait. Each case hosts the API's own composition
/// root, <c>Program.cs</c>, through an <see cref="ApiFactory"/> on TestServer whose store is a
/// <see cref="FakeDataStore"/> holding <see cref="FakeForms.TwoPage"/>, or <see cref="FakeForms.WithRequiredField"/>
/// for the case that orders the required gate before the missing-page check, with synthetic per-table permissions.
/// Every request therefore travels the production pipeline, its error handling, guards, routing, JSON binding, the
/// endpoint handler, the service and result execution, and every assertion reads the status, headers and body the
/// client receives. A real denied database principal is not created, because the database's logins and users stay
/// unchanged; <see cref="MissingPagePermissionSqlTests"/> proves on the database where the SQL Server store reads the
/// permissions it re-checks inside an update.
/// </remarks>
public sealed class PermissionTests
{
    // Text fields of the two-page form: one on its first page and one on its second.
    private const string FirstPageField = "LastName";

    private const string SecondPageField = "Notes";

    private const string SeededText = "Seeded";

    // The optional text field beside the required one on FakeForms.WithRequiredField's only page.
    private const string OptionalField = "Comment";

    /// <summary>
    /// The entry a data table gets in a <see cref="FormPermissions"/>; <see cref="Missing"/> leaves the table without
    /// one.
    /// </summary>
    public enum Grant
    {
        /// <summary>No entry for the table.</summary>
        Missing,

        /// <summary>Neither INSERT nor UPDATE.</summary>
        Neither,

        /// <summary>INSERT without UPDATE.</summary>
        InsertOnly,

        /// <summary>UPDATE without INSERT.</summary>
        UpdateOnly,

        /// <summary>INSERT and UPDATE.</summary>
        Both,
    }

    /// <summary>One of the three record writes.</summary>
    public enum Write
    {
        /// <summary><c>POST /api/forms/{viewId}/records</c>.</summary>
        Post,

        /// <summary><c>PUT /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
        Put,

        /// <summary><c>DELETE /api/forms/{viewId}/records/{globalRecordId}</c>.</summary>
        Delete,
    }

    /// <summary>One of the three data tables of the form.</summary>
    public enum Table
    {
        /// <summary>The base table.</summary>
        Base,

        /// <summary>The first page's table.</summary>
        FirstPage,

        /// <summary>The second page's table.</summary>
        SecondPage,
    }

    /// <summary>
    /// The entry a <see cref="FormPermissions.ResolvedTableNames"/> case adds for the base table beside the one the map
    /// leads the base table's name to.
    /// </summary>
    public enum Conflict
    {
        /// <summary>No further entry.</summary>
        None,

        /// <summary>An entry spelled exactly as the form's metadata spells the base table.</summary>
        ExactlySpelledEntry,

        /// <summary>A second entry with the spelling the map leads the base table's name to.</summary>
        DuplicateEntry,
    }

    /// <summary>
    /// The grants of the base table, the first page table and the second page table, then the <c>canInsert</c>,
    /// <c>canUpdate</c> and <c>canDelete</c> the rule derives from them: INSERT on every table, UPDATE on every table,
    /// and UPDATE on the base table.
    /// </summary>
    public static TheoryData<Grant, Grant, Grant, bool, bool, bool> DerivedFlagCases => new()
    {
        // Everything granted: every write succeeds.
        { Grant.Both, Grant.Both, Grant.Both, true, true, true },

        // Base UPDATE granted and one page's UPDATE denied: PUT is refused while DELETE still succeeds.
        { Grant.Both, Grant.Both, Grant.InsertOnly, true, false, true },

        // INSERT denied on any one table refuses POST.
        { Grant.UpdateOnly, Grant.Both, Grant.Both, false, true, true },
        { Grant.Both, Grant.UpdateOnly, Grant.Both, false, true, true },
        { Grant.Both, Grant.Both, Grant.UpdateOnly, false, true, true },

        // INSERT and UPDATE both denied on a page whose row exists: page UPDATE is checked even when INSERT is denied,
        // so PUT is refused.
        { Grant.Both, Grant.Both, Grant.Neither, false, false, true },

        // A table without an entry denies both permissions on it.
        { Grant.Missing, Grant.Both, Grant.Both, false, false, false },
        { Grant.Both, Grant.Missing, Grant.Both, false, false, true },
        { Grant.Both, Grant.Both, Grant.Missing, false, false, true },

        // Base UPDATE denied with every page fully granted: PUT and DELETE are refused while POST succeeds.
        { Grant.InsertOnly, Grant.Both, Grant.Both, true, false, false },
        { Grant.Neither, Grant.Both, Grant.Both, false, false, false },
    };

    // The form-definition flags and each write agree with the rule: an allowed write commits, and a denied one is the
    // action's 403 with no store write, no new record and the targeted record unchanged. A denied PUT or DELETE is
    // refused before the store's locked read, so neither the update nor the soft delete reaches the store.
    [Theory]
    [MemberData(nameof(DerivedFlagCases))]
    public async Task EachWriteFollowsTheFlagThePermissionsDerive(
        Grant baseTable,
        Grant firstPage,
        Grant secondPage,
        bool canInsert,
        bool canUpdate,
        bool canDelete)
    {
        await using var harness = await Harness.StartAsync();
        var updateTarget = harness.SeedRecord();
        var deleteTarget = harness.SeedRecord();
        harness.SetPermissions(harness.Permissions(baseTable, firstPage, secondPage));

        AssertFlags(await harness.GetDefinitionAsync(), canInsert, canUpdate, canDelete);

        var recordsBefore = harness.RecordCount;
        var created = await harness.PostAsync(FirstPageField, "Created");
        if (canInsert)
        {
            AssertCreated(harness, created, FirstPageField, "Created", recordsBefore);
        }
        else
        {
            AssertForbidden(created, ApiProblems.ForbiddenInsertDetail);
            Assert.Equal(recordsBefore, harness.RecordCount);
        }

        var beforeUpdate = harness.Snapshot(updateTarget);
        var updated = await harness.PutAsync(updateTarget, FirstPageField, "Updated");
        if (canUpdate)
        {
            AssertUpdated(harness, updated, updateTarget, FirstPageField, "Updated");
        }
        else
        {
            AssertForbidden(updated, ApiProblems.ForbiddenUpdateDetail);
            Assert.DoesNotContain(updated.Calls, call => call.Method == nameof(IDataStore.UpdateAsync));
            AssertUnchanged(beforeUpdate, harness.Snapshot(updateTarget));
        }

        var beforeDelete = harness.Snapshot(deleteTarget);
        var deleted = await harness.DeleteAsync(deleteTarget);
        if (canDelete)
        {
            AssertDeleted(harness, deleted, deleteTarget);
        }
        else
        {
            AssertForbidden(deleted, ApiProblems.ForbiddenDeleteDetail);
            Assert.DoesNotContain(deleted.Calls, call => call.Method == nameof(IDataStore.SoftDeleteAsync));
            var afterDelete = harness.Snapshot(deleteTarget);
            Assert.Equal(RecordQueryRules.ActiveRecStatus, afterDelete.RecStatus);
            Assert.Equal(beforeDelete.LastSave, afterDelete.LastSave);
            AssertUnchanged(beforeDelete, afterDelete);
        }
    }

    // UPDATE is granted on every table and INSERT on every table but the second page, whose row one record lacks. A
    // PUT on that record needs INSERT on the missing page whichever page the submitted field is on, so it is refused
    // after the store's locked read with nothing written. The same PUT on a record holding every page row is saved,
    // and once INSERT on the page is granted the record lacking the row is saved too.
    [Theory]
    [InlineData(SecondPageField, true)]
    [InlineData(FirstPageField, false)]
    public async Task UpdateNeedsInsertOnEveryPageWhoseRowIsMissing(string field, bool fieldIsOnMissingPage)
    {
        await using var harness = await Harness.StartAsync();
        var missingPage = harness.SecondPage;
        Assert.Equal(fieldIsOnMissingPage, harness.PageOf(field).PageId == missingPage.PageId);

        var lacking = harness.SeedRecord(missingPage);
        var complete = harness.SeedRecord();
        harness.SetPermissions(harness.Permissions(Grant.Both, Grant.Both, Grant.UpdateOnly));

        AssertFlags(await harness.GetDefinitionAsync(), canInsert: false, canUpdate: true, canDelete: true);

        var before = harness.Snapshot(lacking);
        Assert.DoesNotContain(missingPage.PageId, before.PresentPageIds);
        var denied = await harness.PutAsync(lacking, field, "Denied");
        AssertForbidden(denied, ApiProblems.ForbiddenUpdateDetail);
        Assert.Contains(new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: false), denied.Calls);
        AssertUnchanged(before, harness.Snapshot(lacking));

        var saved = await harness.PutAsync(complete, field, "Saved");
        AssertUpdated(harness, saved, complete, field, "Saved");

        harness.SetPermissions(harness.AllGranted);
        var granted = await harness.PutAsync(lacking, field, "Granted");
        AssertUpdated(harness, granted, lacking, field, "Granted");
    }

    // On a one-page form whose only page row a record lacks, with UPDATE everywhere and INSERT denied on that page, the
    // store's required gate runs before its missing-page INSERT check: a PUT that leaves the required field empty is
    // the gate's 400, and one that fills it passes the gate and is the 403, each after the store's locked read with
    // nothing written. Once INSERT on the page is granted, the same PUT saves and inserts the page row.
    [Fact]
    public async Task UpdateRunsTheRequiredGateBeforeTheMissingPageInsertCheck()
    {
        var form = FakeForms.WithRequiredField;
        var store = new FakeDataStore(form);
        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
        using var client = factory.CreateClient();

        var metadata = form.Metadata;
        var page = Assert.Single(metadata.Pages);
        Assert.Equal(page.PageId, metadata.FindField(FakeForms.RequiredFieldName)?.PageId);
        Assert.Equal(page.PageId, metadata.FindField(OptionalField)?.PageId);

        var lacking = store.SeedRecord(FakeForms.RequiredViewId, missingPageIds: [page.PageId]);
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/forms/{FakeForms.RequiredViewId}/records/{Uri.EscapeDataString(lacking)}");
        store.SetPermissions(FakeForms.RequiredViewId, OnePagePermissions(metadata, pageInsert: false));
        var before = OnePageSnapshot(store, lacking);
        Assert.DoesNotContain(page.PageId, before.PresentPageIds);

        var incomplete = await ExchangeAsync(
            store,
            () => ApiTestClient.PutJsonAsync(client, path, OnePageBody(surname: null, "Incomplete")));
        AssertRequiredFieldMissing(incomplete);
        Assert.Contains(new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: false), incomplete.Calls);
        AssertUnchanged(before, OnePageSnapshot(store, lacking));

        var denied = await ExchangeAsync(
            store,
            () => ApiTestClient.PutJsonAsync(client, path, OnePageBody("Filled", "Denied")));
        AssertForbidden(denied, ApiProblems.ForbiddenUpdateDetail);
        Assert.Contains(new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: false), denied.Calls);
        AssertUnchanged(before, OnePageSnapshot(store, lacking));

        store.SetPermissions(FakeForms.RequiredViewId, OnePagePermissions(metadata, pageInsert: true));
        var granted = await ExchangeAsync(
            store,
            () => ApiTestClient.PutJsonAsync(client, path, OnePageBody("Filled", "Granted")));
        Assert.Equal(StatusCodes.Status200OK, granted.Status);
        Assert.Equal([new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: true)], granted.Writes);
        var after = OnePageSnapshot(store, lacking);
        Assert.Contains(page.PageId, after.PresentPageIds);
        Assert.Equal("Filled", after.Values[FakeForms.RequiredFieldName].AsText());
        Assert.Equal("Granted", after.Values[OptionalField].AsText());
        Assert.NotNull(after.LastSave);
    }

    // UPDATE and INSERT on the base table, UPDATE on the one page table, and INSERT on it only when pageInsert is set.
    private static FormPermissions OnePagePermissions(FormMetadata form, bool pageInsert) => new(
    [
        new TablePermission(form.BaseTableName, CanInsert: true, CanUpdate: true),
        new TablePermission(form.Pages[0].TableName, CanInsert: pageInsert, CanUpdate: true),
    ]);

    // The PUT body of FakeForms.WithRequiredField: the optional field, and the required field unless surname is null.
    private static object OnePageBody(string? surname, string comment)
    {
        var values = new Dictionary<string, string> { [OptionalField] = comment };
        if (surname is not null)
        {
            values[FakeForms.RequiredFieldName] = surname;
        }

        return new { values };
    }

    private static FakeRecordState OnePageSnapshot(FakeDataStore store, string globalRecordId)
    {
        var state = store.GetRecord(FakeForms.RequiredViewId, globalRecordId);
        Assert.NotNull(state);
        return state;
    }

    // The required gate's field-level 400: a validation ProblemDetails whose errors hold only the required field, with
    // its FIELD_IS_REQUIRED_LONG message, and no data-changing store call.
    private static void AssertRequiredFieldMissing(Response response)
    {
        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.NotNull(response.ContentType);
        Assert.Equal(ApiProblems.ProblemContentType, MediaTypeHeaderValue.Parse(response.ContentType).MediaType);

        var body = ApiTestClient.AssertProblemDetails(Body(response), StatusCodes.Status400BadRequest);
        Assert.Equal(ApiProblems.ValidationFailedDetail, body.GetProperty("detail").GetString());
        var entry = Assert.Single(body.GetProperty("errors").EnumerateObject());
        Assert.Equal(FakeForms.RequiredFieldName, entry.Name);
        var message = Assert.Single(entry.Value.EnumerateArray());
        Assert.Equal(
            ValidationMessages.FormatFieldIsRequired(FakeForms.RequiredFieldPrompt, FakeForms.RequiredFieldPageName),
            message.GetString());

        Assert.Empty(response.Writes);
    }

    // Permissions are read on every write and never cached: on one store behind one host, a write that was allowed is
    // refused once the permission it needs is revoked, with nothing written, and allowed again once the permission is
    // restored. Each write reads the permissions exactly once.
    [Theory]
    [InlineData(Write.Post)]
    [InlineData(Write.Put)]
    [InlineData(Write.Delete)]
    public async Task EachWriteReadsThePermissionsAgain(Write write)
    {
        await using var harness = await Harness.StartAsync();
        var granted = harness.AllGranted;
        var revoked = write switch
        {
            // INSERT revoked on the second page.
            Write.Post => harness.Permissions(Grant.Both, Grant.Both, Grant.UpdateOnly),

            // UPDATE revoked on the second page.
            Write.Put => harness.Permissions(Grant.Both, Grant.Both, Grant.InsertOnly),

            // UPDATE revoked on the base table.
            Write.Delete => harness.Permissions(Grant.InsertOnly, Grant.Both, Grant.Both),

            _ => throw new ArgumentOutOfRangeException(nameof(write)),
        };

        var first = harness.SeedRecord();
        var second = harness.SeedRecord();

        harness.SetPermissions(granted);
        var recordsBefore = harness.RecordCount;
        var allowed = await harness.SendAsync(write, first, "First");
        AssertSucceeded(harness, write, allowed, first, "First", recordsBefore);

        // A soft-deleted record stays deleted, so the later DELETEs target the record still active.
        var target = write == Write.Delete ? second : first;
        harness.SetPermissions(revoked);
        recordsBefore = harness.RecordCount;
        var before = harness.Snapshot(target);
        var denied = await harness.SendAsync(write, target, "Second");
        AssertForbidden(denied, ForbiddenDetail(write));
        Assert.Equal(recordsBefore, harness.RecordCount);
        AssertUnchanged(before, harness.Snapshot(target));

        harness.SetPermissions(granted);
        var restored = await harness.SendAsync(write, target, "Third");
        AssertSucceeded(harness, write, restored, target, "Third", recordsBefore);

        foreach (var response in new[] { allowed, denied, restored })
        {
            Assert.Single(response.Calls, call => call.Method == nameof(IDataStore.GetPermissionsAsync));
        }
    }

    // A store that reports no permissions means no check: the flags are null and every write proceeds, including a
    // PUT that inserts a missing page row.
    [Fact]
    public async Task NoReportedPermissionsMeansNoCheck()
    {
        await using var harness = await Harness.StartAsync();
        var lacking = harness.SeedRecord(harness.SecondPage);
        var doomed = harness.SeedRecord();
        harness.SetPermissions(null);

        AssertFlags(await harness.GetDefinitionAsync(), canInsert: null, canUpdate: null, canDelete: null);

        var recordsBefore = harness.RecordCount;
        var created = await harness.PostAsync(FirstPageField, "Created");
        AssertCreated(harness, created, FirstPageField, "Created", recordsBefore);

        var updated = await harness.PutAsync(lacking, SecondPageField, "Updated");
        AssertUpdated(harness, updated, lacking, SecondPageField, "Updated");
        Assert.Contains(harness.SecondPage.PageId, harness.Snapshot(lacking).PresentPageIds);

        var deleted = await harness.DeleteAsync(doomed);
        AssertDeleted(harness, deleted, doomed);

        foreach (var response in new[] { created, updated, deleted })
        {
            Assert.Single(response.Calls, call => call.Method == nameof(IDataStore.GetPermissionsAsync));
        }
    }

    /// <summary>The permissions the store holds for the form when an update given permissions runs.</summary>
    public enum StoreGrants
    {
        /// <summary>INSERT and UPDATE on every table.</summary>
        Every,

        /// <summary>No permissions at all.</summary>
        None,

        /// <summary>Every grant but UPDATE on the first page's table, whose row the record holds.</summary>
        PresentPageUpdateRevoked,

        /// <summary>Every grant but INSERT on the second page's table, whose row the record lacks.</summary>
        MissingPageInsertRevoked,
    }

    // The store decides an update given permissions from the permissions it holds when the update runs, never from the
    // caller's argument, which only turns the check on, as the SQL Server store reads them again inside its
    // transaction: on a record lacking its second-page row, an argument granting nothing still saves when the store
    // grants everything, and an argument granting everything is refused, with nothing written, when the store holds no
    // permissions or revokes a page's UPDATE or the missing page's INSERT.
    [Theory]
    [InlineData(StoreGrants.Every, false, true)]
    [InlineData(StoreGrants.None, true, false)]
    [InlineData(StoreGrants.PresentPageUpdateRevoked, true, false)]
    [InlineData(StoreGrants.MissingPageInsertRevoked, true, false)]
    public async Task TheStoreDecidesAnUpdateFromThePermissionsItHoldsWhenItRuns(
        StoreGrants held,
        bool argumentGrantsEverything,
        bool saves)
    {
        await using var harness = await Harness.StartAsync();
        var lacking = harness.SeedRecord(harness.SecondPage);
        harness.SetPermissions(held switch
        {
            StoreGrants.Every => harness.AllGranted,
            StoreGrants.None => null,
            StoreGrants.PresentPageUpdateRevoked => harness.Permissions(Grant.Both, Grant.InsertOnly, Grant.Both),
            StoreGrants.MissingPageInsertRevoked => harness.Permissions(Grant.Both, Grant.Both, Grant.UpdateOnly),
            _ => throw new ArgumentOutOfRangeException(nameof(held)),
        });
        var argument = argumentGrantsEverything ? harness.AllGranted : new FormPermissions([]);
        var stamp = new SaveStamp("tester", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local));
        var before = harness.Snapshot(lacking);
        var callsBefore = harness.Store.Calls.Count;

        var result = await harness.Store.UpdateAsync(
            harness.Form,
            lacking,
            new Dictionary<string, FieldValue> { [FirstPageField] = FieldValue.FromText("Applied") },
            () => stamp,
            _ => RecordValidationResult.Success(new Dictionary<string, FieldValue>()),
            argument,
            CancellationToken.None);

        var writes = harness.Store.Calls.Skip(callsBefore).Where(call => call.IsWrite).ToList();
        if (saves)
        {
            Assert.Equal(RecordWriteStatus.Success, result.Status);
            Assert.Equal([new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: true)], writes);
            var after = harness.Snapshot(lacking);
            Assert.Equal("Applied", after.Values[FirstPageField].AsText());
            Assert.Equal(stamp, after.LastSave);
        }
        else
        {
            Assert.Equal(RecordWriteStatus.Forbidden, result.Status);
            Assert.Empty(writes);
            AssertUnchanged(before, harness.Snapshot(lacking));
        }
    }

    /// <summary>
    /// A table; the grant of its entry spelled exactly as the form's metadata spells it, <see cref="Grant.Missing"/>
    /// for none; the grant of a second entry spelled in upper case; whether that second entry is listed first; then
    /// the <c>canInsert</c>, <c>canUpdate</c> and <c>canDelete</c> the rule derives and whether a <c>PUT</c> of the
    /// second-page field saves on a record lacking its second-page row. The two other tables are fully granted.
    /// </summary>
    public static TheoryData<Table, Grant, Grant, bool, bool, bool, bool, bool> OtherLetterCaseCases => new()
    {
        // The base table granted exactly and denied in upper case, in either order: every write succeeds.
        { Table.Base, Grant.Both, Grant.Neither, false, true, true, true, true },
        { Table.Base, Grant.Both, Grant.Neither, true, true, true, true, true },

        // The base table denied exactly and granted in upper case, in either order: every write is refused.
        { Table.Base, Grant.Neither, Grant.Both, false, false, false, false, false },
        { Table.Base, Grant.Neither, Grant.Both, true, false, false, false, false },

        // INSERT granted exactly on the second page and denied in upper case: the PUT inserts the missing row.
        { Table.SecondPage, Grant.Both, Grant.UpdateOnly, false, true, true, true, true },
        { Table.SecondPage, Grant.Both, Grant.UpdateOnly, true, true, true, true, true },

        // INSERT denied exactly on the second page and granted in upper case: the PUT is refused after the locked read.
        { Table.SecondPage, Grant.UpdateOnly, Grant.Both, false, false, true, true, false },
        { Table.SecondPage, Grant.UpdateOnly, Grant.Both, true, false, true, true, false },

        // Only the upper-case entry: the table itself has no entry, so both permissions on it are denied.
        { Table.Base, Grant.Missing, Grant.Both, false, false, false, false, false },
        { Table.FirstPage, Grant.Missing, Grant.Both, false, false, false, true, false },
        { Table.SecondPage, Grant.Missing, Grant.Both, false, false, false, true, false },
    };

    // Table names compare ordinally, so an entry spelled in another letter case names another table: whether it is
    // listed before or after the exactly spelled entry, or is the only entry, it neither grants nor denies anything on
    // the form's table, and the flags and every write follow the exactly spelled entry alone. That includes the INSERT
    // the store checks after its locked read for the page whose row is missing.
    [Theory]
    [MemberData(nameof(OtherLetterCaseCases))]
    public async Task AnEntryInAnotherLetterCaseNeitherGrantsNorDenies(
        Table table,
        Grant exact,
        Grant upperCaseEntry,
        bool upperCaseListedFirst,
        bool canInsert,
        bool canUpdate,
        bool canDelete,
        bool putSaves)
    {
        await using var harness = await Harness.StartAsync();
        var tables = new List<(string TableName, Grant Grant)>();
        foreach (var each in new[] { Table.Base, Table.FirstPage, Table.SecondPage })
        {
            var name = harness.TableName(each);
            if (each != table)
            {
                tables.Add((name, Grant.Both));
            }
            else if (upperCaseListedFirst)
            {
                tables.Add((Respelled(name, upperCase: true), upperCaseEntry));
                tables.Add((name, exact));
            }
            else
            {
                tables.Add((name, exact));
                tables.Add((Respelled(name, upperCase: true), upperCaseEntry));
            }
        }

        harness.SetPermissions(harness.Permissions([.. tables]));

        await AssertEachWriteFollows(harness, canInsert, canUpdate, canDelete, putSaves);
    }

    /// <summary>
    /// The grant of the second page's entry, the entry each case adds for the base table, then the <c>canInsert</c>,
    /// <c>canUpdate</c> and <c>canDelete</c> the rule derives and whether a <c>PUT</c> of the second-page field saves
    /// on a record lacking its second-page row.
    /// </summary>
    public static TheoryData<Grant, Conflict, bool, bool, bool, bool> ResolvedTableNameCases => new()
    {
        // Every entry granted: every write succeeds, including the PUT that inserts the missing second-page row.
        { Grant.Both, Conflict.None, true, true, true, true },

        // INSERT denied on the second page's entry: POST is refused and the PUT is refused after the locked read.
        { Grant.UpdateOnly, Conflict.None, false, true, true, false },

        // The base table's name reaches two entries that both grant: they conflict, so every write is refused.
        { Grant.Both, Conflict.ExactlySpelledEntry, false, false, false, false },
        { Grant.Both, Conflict.DuplicateEntry, false, false, false, false },
    };

    // A store whose entries carry another spelling of each table, as the SQL Server store's carry the catalogue's,
    // maps each form table name to its entry through ResolvedTableNames, which resolves the name to that one entry
    // before any decision, the missing-page INSERT check after the locked read included. A name the map leads to one
    // entry while another entry also claims the table decides nothing, so the rule fails closed.
    [Theory]
    [MemberData(nameof(ResolvedTableNameCases))]
    public async Task ResolvedTableNamesLeadEachFormNameToItsEntry(
        Grant secondPage,
        Conflict conflict,
        bool canInsert,
        bool canUpdate,
        bool canDelete,
        bool putSaves)
    {
        await using var harness = await Harness.StartAsync();
        var baseName = harness.Form.BaseTableName;
        var baseEntry = Respelled(baseName, upperCase: true);
        var firstEntry = Respelled(harness.FirstPage.TableName, upperCase: false);
        var secondEntry = Respelled(harness.SecondPage.TableName, upperCase: false);

        var tables = new List<(string TableName, Grant Grant)>
        {
            (baseEntry, Grant.Both),
            (firstEntry, Grant.Both),
            (secondEntry, secondPage),
        };
        switch (conflict)
        {
            case Conflict.None:
                break;

            case Conflict.ExactlySpelledEntry:
                tables.Add((baseName, Grant.Both));
                break;

            case Conflict.DuplicateEntry:
                tables.Add((baseEntry, Grant.Both));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(conflict));
        }

        harness.SetPermissions(harness.Permissions([.. tables]) with
        {
            ResolvedTableNames = new Dictionary<string, string>
            {
                [baseName] = baseEntry,
                [harness.FirstPage.TableName] = firstEntry,
                [harness.SecondPage.TableName] = secondEntry,
            },
        });

        await AssertEachWriteFollows(harness, canInsert, canUpdate, canDelete, putSaves);
    }

    // The name in upper or lower case, which must differ from it ordinally, so an entry under it is spelled
    // differently from the form's own table name.
    private static string Respelled(string name, bool upperCase)
    {
        var respelled = upperCase ? name.ToUpperInvariant() : name.ToLowerInvariant();
        Assert.NotEqual(name, respelled, StringComparer.Ordinal);
        return respelled;
    }

    // With the permissions already set: the form-definition flags, then a POST, a PUT of the second-page field on a
    // record lacking its second-page row, and a DELETE, each committed when allowed and otherwise its 403 with
    // nothing written. A PUT the flags allow can still be refused by the store after its locked read, when the missing
    // page's INSERT is denied; one the flags deny never reaches the store's update.
    private static async Task AssertEachWriteFollows(
        Harness harness,
        bool canInsert,
        bool canUpdate,
        bool canDelete,
        bool putSaves)
    {
        var lacking = harness.SeedRecord(harness.SecondPage);
        var doomed = harness.SeedRecord();

        AssertFlags(await harness.GetDefinitionAsync(), canInsert, canUpdate, canDelete);

        var recordsBefore = harness.RecordCount;
        var created = await harness.PostAsync(FirstPageField, "Created");
        if (canInsert)
        {
            AssertCreated(harness, created, FirstPageField, "Created", recordsBefore);
        }
        else
        {
            AssertForbidden(created, ApiProblems.ForbiddenInsertDetail);
            Assert.Equal(recordsBefore, harness.RecordCount);
        }

        var beforeUpdate = harness.Snapshot(lacking);
        Assert.DoesNotContain(harness.SecondPage.PageId, beforeUpdate.PresentPageIds);
        var updated = await harness.PutAsync(lacking, SecondPageField, "Updated");
        if (putSaves)
        {
            AssertUpdated(harness, updated, lacking, SecondPageField, "Updated");
            Assert.Contains(harness.SecondPage.PageId, harness.Snapshot(lacking).PresentPageIds);
        }
        else
        {
            AssertForbidden(updated, ApiProblems.ForbiddenUpdateDetail);
            if (canUpdate)
            {
                Assert.Contains(new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: false), updated.Calls);
            }
            else
            {
                Assert.DoesNotContain(updated.Calls, call => call.Method == nameof(IDataStore.UpdateAsync));
            }

            AssertUnchanged(beforeUpdate, harness.Snapshot(lacking));
        }

        var beforeDelete = harness.Snapshot(doomed);
        var deleted = await harness.DeleteAsync(doomed);
        if (canDelete)
        {
            AssertDeleted(harness, deleted, doomed);
        }
        else
        {
            AssertForbidden(deleted, ApiProblems.ForbiddenDeleteDetail);
            Assert.DoesNotContain(deleted.Calls, call => call.Method == nameof(IDataStore.SoftDeleteAsync));
            AssertUnchanged(beforeDelete, harness.Snapshot(doomed));
        }
    }

    private static string ForbiddenDetail(Write write) => write switch
    {
        Write.Post => ApiProblems.ForbiddenInsertDetail,
        Write.Put => ApiProblems.ForbiddenUpdateDetail,
        Write.Delete => ApiProblems.ForbiddenDeleteDetail,
        _ => throw new ArgumentOutOfRangeException(nameof(write)),
    };

    private static void AssertFlags(Response response, bool? canInsert, bool? canUpdate, bool? canDelete)
    {
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        var body = Body(response);
        Assert.Equal(canInsert, Flag(body, "canInsert"));
        Assert.Equal(canUpdate, Flag(body, "canUpdate"));
        Assert.Equal(canDelete, Flag(body, "canDelete"));
    }

    // A flag with no value is written as JSON null rather than omitted, so a missing member fails here.
    private static bool? Flag(JsonElement body, string name) => body.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new InvalidOperationException("A permission flag is neither a boolean nor null."),
    };

    // A denied write is a plain ProblemDetails holding exactly type, title, status, detail and traceId, with no field
    // errors and no write outcome, and the request made no data-changing store call.
    private static void AssertForbidden(Response response, string detail)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);
        Assert.NotNull(response.ContentType);
        Assert.Equal(ApiProblems.ProblemContentType, MediaTypeHeaderValue.Parse(response.ContentType).MediaType);

        var body = Body(response);
        Assert.Equal(
            ["detail", "status", "title", "traceId", "type"],
            body.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.4", body.GetProperty("type").GetString());
        Assert.Equal("Forbidden", body.GetProperty("title").GetString());
        Assert.Equal(StatusCodes.Status403Forbidden, body.GetProperty("status").GetInt32());
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));

        Assert.Empty(response.Writes);
    }

    private static void AssertSucceeded(
        Harness harness,
        Write write,
        Response response,
        string id,
        string text,
        int recordsBefore)
    {
        switch (write)
        {
            case Write.Post:
                AssertCreated(harness, response, FirstPageField, text, recordsBefore);
                break;

            case Write.Put:
                AssertUpdated(harness, response, id, FirstPageField, text);
                break;

            case Write.Delete:
                AssertDeleted(harness, response, id);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(write));
        }
    }

    private static void AssertCreated(Harness harness, Response response, string field, string text, int recordsBefore)
    {
        Assert.Equal(StatusCodes.Status201Created, response.Status);
        Assert.Equal([new FakeStoreCall(nameof(IDataStore.InsertAsync), IsWrite: true)], response.Writes);
        Assert.Equal(recordsBefore + 1, harness.RecordCount);

        var body = Body(response);
        var id = body.GetProperty("globalRecordId").GetString();
        Assert.NotNull(id);
        Assert.Equal($"/api/forms/{harness.ViewId}/records/{id}", response.Location);
        Assert.Equal(text, body.GetProperty("values").GetProperty(field).GetString());
        Assert.Equal(text, harness.Snapshot(id).Values[field].AsText());
    }

    private static void AssertUpdated(Harness harness, Response response, string id, string field, string text)
    {
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal([new FakeStoreCall(nameof(IDataStore.UpdateAsync), IsWrite: true)], response.Writes);

        var body = Body(response);
        Assert.Equal(id, body.GetProperty("globalRecordId").GetString());
        Assert.Equal(text, body.GetProperty("values").GetProperty(field).GetString());

        var stored = harness.Snapshot(id);
        Assert.Equal(text, stored.Values[field].AsText());
        Assert.NotNull(stored.LastSave);
    }

    private static void AssertDeleted(Harness harness, Response response, string id)
    {
        Assert.Equal(StatusCodes.Status204NoContent, response.Status);
        Assert.Null(response.Body);
        Assert.Equal([new FakeStoreCall(nameof(IDataStore.SoftDeleteAsync), IsWrite: true)], response.Writes);

        var stored = harness.Snapshot(id);
        Assert.Equal(RecordQueryRules.DeletedRecStatus, stored.RecStatus);
        Assert.NotNull(stored.LastSave);
    }

    // Member by member, and the values entry by entry in storage order.
    private static void AssertUnchanged(FakeRecordState before, FakeRecordState after)
    {
        Assert.Equal(before.GlobalRecordId, after.GlobalRecordId);
        Assert.Equal(before.UniqueKey, after.UniqueKey);
        Assert.Equal(before.RecStatus, after.RecStatus);
        Assert.Equal(before.FirstSave, after.FirstSave);
        Assert.Equal(before.LastSave, after.LastSave);
        Assert.Equal(before.PresentPageIds.Order(), after.PresentPageIds.Order());
        Assert.Equal(before.Values.Keys, after.Values.Keys);
        foreach (var (name, value) in before.Values)
        {
            Assert.Equal(value, after.Values[name]);
        }
    }

    private static JsonElement Body(Response response)
    {
        Assert.True(response.Body.HasValue, "The response has no JSON body.");
        return response.Body.Value;
    }

    // Sends one request and reads its whole response before collecting the store calls made while it ran. Location is
    // read as sent: on Unix a path beginning with '/' parses as an absolute file URI, whose original string is still
    // the header's text.
    private static async Task<Response> ExchangeAsync(FakeDataStore store, Func<Task<HttpResponseMessage>> send)
    {
        var callsBefore = store.Calls.Count;
        using var response = await send().ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        JsonElement? json = null;
        if (body.Length > 0)
        {
            using var document = JsonDocument.Parse(body);
            json = document.RootElement.Clone();
        }

        return new Response(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Headers.Location?.OriginalString,
            json,
            [.. store.Calls.Skip(callsBefore)]);
    }

    /// <summary>A response as the client received it, with the store calls the request made.</summary>
    /// <param name="Status">The response status.</param>
    /// <param name="ContentType">The response's <c>Content-Type</c>, or <see langword="null"/> for none.</param>
    /// <param name="Location">The response's <c>Location</c>, or <see langword="null"/> for none.</param>
    /// <param name="Body">The parsed JSON body, or <see langword="null"/> for an empty body.</param>
    /// <param name="Calls">The store calls made while the request ran, in order.</param>
    private sealed record Response(
        int Status,
        string? ContentType,
        string? Location,
        JsonElement? Body,
        IReadOnlyList<FakeStoreCall> Calls)
    {
        /// <summary>The data-changing entries of <see cref="Calls"/>.</summary>
        public IReadOnlyList<FakeStoreCall> Writes => [.. Calls.Where(call => call.IsWrite)];
    }

    /// <summary>
    /// <see cref="FakeForms.TwoPage"/> in a <see cref="FakeDataStore"/>, behind a started <see cref="ApiFactory"/>
    /// host of the API, and the client that sends requests to it.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ApiFactory factory;

        private readonly HttpClient client;

        private Harness(FormMetadata form, FakeDataStore store, ApiFactory factory, HttpClient client)
        {
            Form = form;
            Store = store;
            this.factory = factory;
            this.client = client;

            // The cases rely on one text field on each page, read from the metadata rather than assumed.
            Assert.Equal(2, Form.Pages.Count);
            Assert.Equal(FirstPage.PageId, PageOf(FirstPageField).PageId);
            Assert.Equal(SecondPage.PageId, PageOf(SecondPageField).PageId);
        }

        public FormMetadata Form { get; }

        public FakeDataStore Store { get; }

        public int ViewId => Form.ViewId;

        public PageMetadata FirstPage => Form.Pages[0];

        public PageMetadata SecondPage => Form.Pages[1];

        public int RecordCount => Store.GetRecords(ViewId).Count;

        public FormPermissions AllGranted => Permissions(Grant.Both, Grant.Both, Grant.Both);

        // The factory replaces only the store, the clock, the SQL observer and the check-code engine, so every other
        // registration and the whole pipeline are production's; the store being in memory, no connection is made. The
        // first client starts the host. A host that fails to start, or whose form lacks the fields the cases rely on,
        // is disposed before the failure propagates.
        public static async Task<Harness> StartAsync()
        {
            var form = FakeForms.TwoPage;
            var store = new FakeDataStore(form);
            var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
            try
            {
                return new Harness(form.Metadata, store, factory, factory.CreateClient());
            }
            catch
            {
                await factory.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public PageMetadata PageOf(string field)
        {
            var pageId = Form.FindField(field)?.PageId;
            Assert.NotNull(pageId);
            var page = Form.FindPage(pageId.Value);
            Assert.NotNull(page);
            return page;
        }

        public string TableName(Table table) => table switch
        {
            Table.Base => Form.BaseTableName,
            Table.FirstPage => FirstPage.TableName,
            Table.SecondPage => SecondPage.TableName,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };

        // Builds the per-table entries from the form's own table names, leaving out each table granted Missing.
        public FormPermissions Permissions(Grant baseTable, Grant firstPage, Grant secondPage) => Permissions(
            (Form.BaseTableName, baseTable),
            (FirstPage.TableName, firstPage),
            (SecondPage.TableName, secondPage));

        // Builds one entry per listed name, in the order listed, leaving out each name granted Missing.
        public FormPermissions Permissions(params (string TableName, Grant Grant)[] tables)
        {
            var entries = new List<TablePermission>();
            foreach (var (tableName, grant) in tables)
            {
                AddEntry(entries, tableName, grant);
            }

            return new FormPermissions(entries);
        }

        public void SetPermissions(FormPermissions? permissions) => Store.SetPermissions(ViewId, permissions);

        // An active record holding a value in both text fields, lacking the rows of missingPages.
        public string SeedRecord(params PageMetadata[] missingPages) => Store.SeedRecord(
            ViewId,
            new Dictionary<string, FieldValue>
            {
                [FirstPageField] = FieldValue.FromText(SeededText),
                [SecondPageField] = FieldValue.FromText(SeededText),
            },
            missingPageIds: missingPages.Select(page => page.PageId));

        public FakeRecordState Snapshot(string globalRecordId)
        {
            var state = Store.GetRecord(ViewId, globalRecordId);
            Assert.NotNull(state);
            return state;
        }

        // GET /api/forms/{viewId}.
        public Task<Response> GetDefinitionAsync() => ExecuteAsync(() => client.GetAsync(FormPath()));

        // POST /api/forms/{viewId}/records.
        public Task<Response> PostAsync(string field, string text) =>
            ExecuteAsync(() => ApiTestClient.PostJsonAsync(client, $"{FormPath()}/records", WriteBody(field, text)));

        // PUT /api/forms/{viewId}/records/{globalRecordId}.
        public Task<Response> PutAsync(string globalRecordId, string field, string text) =>
            ExecuteAsync(() => ApiTestClient.PutJsonAsync(client, RecordPath(globalRecordId), WriteBody(field, text)));

        // DELETE /api/forms/{viewId}/records/{globalRecordId}.
        public Task<Response> DeleteAsync(string globalRecordId) =>
            ExecuteAsync(() => client.DeleteAsync(RecordPath(globalRecordId)));

        // POST ignores the record id; PUT and DELETE target it. PUT and POST submit the first-page text field.
        public Task<Response> SendAsync(Write write, string globalRecordId, string text) => write switch
        {
            Write.Post => PostAsync(FirstPageField, text),
            Write.Put => PutAsync(globalRecordId, FirstPageField, text),
            Write.Delete => DeleteAsync(globalRecordId),
            _ => throw new ArgumentOutOfRangeException(nameof(write)),
        };

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await factory.DisposeAsync().ConfigureAwait(false);
        }

        private static void AddEntry(List<TablePermission> entries, string tableName, Grant grant)
        {
            if (grant == Grant.Missing)
            {
                return;
            }

            entries.Add(new TablePermission(
                tableName,
                CanInsert: grant is Grant.InsertOnly or Grant.Both,
                CanUpdate: grant is Grant.UpdateOnly or Grant.Both));
        }

        // The record-write body, sent as application/json: {"values": {field: text}}.
        private static object WriteBody(string field, string text) =>
            new { values = new Dictionary<string, string> { [field] = text } };

        private string FormPath() => string.Create(CultureInfo.InvariantCulture, $"/api/forms/{ViewId}");

        private string RecordPath(string globalRecordId) =>
            $"{FormPath()}/records/{Uri.EscapeDataString(globalRecordId)}";

        private Task<Response> ExecuteAsync(Func<Task<HttpResponseMessage>> send) => ExchangeAsync(Store, send);
    }
}

/// <summary>
/// The in-transaction permission check of <c>PUT</c> on the SQL Server store, against the rehearsal database (AAP
/// 0.2.4), on a record whose page row is missing and once that row is repaired: every update reads the permissions
/// inside its transaction, after the locked read, the record read and the required gate and before the first write,
/// for the base table and every page table, so the UPDATE on each of them and the INSERT on the table of each page
/// whose row is missing are never decided from the service's earlier read.
/// </summary>
/// <remarks>
/// <para>
/// The seeded login holds every permission and no denied principal is created, because the database's logins and
/// users stay unchanged. The case therefore proves where and on what the check runs through
/// <see cref="SqlCommandRecorder"/>, and <see cref="PermissionTests"/> proves the denial on
/// <see cref="FakeDataStore"/>.
/// </para>
/// <para>
/// The record is created only through <c>POST /api/forms/{viewId}/records</c>, carrying a fresh
/// <see cref="TestDatabase.NewMarker"/> in <see cref="TestDatabase.MarkerField"/>, and stays in the database. The one
/// direct write deletes the <c>FoodHistory3</c> row of that record (AAP 0.3.7, "Data reach of tests"), under the
/// exclusive <see cref="DatabaseWriteLock"/>. Failure messages describe commands without their bound values and never
/// print a response body.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollections.Database)]
public sealed class MissingPagePermissionSqlTests
{
    private const string FoodHistory = "FoodHistory";

    // FoodHistory's text fields FirstName, on its first page (FoodHistory2), and Lab, on its second (FoodHistory3),
    // both nvarchar(4000) with no pattern, length limit or legal values (testdata/sql/03_forms.sql and
    // 05_data_tables.sql).
    private const string PageOneField = "FirstName";

    private const string PageTwoField = "Lab";

    // The fixed operation names EpiInfo.Data reports to the command observer: the record repository's update, and the
    // permission reader's form-level read that RecordService makes before it calls the store.
    private const string UpdateOperation = "records.update";

    private const string ServicePermissionOperation = "GetPermissions";

    private const string PermissionFunction = "HAS_PERMS_BY_NAME";

    private const string LockHint = "UPDLOCK";

    // The parameters the in-transaction permission statement binds, in order: the bracket-quoted catalogue names of
    // FoodHistory's base table, then of its page tables in metaPages.Position order (testdata/sql/03_forms.sql and
    // 05_data_tables.sql).
    private static readonly string[] TableSecurables = ["[FoodHistory]", "[FoodHistory2]", "[FoodHistory3]"];

    // Longer than DatabaseWriteLock's 300-second wait, so a lock held by another process fails the lock call rather
    // than this token; xUnit v2 gives a test no cancellation token of its own.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    // A PUT of a page-one field on a record lacking its FoodHistory3 row, a PUT of the page-two field that repairs the
    // row, and a PUT once every page row is present each read the permissions of the base table and every page table
    // in their transaction, which is where the UPDATE on each table, and the INSERT on FoodHistory3 while its row is
    // missing, are decided. The service's own read runs before each transaction, outside it, and decides nothing there.
    [Fact]
    public async Task UpdatePermissions_AreReadInsideTheUpdateTransactionBeforeItsFirstWrite()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var ct = timeout.Token;
        await using var factory = await ApiFactory.ForDatabaseAsync();
        using var client = factory.CreateClient();
        await using var dbLock = await DatabaseWriteLock.AcquireExclusiveAsync(ct);

        var viewId = await ApiTestClient.GetViewIdAsync(client, FoodHistory, ct);
        var created = await ApiTestClient.CreateRecordAsync(
            client,
            viewId,
            new Dictionary<string, object?> { [TestDatabase.MarkerField] = TestDatabase.NewMarker() },
            ct);
        var id = created.GlobalRecordId;

        // The create wrote a FoodHistory3 row; removing it is the one direct write the test makes on its own record.
        Assert.Equal(1, await CountPageTwoRowsAsync(id, ct));
        Assert.Equal(1, await DeletePageTwoRowAsync(id, ct));
        Assert.Equal(0, await CountPageTwoRowsAsync(id, ct));

        factory.Recorder.Clear();
        await PutAsync(client, viewId, id, PageOneField, "Avery", ct);
        var pageOneUpdate = factory.Recorder.Commands;
        AssertServiceReadPrecedesTheTransaction(pageOneUpdate);
        AssertPermissionsCheckedInTheTransaction(pageOneUpdate);
        Assert.Equal(0, await CountPageTwoRowsAsync(id, ct));

        factory.Recorder.Clear();
        await PutAsync(client, viewId, id, PageTwoField, "Pending", ct);
        var pageTwoUpdate = factory.Recorder.Commands;
        AssertServiceReadPrecedesTheTransaction(pageTwoUpdate);
        AssertPermissionsCheckedInTheTransaction(pageTwoUpdate);
        Assert.Equal(1, await CountPageTwoRowsAsync(id, ct));

        factory.Recorder.Clear();
        await PutAsync(client, viewId, id, PageOneField, "Blake", ct);
        var completeUpdate = factory.Recorder.Commands;
        AssertServiceReadPrecedesTheTransaction(completeUpdate);
        AssertPermissionsCheckedInTheTransaction(completeUpdate);
        Assert.Equal(1, await CountPageTwoRowsAsync(id, ct));
    }

    // PUT {"values": {field: text}} answers 200 with the record holding the submitted text.
    private static async Task PutAsync(
        HttpClient client,
        int viewId,
        string globalRecordId,
        string field,
        string text,
        CancellationToken ct)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/forms/{viewId}/records/{Uri.EscapeDataString(globalRecordId)}");
        using var response = await ApiTestClient.PutJsonAsync(
            client,
            path,
            new { values = new Dictionary<string, object?> { [field] = text } },
            ct);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The PUT of {field} returned {(int)response.StatusCode}, expected 200."));

        var record = await ApiTestClient.ReadJsonElementAsync(response, ct);
        Assert.True(
            record.ValueKind == JsonValueKind.Object &&
            record.TryGetProperty("values", out var values) &&
            values.ValueKind == JsonValueKind.Object &&
            values.TryGetProperty(field, out var stored) &&
            stored.ValueKind == JsonValueKind.String &&
            stored.ValueEquals(text),
            $"The record the PUT of {field} returned does not hold the submitted text.");
    }

    // RecordService's own permission read: one statement, outside any transaction, completed before the update's
    // transaction sent its first statement.
    private static void AssertServiceReadPrecedesTheTransaction(IReadOnlyList<RecordedCommand> commands)
    {
        var serviceReads = commands
            .Where(command => IsPermissionCheck(command) &&
                string.Equals(command.Operation, ServicePermissionOperation, StringComparison.Ordinal))
            .ToList();
        Assert.True(
            serviceReads.Count == 1 && !serviceReads[0].InTransaction && serviceReads[0].Succeeded,
            "The PUT must send exactly one service permission statement, outside any transaction, and it must " +
            "succeed:" + Describe(commands));

        var firstInTransaction = commands.FirstOrDefault(command => command.InTransaction);
        Assert.True(
            firstInTransaction is not null && serviceReads[0].Sequence < firstInTransaction.Sequence,
            "The service permission statement must precede every statement of the update's transaction:" +
            Describe(commands));
    }

    // On the update's connection, from its locked base read on, the statements before the first write are exactly the
    // locked read, the record read and one permission statement bound to the base table and then each page table, all
    // inside the transaction; no other permission statement joins any transaction.
    private static void AssertPermissionsCheckedInTheTransaction(IReadOnlyList<RecordedCommand> commands)
    {
        var lockedReads = commands
            .Where(command => IsUpdate(command) && command.CommandText.Contains(LockHint, StringComparison.Ordinal))
            .ToList();
        Assert.True(
            lockedReads.Count == 1 && lockedReads[0].InTransaction,
            "The PUT must send exactly one locked base read, inside its transaction:" + Describe(commands));
        var lockedRead = lockedReads[0];

        var fromLock = commands
            .Where(command => command.ClientConnectionId == lockedRead.ClientConnectionId &&
                command.Sequence >= lockedRead.Sequence)
            .ToList();
        var beforeWrite = fromLock.TakeWhile(command => !command.IsWrite).ToList();
        var firstWrite = fromLock.FirstOrDefault(command => command.IsWrite);
        var recordRead = beforeWrite.Count == 3 ? beforeWrite[1] : null;
        var check = beforeWrite.Count == 3 ? beforeWrite[2] : null;
        Assert.True(
            recordRead is not null &&
            IsUpdate(recordRead) &&
            recordRead.InTransaction &&
            !IsPermissionCheck(recordRead) &&
            !recordRead.CommandText.Contains(LockHint, StringComparison.Ordinal) &&
            check is not null &&
            IsUpdate(check) &&
            check.InTransaction &&
            IsPermissionCheck(check) &&
            check.Succeeded &&
            firstWrite is not null &&
            firstWrite.InTransaction,
            "From the locked base read on, the update's connection must send the record read, then one permission " +
            "statement, both inside the transaction, and then its first write:" + Describe(fromLock));

        Assert.True(
            check.Parameters.Select(parameter => parameter.Value as string).SequenceEqual(TableSecurables),
            "The in-transaction permission statement must bind exactly the base table and then each page table, " +
            $"{string.Join(", ", TableSecurables)}: {check}");

        Assert.True(
            commands.Count(command => IsPermissionCheck(command) && command.InTransaction) == 1,
            "Exactly one permission statement may run inside a transaction:" + Describe(commands));
    }

    private static bool IsPermissionCheck(RecordedCommand command) =>
        command.CommandText.Contains(PermissionFunction, StringComparison.Ordinal);

    private static bool IsUpdate(RecordedCommand command) =>
        string.Equals(command.Operation, UpdateOperation, StringComparison.Ordinal);

    // Each command on its own line, without parameter values.
    private static string Describe(IEnumerable<RecordedCommand> commands) =>
        string.Concat(commands.Select(command => Environment.NewLine + command));

    private static SqlParameter IdParameter(string globalRecordId) =>
        new("@id", SqlDbType.NVarChar, 255) { Value = globalRecordId };

    private static async Task<int> CountPageTwoRowsAsync(string globalRecordId, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [dbo].[FoodHistory3] WHERE [GlobalRecordId] = @id;";
        command.Parameters.Add(IdParameter(globalRecordId));
        return Assert.IsType<int>(await command.ExecuteScalarAsync(ct));
    }

    // The missing-page fixture: the only direct write of this test, on the FoodHistory record it created.
    private static async Task<int> DeletePageTwoRowAsync(string globalRecordId, CancellationToken ct)
    {
        await using var connection = await TestDatabase.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM [dbo].[FoodHistory3] WHERE [GlobalRecordId] = @id;";
        command.Parameters.Add(IdParameter(globalRecordId));
        return await command.ExecuteNonQueryAsync(ct);
    }
}
