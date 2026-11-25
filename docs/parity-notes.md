# Parity notes — Epi Info Web Data Entry

This file is the single ledger of the Epi Info web data-entry migration: every reconciled assumption, every departure from legacy Epi Info Enter, every parity gap and every undelivered item, together with the run report, which console output never replaces. Each status is `pass`, `fail` or `not run`. Only results produced by a real run count as verified; readings of the seed scripts and the legacy source are labelled "static finding (planning time)".

## Run results

### Snapshot

- **Baseline commit**: `092e3d63ba1eea372365450eb915b1c763a58ed2`. `git rev-parse HEAD` was not recorded when the run started; this commit is the parent of the first migration commit, `8136dd26`, read from `git log`.
- **Tree reported on**: `22ead6a780a63447fd0503e9bcbf3793c7d5e4a8`, 2026-11-25, plus this file. It holds only build manifests and settings: `global.json`, `Directory.Packages.props`, `src/Directory.Build.props`, `src/EpiInfo.Api/EpiInfo.Api.csproj`, `src/EpiInfo.Api/appsettings.json`, `src/EpiInfo.Data/EpiInfo.Data.csproj`, the four `tests/*/*.csproj`, `web/.gitignore`, `web/package.json` and `web/package-lock.json`. The solution, the sources, the tests and the web client are not in it, so no proof file existed to run.
- **Toolchain**: .NET SDK 10.0.115, selected by `global.json` (10.0.100, `latestPatch`); Node v22.23.5.
- **Commands name the solution** (C-1): `dotnet build EpiInfo.Web.sln` and `dotnet test EpiInfo.Web.sln`, because the root also holds `Epi Info 7.sln`, and with two solutions a bare `dotnet build` stops with `MSB1011` (static finding (planning time), reproduced then with SDK 10.0.404).

### Success criteria

| Criterion | Tier | Status | Evidence |
|-----------|------|--------|----------|
| SC-1 clean checkout | P0 | fail | Database precondition: pass (below). `dotnet build EpiInfo.Web.sln`: exit 1, `MSBUILD : error MSB1009: Project file does not exist. Switch: EpiInfo.Web.sln`. `npm ci` in `web/`: pass, exit 0, 321 packages from the committed lockfile. Not run: `dotnet test EpiInfo.Web.sln` (no solution), `npm run lint`, `npm run build`, `npm test` and `npm run e2e` (no `eslint.config.js`, `tsconfig.json`, `vite.config.ts`, `playwright.config.ts` or `src/`). `HealthTests` and `web/e2e/smoke.spec.ts` are not in the tree |
| SC-2 browser workflow | P0 | not run | `web/e2e/smoke.spec.ts`, `web/e2e/support/sql.ts` and the client are not in the tree; `npm run e2e` not run |
| SC-3 stored-data equivalence | P0 | not run | `StoredDataContractTests` is not in the tree |
| SC-4 read equivalence and `DATA-01` | P0 | not run | `ReadEquivalenceTests` is not in the tree |
| SC-5 navigation, listing and find | P0 | not run | `NavigationTests`, `ListAndSearchTests`, `RecordQueryRulesTests`, `SearchCriteriaParserTests` and `useRecordNavigation.test.ts` are not in the tree |
| SC-6 validation and save gate | P0 | not run | `ValidationTests`, `RecordValidatorTests` and `MaskPatternTests` are not in the tree |
| SC-7 transactional save | P0 | not run | `TransactionalSaveTests` is not in the tree |
| SC-8 metadata mapping and renderer | P0 | not run | `FieldTypeMapperTests`, `FormDefinitionFactoryTests`, `FormDefinitionTests`, `SqlMetadataReaderTests` and `FormView.test.tsx` are not in the tree. The direct SQL counts in "Field types found" are context, not this criterion's proof |
| SC-9 check code | P1 | fail | Fails because P1 check code is incomplete; as a P1 proof it does not decide the P0 gate. `CheckCodeScenarioTests`, `ScenarioCatalog.cs`, `DiagnosticsTests` and the interpreter are not in the tree, so no scenario ran and no `PendingP1Ids` array exists yet. Undelivered: the 19 field-event cases `LY-01`..`LY-04`, `EC-AGE-01`..`EC-AGE-05`, `EC-WEEK-01`..`EC-WEEK-05`, `EC-RACE-01`, `EC-RACE-02` and `EC-FEVER-01`..`EC-FEVER-03`; the separate `EC-GEO-01` check that `GEOCODE` is logged and skipped; and the `DiagnosticsTests` case in which a synthetic program with an unsupported command returns a diagnostic carrying form, line and command. The P0 subset of the 24 in-scope scenarios (`EC-VAL-01`..`EC-VAL-03`, `DATA-01`, `DATA-02`) did not run either, for the same reason; those cases belong to the P0 suite |
| SC-10 effects reach the screen | P1 | fail | Fails because P1 client events are incomplete; as a P1 proof it does not decide the P0 gate. `FieldEffects.test.tsx`, `fieldEventQueue.test.ts` and `applyFieldEvent.ts` are not in the tree, so neither test ran. Missing step: the smoke test's SC-10 step, which enters scenario `EC-AGE-01`'s `DOB` and `DateofInterview` on `FoodHistory`, leaves `DOB` and checks that `Age` shows the expected value; `web/e2e/smoke.spec.ts` itself is not in the tree |
| SC-11 performance | P2 | not run | `dotnet test tests/EpiInfo.Api.Tests --filter Category=Performance` not run: `PerformanceTests` is not in the tree, and P2 work starts only once every P0 criterion passes |

### Database precondition

- **Status**: pass. All nine named checks of `testdata/sql/99_verify.sql:4-12` returned exactly once, each with `ok = 1`, and no other row.
- **Target**: SQL Server 2022 RTM-CU28 (16.0.4325.4) Developer Edition in a Linux container. The database checked is a copy of `EpiRehearsal` restored from a backup taken straight after a `testdata/init.sh` load; `99_verify.sql` ran against it at 2026-11-25T10:32:38Z, before any test touched it.
- `testdata/init.sh` prints these rows but exits 0 even when a row has `ok = 0`, so only the rows establish the precondition, never the script's exit status.

| `check_name` | `actual` | `expected` | `ok` |
|--------------|----------|------------|------|
| `metaViews` | 2 | 2 | 1 |
| `metaPages` | 3 | 3 | 1 |
| `metaFieldTypes` | 28 | 28 | 1 |
| `CaseReport records` | 1409 | 1409 | 1 |
| `FoodHistory records` | 609 | 609 | 1 |
| `CaseReport1 orphans` | 0 | 0 | 1 |
| `FoodHistory2 orphans` | 0 | 0 | 1 |
| `FoodHistory3 orphans` | 0 | 0 | 1 |
| `soft-deleted records` | 29 | 29 | 1 |

- **Compatibility level**: no compatibility level is required. Catalogue discovery (`MetadataQueries.Columns`) reads its table-name list with `OPENJSON` at database compatibility level 130 or higher, and with a fixed fallback decoder of the JSON string syntax below it; both paths return the same rows. The nine `99_verify` checks do not cover the level, and `testdata/sql/00_create_database.sql` pins none, so the database takes the server's `model` level. Measured read-only on `EpiRehearsal` (`sys.databases.compatibility_level`), 2026-11-25: 160.

### Checks that need no database

The precondition passed, so this list only separates the steps that do not depend on it.

| Command | Status | Evidence |
|---------|--------|----------|
| `dotnet build EpiInfo.Web.sln` | fail | Exit 1, `MSB1009`: the solution is not in the tree |
| `dotnet test EpiInfo.Web.sln --filter "Category!=Integration"` | not run | No solution |
| `npm ci` (in `web/`) | pass | Exit 0; 321 packages installed from `web/package-lock.json` |
| `npm run lint` | not run | No `web/eslint.config.js` or `web/scripts/check-api-fresh.mjs` |
| `npm run build` | not run | No `web/tsconfig.json`, `web/vite.config.ts` or `web/src/` |
| `npm test` | not run | No `web/vite.config.ts` or test files |

### OpenAPI staleness checks

Each is `not run`: `web/scripts/api-generate.mjs`, `web/scripts/check-api-fresh.mjs`, `src/EpiInfo.Api/openapi/EpiInfo.Api.json` and `web/src/api/schema.ts` are not in the tree.

| Check | Expected | Status |
|-------|----------|--------|
| `npm run lint` on a clean tree | passes | not run |
| `npm run lint` on a warm tree | passes | not run |
| After editing only `src/EpiInfo.Api/openapi/EpiInfo.Api.json` | fails with the stale message | not run |
| After editing only `web/src/api/schema.ts` | fails with the stale message | not run |

### Docker check

`not run`: `src/EpiInfo.Api/Dockerfile` (P2) is not in the tree, so no image was built and `/health`, `/` and `/api/forms` were not requested from a container. Listed under "Undelivered items".

### Undelivered and not-yet-present items

- Relate fields and child forms (P2): not delivered in this run; see [Undelivered items](#undelivered-items).
- SQLite `IDataStore` (P2): not delivered in this run; see [Undelivered items](#undelivered-items).
- Parity matrix (P2): not delivered; see [Undelivered items](#undelivered-items) and [Parity matrix](#parity-matrix).
- Interpreter (P1): mapped for delivery in this run, not in the tree at the snapshot; see [Not in the tree at the snapshot](#not-in-the-tree-at-the-snapshot).
- Field-event behaviour (P1): mapped for delivery in this run, not in the tree at the snapshot; see [Not in the tree at the snapshot](#not-in-the-tree-at-the-snapshot).
- Client events (P1): mapped for delivery in this run, not in the tree at the snapshot; see [Not in the tree at the snapshot](#not-in-the-tree-at-the-snapshot).
- Permission awareness (P2): mapped for delivery in this run, not in the tree at the snapshot; see [Not in the tree at the snapshot](#not-in-the-tree-at-the-snapshot).
- Command-line switch mapping (P2): mapped for delivery in this run, not in the tree at the snapshot; see [Not in the tree at the snapshot](#not-in-the-tree-at-the-snapshot).
- SC-11 performance (P2): mapped for delivery in this run, not in the tree at the snapshot; see [Not in the tree at the snapshot](#not-in-the-tree-at-the-snapshot).
- Docker image (P2): mapped for delivery in this run, not in the tree at the snapshot; see [Not in the tree at the snapshot](#not-in-the-tree-at-the-snapshot) and the [Docker check](#docker-check).
- Higher test coverage (P2): mapped for delivery in this run, not in the tree at the snapshot; see [Not in the tree at the snapshot](#not-in-the-tree-at-the-snapshot).

### Proofs not made

- **Reserved-word round trip** (`ReservedWordIdentifierTests`): not proved. Neither the metadata reader's internal `tempdb` overload nor the repository's internal open-connection overload is in the tree.
- **`tests/Directory.Build.props` inert for `Tests/Tests.csproj`**: not provable on the snapshot tree, which did not hold the props file; proved once it was added (see "Boundary checks").
- **OpenAPI clean and warm byte-identity**: not re-proved; the generation scripts are not in the tree.

### Tree checks at the snapshot

| Check | Result | Status |
|-------|--------|--------|
| `git status --porcelain --untracked-files=all` | Empty once this file is committed; `web/node_modules/` from `npm ci` is ignored | pass |
| `git diff --name-status 092e3d63 22ead6a7` | Thirteen `A` lines, each a CREATE path of the file map: `Directory.Packages.props`, `global.json`, `src/Directory.Build.props`, `src/EpiInfo.Api/EpiInfo.Api.csproj`, `src/EpiInfo.Api/appsettings.json`, `src/EpiInfo.Data/EpiInfo.Data.csproj`, `tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj`, `tests/EpiInfo.CheckCode.Tests/EpiInfo.CheckCode.Tests.csproj`, `tests/EpiInfo.Data.Tests/EpiInfo.Data.Tests.csproj`, `tests/EpiInfo.Domain.Tests/EpiInfo.Domain.Tests.csproj`, `web/.gitignore`, `web/package-lock.json`, `web/package.json`; this file adds `A docs/parity-notes.md`. No `M` line yet, because `README.md` is not yet updated | pass |
| `git diff --name-status 092e3d63 22ead6a7 -- testdata` | Empty | pass |

These checks hold for the snapshot only. The run's closing check repeats them on the completed tree, where `M README.md` is also expected.

## Boundary checks

### Legacy `Tests/` versus new `tests/` (A14)

- **`git config core.ignorecase`**: unset (exit status 1).
- **File system**: ext4 on Linux, case-sensitive, so `Tests/` and `tests/` are two directories.
- **`git ls-files tests Tests`**:

  ```text
  Tests/CreateMapImage.cs
  Tests/Properties/AssemblyInfo.cs
  Tests/Tests.csproj
  Tests/packages.config
  tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj
  tests/EpiInfo.CheckCode.Tests/EpiInfo.CheckCode.Tests.csproj
  tests/EpiInfo.Data.Tests/EpiInfo.Data.Tests.csproj
  tests/EpiInfo.Domain.Tests/EpiInfo.Domain.Tests.csproj
  ```

- **Collision**: none. No tracked `tests/` path, and no planned `tests/` path in the file map, shares a name with one of the four legacy files, so a case-insensitive checkout that merges the two folders also has no same-name file.
- **`tests/Directory.Build.props` and `Tests/Tests.csproj`**: on this ext4 checkout, MSBuild's upward search from `Tests/` never reaches `tests/`, so the props file is not imported at all: `dotnet msbuild Tests/Tests.csproj -getProperty:DirectoryBuildPropsPath -getProperty:Nullable -getProperty:TargetFramework` reports all three empty.
- **Guard check for a case-insensitive checkout** (pass, run once the props file was added, SDK 10.0.115, evaluation only, legacy project never built or edited). A copy of `Tests/Tests.csproj` was evaluated beside a copy of the props file, the layout a merged `Tests/` and `tests/` folder gives:
  - `-preprocess` shows the props file imported, through the `Microsoft.Common.props` import that `Microsoft.CSharp.targets` makes for old-style projects;
  - `UsingMicrosoftNETSdk` is empty, and `TargetFramework`, `Nullable`, `WarningsAsErrors`, `ImplicitUsings`, `IsPackable`, `IsTestProject` and the `Using` items are all empty, while `TargetFrameworkVersion` stays `v4.6.1`;
  - a control copy of the props file with both conditions removed sets every one of them, so the `'$(UsingMicrosoftNETSdk)' == 'true'` guard is what keeps the legacy project unaffected;
  - the four SDK-style test projects evaluate `UsingMicrosoftNETSdk` as `true` and take every setting.
- **Outcome**: verified for this build machine.

### `src/`, `web/` and `docs/` (C-11, A19)

- `git ls-tree --name-only 092e3d63 src web tests` is empty: none of the three folders existed before the run, so none holds legacy content.
- At the baseline `docs/` held `Home.md`, `Home_EI7Grid.png` and `Rates.docx`, which are legacy content. They are left unchanged; the only file the run adds to `docs/` is `docs/parity-notes.md`.
- **Outcome**: verified.

### Enter-caller inventory (A18)

**Search rule.** Files `*.csproj`, `*.cs`, `*.config`, `*.mnu*` and `*.vdproj` outside `Epi.Windows.Enter`, `Epi.Enter.CheckCodeEngine` and `Epi.Core.EnterInterpreter`, searched for `Epi.Windows.Enter`, `Enter.exe`, `EnterWindowsModule`, `EnterMainForm`, `IEnterCheckCode` and `EnterCheckCodeEngine`:

```text
git grep -n -I -E 'Epi\.Windows\.Enter|Enter\.exe|EnterWindowsModule|EnterMainForm|IEnterCheckCode|EnterCheckCodeEngine' -- '*.csproj' '*.cs' '*.config' '*.mnu*' '*.vdproj' ':!Epi.Windows.Enter/**' ':!Epi.Enter.CheckCodeEngine/**' ':!Epi.Core.EnterInterpreter/**'
```

The search ran on the clean checkout and returned 30 lines, classified below.

| Path:line | Class | Owning project | `Epi Info 7.sln` line |
|-----------|-------|----------------|-----------------------|
| `Epi.Menu/Epi.Menu.csproj:121-123` | live dependency: project reference to `Epi.Windows.Enter` | `Epi.Menu` | 81 |
| `Epi.Windows.Menu/Epi.Windows.Menu.csproj:224-226` | live dependency: project reference to `Epi.Windows.Enter` | `Epi.Windows.Menu` | 5 |
| `Epi.Menu/EpiInfoMenuManager.cs:252` | live dependency: code use, `new Epi.Windows.Enter.AnalyticsViewer(null)` | `Epi.Menu` | 81 |
| `Epi.Menu/EpiInfoMenuManager.cs:33, 56, 92, 116` | launch of `Enter.exe` | `Epi.Menu` | 81 |
| `Epi.Windows.MakeView/PresentationLogic/GuiMediator.cs:1848` | launch of `Enter.exe` | `Epi.Windows.MakeView` | 13 |
| `Epi.Windows.Menu/MainWindow.xaml.cs:366` | launch of `Enter.exe` | `Epi.Windows.Menu` | 5 |
| `Epi.Windows.Menu/MenuMainForm.cs:451` | launch of `Enter.exe` | `Epi.Windows.Menu` | 5 |
| `Epi.Core/Configuration_Static.cs:992` | configuration: module entry `Epi.Windows.Enter.EnterWindowsModule, Enter` | `Epi.Core` | 30 |
| `Epi.Core/Resources/Configuration/EpiInfo.mnu.2:86` | configuration: menu command `Execute Enter.exe` | `Epi.Core` | 30 |
| `Setup/Setup.vdproj:5554, 5555, 6011, 6015, 7199, 7203, 11028, 11029` | installer content: `Enter.exe`, its `.config` and `.manifest` | `Setup` | 102 |
| `Epi.Menu/EpiInfoMenuManager.cs:29` | textual: `//` comment | `Epi.Menu` | 81 |
| `Epi.Windows.MakeView/Forms/MakeViewMainForm.cs:649` | textual: `//` comment | `Epi.Windows.MakeView` | 13 |
| `Epi.Windows.Analysis/EntryPoint.cs:61` | textual: inside the `/* */` block at lines 51-87 | `Epi.Windows.Analysis` | 17 |
| `Epi.Windows.AnalysisDashboard/EntryPoint.cs:113` | textual: inside the `/* */` block at lines 103-139 | `Epi.Windows.AnalysisDashboard` | 56 |
| `Epi.Windows.Mapping/EntryPoint.cs:56` | textual: inside the `/* */` block at lines 48-82 | `Epi.Windows.Mapping` | 54 |
| `EpiMenuCommandPlugin/EpiMenuEventGrammar.cs:50, 74, 134` | textual: a `//` comment at 50, and the `/* */` blocks at lines 73-99 and 133-146 | `EpiMenu.CommandPlugin` | 48 |

**Against the static finding (planning time)**, which counted 3 project references, 4 `Enter.exe` launches, 2 configuration entries, the installer, 0 live code uses and 2 textual hits:

- **Project references**: 2 under the rule's tokens. Widening the search to the other Enter project names finds one more, `Epi.Windows/Epi.Windows.csproj:644`, a reference to `Epi.Core.EnterInterpreter`; `Epi.Windows` is in `Epi Info 7.sln` at line 22.
- **Launches**: 7 call sites in 4 files, which matches the planning count of 4 when counted per file.
- **Configuration entries and installer**: as counted.
- **Live code use**: 1, `Epi.Menu/EpiInfoMenuManager.cs:252`, where planning found none. It sits in `Epi.Menu`, which already references `Epi.Windows.Enter` and is in the legacy solution.
- **Textual hits**: 8 lines in 6 files, including the two named at planning time (`Epi.Windows.Analysis/EntryPoint.cs:51-61` and `EpiMenuCommandPlugin/EpiMenuEventGrammar.cs:73-99`).

**Outcome**: verified. Every owning project is in `Epi Info 7.sln`, the installer included, so there is no live dependency, launch or configuration entry outside the legacy solution. Every hit is left untouched.

## Assumptions

"Direct SQL" is a query run on the rehearsal database at write-up, 2026-11-25. It confirms the seeded data the assumption names, but not the behaviour of the new code, which only the named test proves.

| Id | Assumption and settling source | Run-time check | Static finding (planning time) | Outcome | Evidence; decision |
|----|--------------------------------|----------------|--------------------------------|---------|--------------------|
| A1 | The seeded forms and `testdata/` scripts load and verify, every `99_verify` row `ok = 1` (`testdata/sql/99_verify.sql:4-12`) | Database precondition | Scripts present; not runnable at planning time | verified | Nine rows, each `ok = 1`, at 2026-11-25T10:32:38Z ("Database precondition") |
| A2 | Legacy navigation orders by `UniqueKey` and excludes `RECSTATUS=0` (`Epi.Core/Data/Services/CollectedDataProvider.cs:1422-1878`, `Epi.Enter.CheckCodeEngine/RunTimeView.cs:673-694`, `Epi.Windows.Enter/PresentationLogic/GuiMediator.Event.Handlers.cs:336-340`) | `NavigationTests`: the live distinct `RecStatus` values are a subset of {0, 1} | Ordering by `UniqueKey` holds. Exclusion does not: legacy counts and steps through deleted records and shows them disabled | not run: `NavigationTests` is not in the tree | Direct SQL: `CaseReport` has `RecStatus` 0 × 17 and 1 × 1,392; `FoodHistory` has 0 × 12 and 1 × 597. Decisions C-5, C-16 |
| A3 | The required-field wording is recoverable from legacy resources (`Epi.Core/SharedStrings.resx:637-639`) | `RecordValidatorTests` pins the copied text | Found: `FIELD_IS_REQUIRED_LONG` | not run: `RecordValidatorTests` and `ValidationMessages.cs` are not in the tree | Re-read at write-up: `{0} is a required field (page {1}). Please fill in this field before attempting to leave the record, save data, or close the form. `, trailing space included. Decision C-12 |
| A4 | The 24 in-scope scenarios use only the subset, and `metaViews.CheckCode` holds both programs (`testdata/sql/03_forms.sql:metaViews`, `testdata/scenarios/check_code_scenarios.json`) | `CheckCodeScenarioTests`, failing on any `warning` or `error` diagnostic except for `EC-GEO-01` | Both programs present. Constructs used: `Field` blocks, `IF/ELSE/END-IF`, `=`, `NOT`, `AND`, `(.)`, `(-)`, `ENABLE`, `DISABLE`, `CLEAR`, `ASSIGN`, `DIALOG`, `YEARS`, `EPIWEEK`, `GEOCODE` | not run: the harness and the interpreter are not in the tree | Direct SQL: `metaViews.CheckCode` holds 443 characters for `CaseReport` and 2,428 for `FoodHistory`. A failing scenario keeps legacy behaviour where known and gets a "Parity gaps" entry |
| A5 | Soft delete sets `RECSTATUS=0` on the base row only and leaves page rows in place (`Epi.Windows.Enter/PresentationLogic/GuiMediator.Event.Handlers.cs:641-722`, `Epi.Core/Data/Services/CollectedDataProvider.cs:1127-1220`) | `StoredDataContractTests` asserts the target invariant | Legacy does more: where the stamp columns exist, it writes the base status three times. First it captures the on-screen values, sets `RecStatus` to 0 and saves (`GuiMediator.Event.Handlers.cs:643-648`): one base `UPDATE` writes the last-save stamps with that 0, then every page row is rewritten (`CollectedDataProvider.cs:1145-1150, 1169-1218`). The cascade then starts with the record itself: it reloads it, sets `RecStatus` to 0 and saves it again, a second base `UPDATE` with new last-save stamps and a second rewrite of every page row. Only then does it reach related-form records: it saves each child-view record whose `FKEY` matches the same way and sets grid rows to 0 (`GuiMediator.Events.Canvas.cs:397-447`). Last, the handler sets `RecStatus` to 0 a third time on the base row, recursing into grid tables and child-view rows (`GuiMediator.Event.Handlers.cs:649-661, 685-722`). Page rows remain | not run: `StoredDataContractTests` is not in the tree | Decision C-6 ("Request-mandated differences") |
| A6 | The listed legacy paths exist as named | Re-listing before reading | Present; four requested names resolve to nearest equivalents | verified | Every path in "Legacy paths" exists at the snapshot; file counts re-taken with `git ls-files` |
| A7 | Neither seeded form needs relate fields (`testdata/sql/03_forms.sql:metaFields`) | `SqlMetadataReaderTests` | No `FieldTypeId` 20 row | verified by direct SQL; `SqlMetadataReaderTests` not run | `metaFields` rows with `FieldTypeId = 20`: 0. Views with `IsRelatedView = 1`: 0. Relate is not delivered ("Undelivered items") |
| A8 | The supported types cover the two seeded forms (`metaFields` ⋈ `metaFieldTypes`) | `SqlMetadataReaderTests`, `FieldTypeMapperTests` | Partly not: Phone Number (4 fields) and Comment Legal (18) map to `text`; Yes/No (7) maps to `unsupported` (C-2); Command Button (1) is unsupported | verified by direct SQL for the counts; both tests not run | Page-bound rows: Phone Number 3 on `CaseReport` and 1 on `FoodHistory`, Comment Legal 18, Yes/No 7, Command Button 1 (`GetCoordinates`). Per type in "Field types found" |
| A9a | Legal-value lists come from metadata and the code table `metaFields.SourceTableName` (`testdata/sql/04_code_tables.sql`, `Epi.Core/Data/Services/MetadataDbProvider.cs:170-199`) | `SqlMetadataReaderTests` compares each Legal Values field's options with its code-table rows | Seeded Legal Values fields name code tables with `TextColumnName` set | verified by direct SQL for the tables and columns; the option comparison not run | The three Legal Values fields (`FoodHistory` `Sex`, `EthnicityGroup`, `LooseStoolsPerDay`) name `codephvs_administrativesex_hl7_2x`, `codephvs_ethnicitygroup_cdc` and `codeloosestoolsperday`; each table and its `TextColumnName` column exist. No field has `Sort = 1` |
| A9b | Option lists come from metadata (`Epi.Core/Services/Fields/OptionField.cs:84-102`, `Epi.Windows.Enter/Forms/canvas.render.cs:184-200`) | `FieldTypeMapperTests`, `FormDefinitionFactoryTests` with synthetic metadata | Legacy takes labels from `metaFields.List` before `\|\|`, not from `SourceTableName`, and stores the 0-based index. No seeded field is an Option | not run: both tests are not in the tree | Direct SQL: no page-bound `Option` (`FieldTypeId` 12) row, so no seeded data can confirm it |
| A10 | Search matches `FindRecordDialog` (`Epi.Windows.Enter/Dialogs/FindRecordDialog.cs:103-120, 293-374, 404-770`, `Epi.Core/Data/Services/CollectedDataProvider.cs:1890-2380`) | `SearchCriteriaParserTests`, `ListAndSearchTests` | Does not match the request's "contains": legacy uses equality with wildcards, `" or "` alternatives, comparison prefixes and its own Phone Number path | not run: both tests are not in the tree | Decisions C-4, C-18 ("Search semantics") |
| A11 | The logon stamp is the OS user of the API process (`Epi.Core/Data/Services/CollectedDataProvider.cs:963-1004`) | `StoredDataContractTests` | Legacy writes `WindowsIdentity.GetCurrent().Name` (`DOMAIN\user`); the target writes `Environment.UserName`, as the request decides | not run: `StoredDataContractTests` is not in the tree | Format difference recorded under "Request-mandated differences" |
| A12 | SC-8's 43/74 totals count every `metaFields` row of the view (`testdata/sql/03_forms.sql:metaFields`, `testdata/sql/05_data_tables.sql`) | `FormDefinitionTests`, `SqlMetadataReaderTests` | Not so: 64/91 rows, 61/88 on pages; 43/74 are page-table data columns | verified by direct SQL; both tests not run | 64/91 rows; 61/88 page-bound (61; 55 and 33 per page); 43/74 data columns. Decision C-7 ("Field types found") |
| A13 | `EC-GEO-01` and `DATA-01` are among the 24 or covered elsewhere | `CatalogCounts` | `DATA-01` is in the 24; `EC-GEO-01` is the 25th entry (`inScope: false`) | not run: `CatalogCounts` is not in the tree | Static finding from reading `testdata/scenarios/check_code_scenarios.json` at write-up: 25 entries; 24 with `inScope: true` (19 field events, 3 saves `EC-VAL-01`..`EC-VAL-03`, 2 data cases `DATA-01`, `DATA-02`); `EC-GEO-01` with `inScope: false` |
| A14 | Legacy `Tests/` and new `tests/` cause no collision on the build machine | Boundary-check step | No shared names in the planned paths; planning on Linux with `core.ignorecase` unset | verified | "Boundary checks" |
| A15 | `FKEY` is written as legacy writes it for a parentless form (`Epi.Core/Data/Services/CollectedDataProvider.cs:936-961`) | `StoredDataContractTests` (`NULL` on created rows); `SqlMetadataReaderTests` (no foreign-key field) | Legacy names the column only when a foreign-key field exists, and neither form has one. All 2,018 seeded rows hold `NULL` | verified by direct SQL for the seeded rows; both tests not run | Non-`NULL` `FKEY`: 0 of 2,018 base rows. Fields with `FieldTypeId = 24` (ForeignKey): 0 |
| A16 | Table and column names come from the seeded database (`Epi.Core/Data/Services/MetadataDbProvider.cs:2165-2192`, `Epi.Core/Page.cs:219-231`) | `SqlMetadataReaderTests` | Base tables `CaseReport`, `FoodHistory`; page tables `CaseReport1`, `FoodHistory2`, `FoodHistory3`; status column spelled `RecStatus` | verified by direct SQL; `SqlMetadataReaderTests` not run | `metaFields.DataTableName` holds `CaseReport` and `FoodHistory`; the page tables named view table + `PageId` (1; 2 and 3) exist; both base tables spell the column `RecStatus` (`smallint`) |
| A17 | Column families match the mapping table (`testdata/sql/05_data_tables.sql`, `sys.columns`) | `SqlMetadataReaderTests` | Matches, with Yes/No as `tinyint` and no Option field | verified by direct SQL; `SqlMetadataReaderTests` not run | Families per table in "Field types found"; no mismatch |
| A18 | No other legacy caller of Enter interfaces exists outside the legacy solution | Boundary-check step | Every caller found is in `Epi Info 7.sln` or its configuration and installer | verified | "Boundary checks": the repeat found one live code use and more hit lines than planning counted, all inside the legacy solution |
| A19 | `src/`, `web/` and `docs/` hold no legacy content | Boundary-check step | `docs/` holds three legacy files | verified | "Boundary checks"; decision C-11 |

## Legacy paths

Every path below was re-listed at the snapshot and exists (A6). All are reference only: never edited and never part of a build.

| Requested reference | Repository path(s) | Read for | Re-listed |
|---------------------|--------------------|----------|-----------|
| `Forms/EnterMainForm.cs` | `Epi.Windows.Enter/Forms/EnterMainForm.cs` | Toolstrip, navigation and new-record display, permissions (`:1095-1125, 1455-1500, 2015-2070`) | confirmed |
| `Canvas*.cs` | `Epi.Windows.Enter/Forms/Canvas.cs`, `Epi.Windows.Enter/Forms/Canvas.control.events.cs`: 2 of 3, since `Canvas.Designer.cs` is designer-only | Field rendering and control events | confirmed |
| `canvas.render.cs` | `Epi.Windows.Enter/Forms/canvas.render.cs` | Value display, including the Option index (`:184-200`) | confirmed |
| `ViewExplorer.cs` | `Epi.Windows.Enter/Forms/ViewExplorer.cs` | Page list and order | confirmed |
| `PresentationLogic/ControlFactory.cs` | `Epi.Windows.Enter/PresentationLogic/ControlFactory.cs` | Control per field type, masks (`:379-393`), dropdown sort (`:472-506`) | confirmed |
| `GuiMediator*.cs` | `Epi.Windows.Enter/PresentationLogic/{GuiMediator.cs, GuiMediator.Event.Handlers.cs, GuiMediator.Events.Canvas.cs, GuiMediator.Events.MainForm.cs, GuiMediator.Events.ViewExplorer.cs, GuiMediator.IEnterCheckCode.cs}`, 6 files | Navigation, saving, delete, the required gate, check-code effects (`GuiMediator.Event.Handlers.cs:237-340, 641-722`; `GuiMediator.Events.ViewExplorer.cs:58-139`; `GuiMediator.IEnterCheckCode.cs:960-980, 1631-1694, 2086-2220`) | confirmed |
| `Controls/*.cs` | `Epi.Windows.Enter/Controls/{CueTextBox, CustomRadioButton, DateTimePicker, LegalValuesComboBox, ListFieldComboBox, YesNoComboBox}.cs`, 6 files | Control behaviour per type | confirmed |
| `Dialogs/FindRecordDialog.cs` | `Epi.Windows.Enter/Dialogs/FindRecordDialog.cs` | Search criteria (`:103-120, 293-374, 404-770`) | confirmed |
| Not listed; added for the P2 switches | `Epi.Windows.Enter/EntryPoint.cs` | Command-line switches `title`, `project`, `view`, `record` (`:24-27`) | confirmed |
| `Project.cs`, `View.cs`, `Page.cs` | `Epi.Core/Project.cs`, `Epi.Core/View.cs`, `Epi.Core/Page.cs` | Form model, empty record (`View.cs:1397-1442`), page table name (`Page.cs:219-231`) | confirmed |
| `Services/Fields/**` | `Epi.Core/Services/Fields/`: 54 top-level C# files, plus `Fields.cd` and the `Grid Columns/` folder | The classes of the seeded types and of the types the request names: `SingleLineTextField`, `MultilineTextField`, `NumberField`, `DateField`, `CheckBoxField`, `YesNoField`, `PhoneNumberField`, `DDLFieldOfCommentLegal`, `DDLFieldOfLegalValues`, `OptionField` (`:45-56, 84-102`), `GrouplField`, `LabelField`, `CommandButtonField`, `RelatedViewField`, `GridField`, `ImageField`, `MirrorField`, `GUIDField`; base classes and interfaces only as needed | confirmed |
| `Data/Services/MetadataDbProvider.cs` | `Epi.Core/Data/Services/MetadataDbProvider.cs` | Code-table read (`:170-199`), base table name (`:2165-2192`) | confirmed |
| `Data/Services/CollectedDataProvider.cs` | `Epi.Core/Data/Services/CollectedDataProvider.cs`, Enter methods only, within `:927-2380` | `SaveRecord` (`:927, 1043, 1127, 1225`), `UpdateBaseTable` (`:1169`), `GetRecordCount` (`:1422`), `GetFirstRecordId` (`:1470`), `GetLastRecordId` (`:1519`), `LoadRecordIntoView` (`:1568, 1652`), `GetPreviousRecordId` (`:1773`), `GetNextRecordId` (`:1830`), `GetSearchRecords` (`:1890`), `JoinTables` (`:2317`). Skipped: the EWE/WCF code at `:720-834` (`SaveAsResponse`, `worker_SaveAsResponse`, `worker_SaveAsResponseCompleted`) | confirmed |
| `Data/Services/AppData.cs` | `Epi.Core/Data/Services/AppData.cs` | Pattern-to-mask table | confirmed |
| `DbDriverBase.cs`, `IDbDriver.cs`, `Query*.cs`, `Table*.cs` | `Epi.Core/Data/{DbDriverBase.cs, IDbDriver.cs, Query.cs, QueryParameter.cs, Table.cs, TableColumn.cs}` | Identifier escaping, parameter types | confirmed |
| `Epi.Data.SqlServer/**` | `Epi.Data.SqlServer/{SqlDatabase.cs, SqlDatabase.IDataSource.cs, SqlDBFactory.cs, SqlQuery.cs, SqlException.cs}`: 5 behavioural files of its 17 C# source files | Connection, timeout, permissions (`SqlDatabase.cs:1804-1935`), errors | confirmed |
| `Epi.Enter.CheckCodeEngine/**` | `Epi.Enter.CheckCodeEngine/{EnterCheckCodeEngine.cs, RunTimeView.cs, StackCommand.cs, Events/OpenViewEventArgs.cs, Events/RunCheckCodeEventArgs.cs}`: the 5 behavioural sources; the sixth C# file, `Properties/AssemblyInfo.cs`, is not read | Event dispatch, position formula (`RunTimeView.cs:673-694`) | confirmed |
| `cAST.cs`, `EnterRule.cs`, `IEnterCheckCode.cs` | `Epi.Core.EnterInterpreter/{cAST.cs, EnterRule.cs, IEnterCheckCode.cs}` | AST shape, host interface | confirmed |
| `Rule_Context.cs` (nearest equivalent) | `Epi.Core.EnterInterpreter/Rule_Context.cs` | Evaluation context | confirmed |
| `EpiInterpreterParser` (nearest equivalent) | `Epi.Core.EnterInterpreter/EpiInfo7EventGrammar.cs:48`, `public class EpiInterpreterParser` | Parser entry | confirmed |
| `Rules/` for the subset | `Epi.Core.EnterInterpreter/Rules/`: 58 top-level C# files. Read: `Rule_If_Then_Else_End`, `Rule_Else_If_Statement` (to detect it), `Rule_Enable`, `Rule_Disable`, `Rule_Clear`, `Rule_Assign`, `Rule_Dialog`, `Rule_Geocode`, `Rule_CompareExp`, `Rule_AndExp`, `Rule_NotExp`, `Rule_Value`, `Rule_Expression`, `Rule_Statement`, `Rule_Statements`, `Rule_Field_Checkcode_Statement`, `Rule_Begin_{Before,After,Click}_Statement`, and to recognise and skip them `Rule_Page_Checkcode_Statement`, `Rule_Record_Checkcode_Statement`, `Rule_View_Checkcode_Statement`, `Rule_DefineVariables_Statement`, `Rule_Subroutine_Statement` | Statement semantics | confirmed |
| `Rules/Functions/Rule_Years.cs` | `Epi.Core.EnterInterpreter/Rules/Functions/Rule_Years.cs` | Exact algorithm (`:49-72`) | confirmed |
| The EpiWeek function (nearest equivalent) | `Epi.Core.EnterInterpreter/Rules/Functions/Rule_EPIWEEK.cs` | Exact algorithm | confirmed |
| The grammar (nearest equivalent) | `Epi.Core.EnterInterpreter/grammar/EpiInfo.Enter.Grammar.grm` | Language specification | confirmed |

**Reference only, not translated:**

- `Epi.Windows/**`, the shared `MainForm` and docking.
- Every `*.Designer.cs`, for example `Epi.Windows.Enter/Forms/Canvas.Designer.cs`, `Epi.Windows.Enter/Forms/ViewExplorer.Designer.cs` and `Epi.Data.SqlServer/Properties/Settings.Designer.cs`.
- Every `*.resx`, except the one string of `Epi.Core/SharedStrings.resx:637-639`, copied verbatim and without any resource mechanism (C-12).

**Not read:**

- `Epi.Data.SqlServer/Forms/**`, the connection dialogs: 10 C# source files with their `.resx`.
- `Epi.Core.EnterInterpreter/grammar/EpiInfo.Enter.Grammar.cgt` and `Epi.Core/GoldParserEngine.dll`, which the request excludes.

## Field types found

Counts come from direct SQL on `metaFields` ⋈ `metaFieldTypes` (page-bound rows) and `sys.columns` ⋈ `sys.types`; the data-field counts and column types match `testdata/forms.json`. The `fieldType` column is the target mapping. `SqlMetadataReaderTests` and `FieldTypeMapperTests` re-confirm both and did not run at the snapshot.

### `CaseReport` (page table `CaseReport1`)

| `metaFieldTypes.Name` | `fieldType` | Data fields (page-table columns) | Column type |
|-----------------------|-------------|----------------------------------|-------------|
| Text | `text` | 16 | `nvarchar(4000)` |
| Phone Number | `text` | 3 | `nvarchar(20)` |
| Comment Legal | `text` | 18 | `nvarchar(255)` |
| Number | `number` | 2 | `float` |
| Date | `date` | 4 | `datetime` |
| Label/Title | `label` | none (14 `metaFields` rows) | no column |
| Group | `group` | none (4 rows) | no column |
| **Total** | | **43** | |

### `FoodHistory` (page tables `FoodHistory2` and `FoodHistory3`)

| `metaFieldTypes.Name` | `fieldType` | Data fields (page-table columns) | Column type |
|-----------------------|-------------|----------------------------------|-------------|
| Text | `text` | 8 (6 + 2) | `nvarchar(4000)` |
| Phone Number | `text` | 1 (1 + 0) | `nvarchar(20)` |
| Multiline | `multiline` | 2 (1 + 1) | `ntext` |
| Number | `number` | 6 (6 + 0) | `float` |
| Date | `date` | 7 (6 + 1) | `datetime` |
| Checkbox | `checkbox` | 40 (17 + 23) | `bit` |
| Legal Values | `legalValues` | 3 (3 + 0) | `nvarchar(255)` |
| Yes/No | `unsupported` (C-2) | 7 (5 + 2): `ILL`, `DoctorVisit`, `Hospitalized`, `Antibiotics`, `Died`; `StoolSubmitted`, `O157H7isolated` | `tinyint` |
| Label/Title | `label` | none (9 rows) | no column |
| Group | `group` | none (4 rows) | no column |
| Command Button | `unsupported` | none (1 row, `GetCoordinates`) | no column |
| **Total** | | **74 (45 + 29)** | |

**Absent from both forms**: Option, Relate, Grid, Image, Mirror and GUID, as well as Text (Uppercase), Time, Date/Time, Codes and List. No field has `IsEncrypted = 1`.

### Type names (C-9)

The mapper matches the seeded `metaFieldTypes` names (`testdata/sql/02_reference_data.sql:metaFieldTypes`), after lower-casing and removing spaces: `Label/Title`, `Text` and `Legal Values`, where the request says "Label", "Single-line text" and "Legal-values dropdown". All 28 seeded names were read; the ones in use are those in the two tables above.

### Column families

| Table | Column types (count) |
|-------|----------------------|
| `CaseReport`, `FoodHistory` (base) | `int` 1 (`UniqueKey`), `smallint` 1 (`RecStatus`), `nvarchar(255)` 4 (`GlobalRecordId`, `FKEY`, `FirstSaveLogonName`, `LastSaveLogonName`), `datetime` 2 (`FirstSaveTime`, `LastSaveTime`) |
| `CaseReport1` | `nvarchar(4000)` 16, `nvarchar(255)` 19 (18 + `GlobalRecordId`), `nvarchar(20)` 3, `float` 2, `datetime` 4 |
| `FoodHistory2` | `nvarchar(4000)` 6, `nvarchar(255)` 4 (3 + `GlobalRecordId`), `nvarchar(20)` 1, `ntext` 1, `float` 6, `datetime` 6, `bit` 17, `tinyint` 5 |
| `FoodHistory3` | `nvarchar(4000)` 2, `nvarchar(255)` 1 (`GlobalRecordId`), `ntext` 1, `datetime` 1, `bit` 23, `tinyint` 2 |

- **Families seen**: `bit`, `datetime`, `float`, `int`, `ntext`, `nvarchar(n)` (20, 255, 4000), `smallint` and `tinyint`; `int` and `smallint` occur only in the base tables.
- **Column-type mismatches**: none found by direct SQL. Every supported field's column is in its admitted set, and Yes/No falls to `unsupported` under the non-character rule (C-2), not as a mismatch. `SqlMetadataReaderTests` did not run.
- **Option index range**: an Option field is admitted only when the 0-based index of its last item fits its integer column, so 257 items on `tinyint` leave it `unsupported`; no seeded field is an Option.

### Patterns and other field rules

| Pattern | Fields | Server rule |
|---------|--------|-------------|
| `###-###-####` | `CaseReport` `PhoneNumber`, `PhysicianPhone`, `PersonCompletingFormPhone` | Text `#` mask: exact length, a digit at each `#`, the literal elsewhere |
| `###` | `CaseReport` `Age`, `AntibioticUseDays` | Number mask: at most three integer digits, no decimals; a leading sign is allowed and does not count as an integer digit |
| `###.#` | `FoodHistory` `FeverTemp` | Number mask: at most three integer digits and one decimal digit; a leading sign is allowed and does not count as an integer digit |
| `MM-DD-YYYY` | 11 date fields | Display and entry format for the client only; not checked on ISO values |

- **Other patterns**: none found, so no field is left with "no server check". The `FoodHistory` phone field has no pattern.
- **Ranges** (`Lower`/`Upper`): `CaseReport` `Age` 0–120 and three date ranges; `FoodHistory` `FeverTemp` 80–110 and six date ranges such as `01-15-2011`–`12-31-2012`.
- **`MaxLength`**: `Zip` = 5, the only field with a value above 0.
- **Read-only**: `StateIDNumber`, `CDCIDNumber`, `ReportedToCDCDate` and `OnsetWeek`.
- **Required**: no field has `IsRequired = 1` (direct SQL: 0 of the 155 `metaFields` rows). The required gate's proof is therefore planned with synthetic metadata through `FakeDataStore` in `ValidationTests` (SC-6), and is `not run` at the snapshot because `ValidationTests` is not in the tree.

### SC-8 reconciliation (C-7)

| Measure | `CaseReport` | `FoodHistory` |
|---------|--------------|---------------|
| `metaFields` rows | 64 | 91 |
| System rows with a null `PageId` (`UniqueKey`, `RecStatus`, `GlobalRecordId`), not listed | 3 | 3 |
| Page-bound rows listed in the form definition | 61 (`Page 1`: 61) | 88 (`Page 1`: 55, `Page 2`: 33) |
| Page-table data columns, the request's 43/74 | 43 | 74 |
| DTO data fields | 43 | 67 (74 minus the seven Yes/No fields) |

`metaFields.ControlFontSize` is created as `decimal(2)`, so 8.25 is stored as 8 (`testdata/README.md`, finding 5). That legacy schema quirk is left as it is, and the new application does not read the column.


## Request-mandated differences

Each entry states the decided target behaviour and its legacy source. The test that proves the delivered code follows it is named; at the snapshot none of those tests is in the tree.

### Intentional behaviour changes

These are the only two departures the request designates as intentional changes.

| Change | Legacy | Target | Owner; proof |
|--------|--------|--------|--------------|
| Transactional save | Base and page statements run with no transaction (`Epi.Core/Data/Services/CollectedDataProvider.cs:927-1392`) | Insert, update and soft delete each run in one `SqlTransaction` at `READ COMMITTED`, so no partial record is ever committed. An exception before commit triggers a rollback attempt. The response's `writeOutcome` says what is known about the data. It is `notWritten`, the only outcome on which a `POST` (insert) or `PUT` (update) is resent automatically, when no DML was sent, the rollback completed without error, or the server reported the transaction aborted as deadlock victim (1205). It is `unknown` when DML was sent and neither a commit nor a completed rollback can be confirmed: the commit call failed or did not return, or the rollback itself failed. It is `written` when the commit succeeded and a later step failed, such as the post-commit log line or serialisation. After `unknown` or `written`, a `POST` or `PUT` is not resent automatically. A `DELETE` (soft delete) repeats safely whatever its `writeOutcome`, because a `DELETE` of an already-deleted record returns 204 and writes nothing | `src/EpiInfo.Data/Records/SqlRecordRepository.cs`; `TransactionalSaveTests` (SC-7) |
| Command timeout | 1,500 s (`Epi.Data.SqlServer/SqlDatabase.cs:103`; `Epi.Core/Data/DBReadExecute.cs:585, 655, 755`) | `Database:CommandTimeoutSeconds`, default 30 s, settable as `Database__CommandTimeoutSeconds` | `src/EpiInfo.Data/DatabaseOptions.cs`, `src/EpiInfo.Api/appsettings.json` |

### Request requirements

- **C-2: Yes/No fields are never written.**
  - Legacy: the seven Yes/No fields use `tinyint` columns in both page tables (`Epi.Core/Services/Fields/YesNoField.cs:95-96, 192-195`; `Epi.Data.SqlServer/SqlDatabase.cs:316-317`): `ILL`, `DoctorVisit`, `Hospitalized`, `Antibiotics` and `Died` in `FoodHistory2` (`testdata/sql/05_data_tables.sql:104, 112-114, 117`), and `StoolSubmitted` and `O157H7isolated` in `FoodHistory3` (`testdata/sql/05_data_tables.sql:149-150`). Enter writes them. A selected Yes is stored as 1 and a selected No as 0 (`Epi.Windows.Enter/PresentationLogic/GuiMediator.cs:1888-1902`). No selection is stored as `NULL` (`GuiMediator.cs:1925-1927`; `Epi.Core/Data/Services/CollectedDataProvider.cs:1079-1085, 1293-1299`). The seeded rows hold only 0, 1 and `NULL`, with 761 `NULL` cells across the seven columns (direct SQL at write-up).
  - Target: a seeded type outside both type lists maps to `unsupported` unless its column is character, so Yes/No renders as a disabled placeholder naming the type, is absent from `values`, is never written and is not searchable. An insert leaves those columns `NULL`, and an update leaves them unchanged. The request's "plain text input, saved as text" applies to the character-column types Phone Number and Comment Legal, which map to `text`.
- **C-3: Read-only fields are never written from the client.**
  - Legacy: check-code `ASSIGN` results are saved into read-only fields. Seeded `OnsetWeek` is read-only and assigned by `ASSIGN OnsetWeek = EPIWEEK(OnsetDate)` (`FoodHistory` check code line 18, `testdata/sql/03_forms.sql:72`).
  - Target: a read-only field present in `values` is a 400. The client never sends read-only fields (`StateIDNumber`, `CDCIDNumber`, `ReportedToCDCDate`, `OnsetWeek`); an assigned read-only value is shown on screen and not saved. Proof: `ValidationTests`, `fieldEventQueue.test.ts`.
- **C-5: Deleted records are hidden by default.**
  - Legacy: count, first, last, next and previous apply no `RecStatus` filter, so deleted records are navigated and shown disabled (`Epi.Core/Data/Services/CollectedDataProvider.cs:1422-1878`; `Epi.Enter.CheckCodeEngine/RunTimeView.cs:673-694`; `Epi.Windows.Enter/PresentationLogic/GuiMediator.Event.Handlers.cs:336-340`).
  - Target: `count`, `at`, positions, the list and search use the active set. `includeDeleted=true` reproduces the legacy set for navigation only: `count`, `at`, positions and the navigator. Find differs, because legacy Find keeps only `RecStatus > 0` rows while search with `includeDeleted=true` also returns deleted rows (C-16; "Search semantics"). Proof: `NavigationTests`, `ListAndSearchTests`.
- **C-6: Writes around navigation and delete.**
  - Legacy: a dirty record is saved before leaving it (`Epi.Windows.Enter/PresentationLogic/GuiMediator.Event.Handlers.cs:237-247`) and saved again after the next record opens (`:272-285`). Where the stamp columns exist, delete writes the base status three times. It captures the on-screen values, sets `RecStatus` to 0 and saves: one base `UPDATE` writes the last-save stamps with that 0, then every page row is rewritten. The cascade's reload and save of the record itself repeats both, the second status write, before it reaches related-form and grid rows. The handler then sets `RecStatus` to 0 a third time on the base row and on grid and child-view rows (`:641-663, 685-722`; `Epi.Windows.Enter/PresentationLogic/GuiMediator.Events.Canvas.cs:397-447`; `Epi.Core/Data/Services/CollectedDataProvider.cs:1145-1150, 1169-1218`). Undelete is offered (`:665-683`), and view, page and record events run.
  - Target: save-before-leave is kept, and a 400 keeps the user on the record. The re-save after open is dropped, so a read never writes (`DATA-01`). Delete is confirmed first and discards unsaved edits; it then updates only the base row (`RecStatus = 0`, `LastSaveLogonName`, `LastSaveTime`), with no page rewrite and no cascade. A delete of an already-deleted record writes nothing. Undelete and the view, page and record events are not carried; only field events are. Proof: `StoredDataContractTests`, `ReadEquivalenceTests`, `useRecordEditor.test.ts`.
- **C-10: The empty record.**
  - Legacy: an unchecked checkbox does not make a record non-empty (`Epi.Core/View.cs:1397-1442`), and unchecked checkboxes are stored as 0.
  - Target: the API rejects a `POST` only when every submitted value is `null` once empty text becomes `null` (request-level 400, "the record has no values"). An explicit `false` is a value, so a body of only `false` checkboxes creates a record. The editor follows legacy emptiness: a new record whose only entries are untouched or unchecked checkboxes is never posted and is discarded on leaving. When it does post, it sends `false` for each unchecked writable checkbox, so stored rows hold 0 as legacy writes; read-only checkboxes are never sent. Proof: `ValidationTests`, `useRecordEditor.test.ts`.
- **C-16: One active-set predicate.**
  - Legacy: Find appends `RecStatus > 0` (`Epi.Core/Data/Services/CollectedDataProvider.cs:2249`); navigation filters nothing.
  - Target: the active set is `RecStatus = 1` for `count`, `at`, positions, the list and search, and `includeDeleted=true` selects every row. Find's predicates are therefore legacy `RecStatus > 0` against target `RecStatus = 1` by default, and no status filter with `includeDeleted=true`. With the toggle off, the two differ only for values other than 0 and 1; the live database holds only 0 and 1 (A2). With the toggle on, target search also returns `RecStatus = 0` rows, and any other value at or below 0, which legacy Find excludes. Proof: `NavigationTests`.

### Design reconciliations

- **C-15: Date fields.** A `date` field is `YYYY-MM-DD` on the wire and is stored at 00:00:00 in its `datetime` column. Legacy keeps the time of day (`Epi.Core/Data/Services/CollectedDataProvider.cs:1302-1315`). The time-bearing types, Date/Time and Time, are unsupported and not seeded.
- **C-17: Navigator coordinates.** The Record DTO's `position` and `count` stay intrinsic in every response: over the active set when `recStatus` is 1, over all rows when it is 0. The navigator's "[n] of N" follows the deleted toggle. N is `count` with the toggle's `includeDeleted`; n is the requested `at` position, or the `position` returned by `GET .../records/{globalRecordId}/position?includeDeleted=<toggle>`, and never comes from search. Legacy numbers the open record, whose `UniqueKey` is k, by `Select Count(*) From [<base table>] Where UniqueKey <= k` with no `RecStatus` filter, so its "[n] of N" counts deleted rows too (C-5). It adds a `FKEY` filter only for a related view, and neither seeded form is one (`Epi.Enter.CheckCodeEngine/RunTimeView.cs:673-694`; the query is at `:687`). The target keeps that formula but applies it over the selected set: the active set `RecStatus = 1` (C-16), or every row with `includeDeleted=true`. With the toggle on, n and N equal legacy's. With it off, both leave out deleted rows, so an active record preceded by deleted records has a smaller n.
- **C-19: Five additive extensions**, each optional or extra, so every specified shape still holds:
  - the Field DTO's `members` (a group's field names from `metaFields.List`, `null` otherwise; C-8);
  - search `excludeGlobalRecordId`;
  - a 400 on `GET .../records/count` for an invalid `includeDeleted`;
  - the ProblemDetails `writeOutcome` member (`notWritten`, `unknown`, `written`) on write 500 and 503 responses;
  - `GET .../records/{globalRecordId}/position`, returning `{position, count}` over the selected set without changing the Record DTO (see C-17 for the legacy `UniqueKey <= k` count it replaces). It reads only the base table, so a known record whose page row is missing is still placed and never disappears. A known record outside the selected set, such as a deleted record with the toggle off, returns `position: null` with the set's `count`. When the toggle is switched off while a deleted record is open, the navigator therefore opens `at/{min(n, count)}`. An id not in the base table is a 404, an invalid `includeDeleted` a 400, an unexpected failure a 500 and an unavailable database a 503.
- **C-18: Find.** The target choices are listed under "Search semantics".
- **Delete confirmation.** Legacy asks nothing: the "Mark as Deleted" menu item raises `MarkAsDeletedRecordEvent` straight away (`Epi.Windows.Enter/Forms/EnterMainForm.cs:801-812`). The mediator hands it to `MarkAsDeletedRecordHandler` (`Epi.Windows.Enter/PresentationLogic/GuiMediator.Event.Handlers.cs:54`), which saves the on-screen values as part of the delete (`:641-663`; C-6). A confirmation dialog, stating that unsaved changes to the record will be discarded, comes before any request. Cancel sends nothing. Confirm sends only the base-only `DELETE`, with no `PUT` before it, and a never-saved record is discarded with no request.
- **Server pattern rule.** Legacy loads the pattern into a WinForms `MaskedTextBox` as an entry aid, with no save-time check, so partial entries were accepted (`Epi.Windows.Enter/PresentationLogic/ControlFactory.cs:379-393`). The target checks the seeded patterns on the server ("Field types found"). This is a deliberate target rule, not legacy parity. It applies only to submitted values, so stored historical values such as `(555) 0148-6367` (`testdata/sql/07_synthetic_records.sql`) are never re-validated.
- **Save stamps.**
  - Insert: legacy writes `FirstSave*` with the base row, then `LastSave*` and `RecStatus` in a separate `UpdateBaseTable` statement, each pair from its own `DateTime.Now` (`Epi.Core/Data/Services/CollectedDataProvider.cs:963-1004`). The target writes one stamp, truncated to whole seconds, to both pairs.
  - Update: legacy rewrites every column of every page (`:1225-1392`). The target writes only the submitted columns, so concurrent saves of different fields both survive. There is no row-version check: the later commit wins for fields both saves submitted, as in legacy.
- **Every missing required field is reported.** Legacy stops at the first (`Epi.Windows.Enter/PresentationLogic/GuiMediator.Events.ViewExplorer.cs:58-139`). The target lists each missing field, with the verbatim `FIELD_IS_REQUIRED_LONG` text and the prompt and page name filled in.
- **Permission rule (P2, not in the tree at the snapshot).** `canInsert` needs INSERT on the base and every page table, as legacy computes it. `canUpdate` needs UPDATE on the base and every page table: legacy checks page UPDATE only when `canInsertRecords` is true (`Epi.Windows.Enter/Forms/EnterMainForm.cs:1485-1500`), while the target always checks it. A separate `canDelete` needs UPDATE on the base table, because soft delete updates only the base row. Planned enforcement: `RecordService` reads the permissions on every write, with no caching. `POST` needs `canInsert`, and `DELETE` needs `canDelete`. `PUT` needs `canUpdate` and, for each page whose row the locked read finds missing, INSERT on that page table, because the save then inserts that row and repeats its `UPDATE`, as legacy does (`Epi.Core/Data/Services/CollectedDataProvider.cs:1360-1380`). That check runs after the locked read and before any DML. A denied write returns 403 before any DML, so nothing is written. The proofs, `SqlPermissionReaderTests` and `PermissionTests`, are planned and not run.
- **Permission reader (P2).** Legacy `GetPermissions` (`Epi.Data.SqlServer/SqlDatabase.cs:1804-1935`) first runs one catalogue query, `SELECT HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name), 'OBJECT', 'SELECT') AS have_select, name FROM sys.tables` (`:1809-1811`), and keeps only each row's `name`: the schema-qualified `have_select` flag decides nothing and is only debug-printed (`:1828-1838`). It then runs four loops over those names, for INSERT, UPDATE, DELETE and SELECT in turn, each sending one statement per table that concatenates the unquoted one-part name into `SELECT HAS_PERMS_BY_NAME('<table>', 'OBJECT', '<permission>')` (`:1847`, `:1870`, `:1894`, `:1918`), and reads only the value `1` as granted. The target (`src/EpiInfo.Data/Permissions/SqlPermissionReader.cs`) differs in three ways. It checks only the form's own base and page tables, as one catalogue read confirms them, not every table of the database. It reads only INSERT and UPDATE, for those tables together rather than one statement per table and permission, because soft delete is an `UPDATE` of the base row, so DELETE and SELECT decide nothing; a call therefore sends at most one catalogue read (none when the base and page table names are all blank) and one permission statement for up to 1,000 confirmed tables, plus one for each further 1,000 or part of 1,000 (none when the catalogue confirms no table). And each bracket-quoted table name travels only as the value of its own parameter (`@p0`, `@p1`, ...), never in the statement text. As in legacy, only `1` grants: `0` and `NULL` deny, and a table missing from the catalogue, base or page, gets no entry, so `FormPermissions` denies it. Nothing is cached: no call reuses an earlier call's catalogue or permission result, so a grant or revoke applies to the next call. Proof: `SqlPermissionReaderTests` (read-only, as the seeded login) and `PermissionTests` (denials through `FakeDataStore`).
- **Identifier quoting.** Legacy `InsertInEscape` adds `[` only when a name does not already start with it and `]` only when it does not already end with it, and never doubles an embedded `]` (`Epi.Data.SqlServer/SqlDatabase.cs:2094-2107`; `Epi.Core/Data/DbDriverBase.cs:472-487`). The target always wraps and always doubles `]`, so `a]b` becomes `[a]]b]` and `[x]` becomes `[[x]]]`, and it quotes only names the metadata reader returned (`src/EpiInfo.Data/Sql/SqlIdentifier.cs`). The two agree on every seeded name. Proof: `SqlIdentifierTests`, `IdentifierProvenanceTests`.
- **Logon stamp format (A11).** Legacy writes `WindowsIdentity.GetCurrent().Name`, in the form `DOMAIN\user` (`Epi.Core/Data/Services/CollectedDataProvider.cs:963-1004`). The target writes `Environment.UserName` of the API process, with no domain part.

## Legacy defects kept

The three scenarios with `legacyDefect: true` assert the legacy result, and the target is planned to reproduce it. Scenario validation is pending: `CheckCodeScenarioTests` has not run (SC-9 `not run`).

### LY-02

- **Legacy behaviour:** `CaseReport` `Field Sex After` runs `IF Sex = "1" THEN ENABLE Pregnant ELSE DISABLE Pregnant CLEAR Pregnant END-IF` (check code lines 6-16, `testdata/sql/03_forms.sql:31-41`). In `codeSex`, `1` is Male and `2` is Female, so entering `Sex` "2" with `Pregnant` "1" disables and clears `Pregnant`.
- **Why it is a defect:** the condition is inverted. Pregnancy cannot be recorded for a female patient, and the field is enabled for a male one (`testdata/README.md`, finding 1).
- **Target:** kept. The scenario expects `Pregnant` with `enabled: false` and `value: null`. Reproduction is planned. Status: not run (`CheckCodeScenarioTests`, SC-9).

### LY-03

- **Legacy behaviour:** the same rule runs in `Field LastName Before` (check code lines 18-28, `testdata/sql/03_forms.sql:43-53`). Entering `LastName` with `Sex` "2" and `Pregnant` "2" disables and clears `Pregnant`.
- **Why it is a defect:** the same inverted condition, reached from a second event, so a female patient's pregnancy answer is cleared whenever the user enters `LastName`.
- **Target:** kept. The scenario expects `Pregnant` with `enabled: false` and `value: null`. Reproduction is planned. Status: not run (`CheckCodeScenarioTests`, SC-9).

### EC-AGE-03

- **Legacy behaviour:** `FoodHistory` `Field DOB After` runs `ASSIGN AGE = YEARS(DOB, DateofInterview)` (check code line 6, `testdata/sql/03_forms.sql:60`). `YEARS` computes `new DateTime((later - earlier).Ticks).Year - 1` (`Epi.Core.EnterInterpreter/Rules/Functions/Rule_Years.cs:55-66`), turning the elapsed days into a year count from year 1. For `DOB` 2000-02-29 and `DateofInterview` 2011-02-28 it returns 11.
- **Why it is a defect:** it is not a birthday-based age. The span is 4,017 days, exactly the length of calendar years 1 to 11, so `new DateTime` lands on 0012-01-01 and the result is 11 even though the eleventh birthday has not been reached; a birthday-based age gives 10 (`testdata/README.md`, finding 2).
- **Target:** kept, planned as an exact port: `YearsFunction` is to port legacy `YEARS`. Legacy `YEARS` matches all 356 stored sample ages (`testdata/README.md`, finding 2), and the scenario expects `Age` 11. Reproduction is planned. Status: not run (`YearsFunctionTests` and the scenario in `CheckCodeScenarioTests`, SC-9).

## Search semantics

`POST .../records/search` follows legacy Find, because the request's tie-break makes legacy semantics win (C-4). The records-list `q` keeps the request's case-insensitive "contains", because it has no legacy counterpart.

### Legacy extraction

- **Dialog**: at most six fields can be selected (`Epi.Windows.Enter/Dialogs/FindRecordDialog.cs:293-317`), and each control's text is trimmed (`:355-374`). A value splits on exactly `" or "`, `" OR "`, `" Or "` or `" oR "`, dropping empty entries. A leading `<=`, `>=`, `<>`, `>` or `<` is read as the operator, `=` by default, and stripped from the value (`:708-770`). Criteria on different fields combine with AND, and the alternatives of one field with OR.
- **Query**: the base table is `INNER JOIN`ed only to the pages that hold a criterion field (`Epi.Core/Data/Services/CollectedDataProvider.cs:1940-1967`). A system-field criterion maps to an unbound page with no table name, and the join then fails (the `GlobalRecordId` and `RecStatus` rows below). Predicates are built per type, as in the table below, and `RecStatus > 0` is appended (`:2249`). The open record is removed after the query only when the caller passes a non-null `currentView` with the searched view's name. The standard Enter Find command passes none (`Epi.Windows.Enter/Forms/EnterMainForm.cs:406-409`; `Epi.Windows.Enter/Dialogs/FindRecordDialog.cs:67, 80-81, 676`), so a matching open record stays in its result. Only the linked-record viewer passes one (`Epi.Windows.Enter/Forms/LinkedRecordsViewer.cs:292, 336`), and the provider then removes that record (`Epi.Core/Data/Services/CollectedDataProvider.cs:2256-2264`).
- **Hydration**: the matching base rows are re-read and each page is joined in memory; a base row without a row on some page is dropped (`:2289-2298, 2357-2375`). The result is therefore the matching base rows that have a row on every page, in no stated order.

| Field kind | Legacy | Target |
|------------|--------|--------|
| `text`, `multiline`, `legalValues`, except Phone Number | Blank text is rejected by the dialog (`FindRecordDialog.cs:611-643`). The operator prefix is stripped and ignored; a value left empty by a bare prefix such as `>` becomes `IS NULL` (`CollectedDataProvider.cs:2186-2193`). A value containing `%`, `*` or `?` becomes `LIKE` on an inlined literal, with `*` → `%` and single quotes removed (`:2195-2206`). Multiline compares `SUBSTRING(col, 1, 255) = SUBSTRING(@p, 1, 255)` (`:2212-2222`); anything else is `= @p` (`:2224-2231`) | The same, with every value a parameter: blank → 400; a bare prefix → `IS NULL`; the `LIKE` pattern keeps its quotes; `_` and `[...]` keep their SQL meaning and `?` matches literally, as in legacy |
| Phone Number (`fieldType` `text`, chosen by the `metaFieldTypes` name) | Blank rejected by the dialog. A bare prefix gives `IS NULL`; otherwise `col <prefix> '<value>'`, a string comparison with no wildcard path (`CollectedDataProvider.cs:2118-2131`) | The same, parameterised |
| `number` | Blank → `IS NULL`. Each alternative must parse as a number once the prefix is stripped, so a bare prefix is rejected (`FindRecordDialog.cs:418-463`). `col <prefix> <number>` (`CollectedDataProvider.cs:2046-2066`) | The same, with an invariant number parameter; unparseable or a bare prefix → 400 |
| `option` | Treated as numeric by the dialog (`FindRecordDialog.cs:418`). Blank passes `null` to the provider, which fails with a null dereference (`CollectedDataProvider.cs:2186`), so legacy reports an error and returns no rows. Otherwise `= @p` as text, prefix ignored (`:2224-2231`) | Blank → 400. Otherwise an integer index, prefix ignored, `= @p` |
| `date` | Blank → `IS NULL`. Each alternative is parsed with the current culture, and a bare prefix is rejected (`FindRecordDialog.cs:464-510`). `col <prefix> @p` as `DbType.DateTime` (`CollectedDataProvider.cs:2135-2169`) | Blank → `IS NULL`. Only `YYYY-MM-DD` is accepted, bound as `datetime`; a date outside 1753-01-01 to 9999-12-31, another format or a bare prefix → 400 |
| `checkbox` | Blank rejected (`FindRecordDialog.cs:511-517`). Each alternative, prefix included, must equal the configured Yes or No text (defaults `Yes` and `No`, `Epi.Core/Configuration_Static.cs:710-711`) or `(+)` or `(-)` (`FindRecordDialog.cs:518-554`). `= @p` as `DbType.Boolean` (`CollectedDataProvider.cs:2022-2045`) | Blank → 400. The strings `Yes` and `(+)` → `= 1`, `No` and `(-)` → `= 0`, case-sensitive, using the defaults because the new app has no legacy configuration file. A JSON boolean is also accepted. Any other string, `true`, `1` and `0` included → 400 |
| `GlobalRecordId` (system) | Offered by the dialog (`FindRecordDialog.cs:116-120`), whose generic branch rejects a blank. The provider would build `IS NULL` for a bare prefix, otherwise `basetable.GlobalRecordId <prefix> '<value>'` (`CollectedDataProvider.cs:2068-2082`). The search fails before any result: `GlobalRecordIdField` is a `PredefinedDataField`, not a `RenderableField` (`Epi.Core/Services/Fields/GlobalRecordIdField.cs:13`; `Epi.Core/Services/Fields/PredefinedDataField.cs:13`), so no page's `Fields` holds it (`Epi.Core/Page.cs:364-386`). `GetTableName` returns a new, unbound `Page` (`CollectedDataProvider.cs:579-591`; `Page.cs:147-149`) whose `TableName` is `null` (`:219-231`). The unconditional page join (`CollectedDataProvider.cs:1940-1967`) then emits `INNER JOIN [] pageTable0` (`Epi.Core/Util.cs:1193-1196`), and SQL Server rejects the empty identifier | A base-table criterion: the predicate is on the base-table column `GlobalRecordId`, so the field adds no criterion-specific page join and the legacy unbound-page join failure is not reproduced. The search still joins every page table ("Result set" in "Target choices" below), so only base rows with a row on every page are eligible. Blank → 400; a bare prefix → `IS NULL`; otherwise the prefix comparison on the base column, parameterised |
| `RecStatus` (system) | Offered by the dialog (`FindRecordDialog.cs:116-120`) and handled by its generic branch. The provider would build `= @p` as text, then append `RecStatus > 0` (`CollectedDataProvider.cs:2224-2231, 2249`). The search fails before any result, as for `GlobalRecordId`: `RecStatusField` is also a `PredefinedDataField` (`Epi.Core/Services/Fields/RecStatusField.cs:14`), so its criterion maps to the same unbound page and the join fails | A base-table criterion: the predicate is on the base-table column `RecStatus`, so the field adds no criterion-specific page join and the legacy unbound-page join failure is not reproduced. The search still joins every page table ("Result set" in "Target choices" below), so only base rows with a row on every page are eligible. It is matched as an integer, prefix ignored, `= @p` on the base column, inside the selected set |
| `unsupported`, `label`, `group`, `UniqueKey`, unknown | The dialog lists no labels, groups, mirrors, relates, grids or command buttons (`FindRecordDialog.cs:103-114`) | 400. Unsupported types, Yes/No included, are not searchable |

### Target choices

The one list of Find departures and retentions (C-4, C-18):

- **Kept**: the six-field limit, so a seventh criterion field is a 400 on `criteria`; trimming of each string value; the split and prefix rules; AND across fields and OR within one. A field named twice is a 400, since the dialog cannot select a field twice.
- **Result set**: one statement `INNER JOIN`s the base table to every page table and applies the criteria. For the legacy searches that reach hydration, the all-page join reproduces legacy's final, hydrated result (base rows with every page row), though not legacy's two-step query. A legacy search on a system field fails at its join first (the `GlobalRecordId` and `RecStatus` rows above).
- **Set filter**: `RecStatus = 1`, or every row with `includeDeleted=true`, replacing `RecStatus > 0` (C-16).
- **Open record**: excluded on the server through `excludeGlobalRecordId` (C-19), before `total` and the cap. This is a behavioural difference from standard Enter Find, which keeps a matching open record in its result, not only a change of timing. Legacy removed the open record only on the linked-record path, after the query ("Query" above).
- **Order and cap**: results are ordered by `UniqueKey` and capped at 200; `total` counts all matches and `truncated` is `total > 200`. Each item's `position` is its `ROW_NUMBER()` over the selected set by `UniqueKey`, computed before the criteria filter, so it equals the navigation position. Legacy had no order and no cap.
- **Parameterisation**: every value is a parameter, so quotes stay in `LIKE` patterns where legacy removed them.
- **Departures in the table**: ISO dates bound as `datetime`; a blank Option rejected cleanly instead of the legacy null dereference; JSON booleans accepted for checkboxes beside `Yes`/`(+)` → 1 and `No`/`(-)` → 0, case-sensitive; the system fields `GlobalRecordId` and `RecStatus` searched as base-table criteria, where the legacy join on their unbound page fails before any result; Yes/No fields not searchable.

### Grid q

The records-list `q` is a case-insensitive "contains" over the grid columns. It converts each column to `nvarchar(max)` (style 23 for `datetime`) and compares under `Latin1_General_100_CI_AS` with `LIKE @q ESCAPE '\'`, with `%`, `_`, `[` and `\` escaped in `@q`, so it does not depend on the database collation. A blank `q` applies no filter.

### Collation

- **Database collation**: `SQL_Latin1_General_CP1_CI_AS`, read with `DATABASEPROPERTYEX(DB_NAME(), 'Collation')` by direct SQL at write-up. `testdata/sql/00_create_database.sql` pins no collation, so the server default applies.
- **Implied behaviour**: Find's equality and `LIKE` compare under that collation, case-insensitively and accent-sensitively, exactly as legacy does on this database.
- **`ListAndSearchTests`**, which reads the collation and pins that behaviour: not run, because it is not in the tree.

## Parity gaps

Four gaps were recorded during the run, all in the check-code `=` operator (`src/EpiInfo.CheckCode/Evaluation/ValueComparer.cs`); the first also covers `NOT`'s comparison with `"true"` (`Evaluator.cs`). `EvaluatorTests` pins the first three; the fourth, whose legacy answer depends on the desktop's date format, has no pinning test. No seeded program reaches any of them: the seeded comparisons are `Sex = "1"`, `FeverTemp = (.)` and `Fever = (-)`, and the one seeded `NOT` negates the comparison `FeverTemp = (.)`, a boolean (`testdata/sql/03_forms.sql:metaViews`).

### `=` on two texts: the desktop culture

- **Legacy:** two strings are trimmed and compared with `StringComparer.CurrentCultureIgnoreCase` (`Epi.Core.EnterInterpreter/Rules/Rule_CompareExp.cs:224-227`), so under the desktop's current culture with Windows NLS collation. `NOT` makes the same kind of comparison: it compares its operand's string form, untrimmed, with `"true"` using `StringComparison.CurrentCultureIgnoreCase` (`Epi.Core.EnterInterpreter/Rules/Rule_NotExp.cs:40`).
- **Kept:** the same trimming and a linguistic comparison that ignores case, with the invariant culture, so the answer does not depend on the server; `NOT` compares its operand's text, untrimmed, with `"true"` in that comparison (`src/EpiInfo.CheckCode/Evaluation/Evaluator.cs`, `IsTrueForNot`). The two agree on case, trimming, canonically equivalent forms (a precomposed `é` equals `e` followed by a combining acute accent) and ignorable characters. So `NOT` of `tr` + soft hyphen (U+00AD) + `ue` is false, as in legacy, while `IF` and `AND`, which legacy and the port both test by an ordinal comparison of the lowered form with `"true"` (`Rule_If_Then_Else_End.cs:170`; `Rule_AndExp.cs:41-44`), find that text not true.
- **Why not equal:** the server can run neither the desktop's culture nor NLS. The comparisons differ where NLS treats an expansion as equal, such as `ß` and `ss` (`"straße" = "STRASSE"` holds in legacy and not here), where a desktop culture has its own casing rules, such as the Turkish dotted and dotless `i`, and, for `NOT`'s comparison with `"true"`, wherever NLS and the invariant culture give a character a different collation weight. No seeded program compares such text, and none applies `NOT` to text.

### `=` on numeric text: the desktop culture

- **Legacy:** text against a number is parsed with `double.TryParse(string)` (`Epi.Core.EnterInterpreter/Rules/Rule_CompareExp.cs:123-149, 150-175`), whose styles are `NumberStyles.Float | NumberStyles.AllowThousands`, under the desktop's current culture.
- **Kept:** the same number styles, thousands separators included, with the invariant culture: `,` groups digits and `.` is the decimal point, as en-US reads them, so `"1,000" = 1000` and `"1,000.5" = 1000.5` hold. A result that is not finite is rejected, as .NET Framework rejected an overflow.
- **Why not equal:** the server cannot know the desktop's culture. Under a culture that uses `,` as the decimal separator, such as de-DE, legacy reads `"1,5"` as 1.5 and `"1.000"` as 1000, where the server reads 15 and 1.

### `=` with a boolean on the left that legacy cannot convert

- **Legacy:** with a boolean on the left and a non-empty value of another type on the right, the right side's string form is converted with `ConvertStringToBoolean` (`Epi.Core.EnterInterpreter/Rules/Rule_CompareExp.cs:182-185`; `Epi.Core.EnterInterpreter/EnterRule.cs:64-89`). Only `(+)`, `YES`, `Y`, `TRUE`, `T`, `(-)`, `NO`, `N`, `FALSE` and `F` convert, in any letter case and untrimmed. Any other text, and every number, date or option index, converts to `null`, and the cast of that `null` throws. The exception leaves the whole event block, so its remaining statements do not run; it is logged when errors are suppressed and raised otherwise (`Epi.Core.EnterInterpreter/Rules/Rule_Begin_After_Statement.cs:33-47`; `Rule_Begin_Before_Statement.cs:35-49`; `Rule_Begin_Click_Statement.cs:34-48`).
- **Kept:** the ten spellings convert exactly as in legacy, so `Fever = "No"` holds for an unchecked checkbox and not for a checked one. Every other right side gives one `error` diagnostic, `Type mismatch: a boolean can be compared only with a yes or no value.`, with the statement's line and command. That statement is skipped, the effects of earlier statements stand and the next statement runs, as for `EPIWEEK` without a date.
- **Why not equal:** a field event reports a runtime error as a diagnostic that keeps earlier effects and never blocks a save; it has no counterpart for an exception that ends the block. No seeded program compares a checkbox on the left with anything but `(-)`.

### `=` between a date and text: the desktop culture

- **Legacy:** a date and text are compared by the string forms of both sides, untrimmed, with `StringComparer.CurrentCultureIgnoreCase` (`Epi.Core.EnterInterpreter/Rules/Rule_CompareExp.cs:191-217`). The date's form is `DateTime.ToString()` in the desktop's current culture, such as `1/15/2011 12:00:00 AM` under en-US.
- **Kept:** the invariant string forms of both sides, a date as `yyyy-MM-dd` (C-15), untrimmed, compared linguistically with the invariant culture, ignoring case. So text `"2011-01-15"` equals the date 2011-01-15, and text `"1/15/2011 12:00:00 AM"` does not.
- **Why not equal:** the server does not know the desktop's date format, and dates are ISO `YYYY-MM-DD` on the wire (C-15). No seeded program compares a date with text.

These checks would record further gaps, with their state at the snapshot:

| Check | Gap it would record | State |
|-------|---------------------|-------|
| `NavigationTests`: live distinct `RecStatus` values (C-16) | A value other than 0 and 1 | not run. Direct SQL found only 0 and 1 |
| `SqlMetadataReaderTests`: column type against the admitted set | A field whose column type its `fieldType` does not admit | not run. Direct SQL found no mismatch |
| `CheckCodeScenarioTests`: every in-scope scenario (A4) | A scenario that fails, with its id | not run |
| `ReservedWordIdentifierTests` | A reserved-word round trip that cannot be proved | not run (see "Proofs not made") |
| P2 permission awareness | That no real denied principal could be tested, because it needs a new login or user, which the unchanged-database rule excludes | not applicable while P2 permissions are not in the tree |

## Undelivered items

| Item | Tier | Reason | What remains |
|------|------|--------|--------------|
| Relate fields and child forms | P2 | Neither seeded form has a relate field or a child form (A7), so the feature could be neither built against nor proved without changing `testdata/` | Future scope: reading relate fields and child forms from metadata, with the related-record navigation they need, and verifying them against a fixture that holds a relate field and a child form. This run changes neither the code path nor `testdata/`; until then relate stays `unsupported` and renders as a disabled placeholder |
| SQLite `IDataStore` | P2 | The repository holds no SQLite copy of `EpiRehearsal`, and producing one is a data conversion the request does not specify, so the store would have nothing to run or be proved against | Future scope: obtaining a SQLite rehearsal dataset, or specifying the conversion that produces one from `EpiRehearsal`; then implementing the SQLite `IDataStore` with its registration, and verifying it against that dataset. This run adds no file, package or registration for it |
| Parity matrix | P2 | P2 work starts once every P0 criterion passes, and at the snapshot none had passed. The matrix's statuses (ported, changed, not carried) can only be stated against delivered, tested code | The "Parity matrix" section, once SC-1 to SC-8 pass |

### Not in the tree at the snapshot

These items are mapped for delivery in this run but were not in tree `22ead6a7`.

| Item | Tier | Files not in the tree | What remains |
|------|------|-----------------------|--------------|
| Interpreter | P1 | `src/EpiInfo.CheckCode/{Lexing,Syntax,Parsing,Evaluation,Functions}/**`, with the facade `ICheckCodeEngine.cs` and `CheckCodeEngine.cs` | The interpreter and its tests in `tests/EpiInfo.CheckCode.Tests/**` |
| Field-event behaviour | P1 | `src/EpiInfo.Api/Endpoints/CheckCodeEndpoints.cs`, `src/EpiInfo.Api/Services/FieldEventService.cs` | The endpoint's effects, `unsupportedCheckCode`, and the effect cases of `FieldEventEndpointTests` and `CheckCodeScenarioTests` |
| Client events | P1 | `web/src/state/fieldEventQueue.ts`, `web/src/checkcode/applyFieldEvent.ts` | Event raising, effect application, `FieldEffects.test.tsx`, `fieldEventQueue.test.ts` and the smoke test's SC-10 step |
| Permission awareness | P2 | `src/EpiInfo.Data/Permissions/SqlPermissionReader.cs` | The reader, the form-definition flags, the 403 checks, `SqlPermissionReaderTests` and `PermissionTests` |
| Command-line switch mapping | P2 | The `?view=&record=` handling in `web/src/App.tsx`, and `web/src/App.test.tsx` | The mapping and its client test |
| SC-11 performance | P2 | `tests/EpiInfo.Api.Tests/Performance/PerformanceTests.cs` | The test and its run with `--filter Category=Performance` |
| Docker image | P2 | `src/EpiInfo.Api/Dockerfile` | The image and the Docker check under "Run results" |
| Higher test coverage | P2 | Additional cases in the existing test files | The additional cases |

### Pending P1 scenarios

None. `tests/EpiInfo.Api.Tests/Scenarios/ScenarioCatalog.cs` was not in the tree at the snapshot, and its `PendingP1Ids` is planned empty.

## Parity matrix

Not delivered (P2); listed under Undelivered items.
