# Epi Info rehearsal test data (`/testdata`)

A SQL Server database for the Enter (data entry) modernization rehearsal. It contains two real Epi Info forms (Lyme `CaseReport`, EColi `FoodHistory`), their records, and synthetic records, laid out exactly as the legacy app stores them on SQL Server. No production data.

## Quick start

**Mac / Linux (Docker Desktop):**

```bash
cd testdata
docker compose up            # starts SQL Server 2022 and loads everything, then db-init exits
```

App connection string:

```
Server=localhost,1433;Database=EpiRehearsal;User Id=sa;Password=Rehearsal!Passw0rd;TrustServerCertificate=True
```

Override the password with `MSSQL_SA_PASSWORD`. On Apple Silicon the image runs under Rosetta (enable it in Docker Desktop settings).

**Optional, Windows with LocalDB (not used for this rehearsal):**

```powershell
cd testdata
.\init.ps1                                  # (localdb)\MSSQLLocalDB, Windows auth
.\init.ps1 -Server "localhost,1433" -User sa -Password '...'
```

Rerunning either script drops and recreates `EpiRehearsal`, so it doubles as a reset between test runs. The last script (`99_verify.sql`) prints checks; every row should show `ok = 1`.

## What's inside

| File | Contents |
|---|---|
| `sql/00_create_database.sql` | Drop and recreate `EpiRehearsal` |
| `sql/01_metadata_schema.sql` | 17 meta tables, generated from `MetadataDbProvider.CreateTables()` (the real SQL Server path) |
| `sql/02_reference_data.sql` | Lookup rows Epi inserts in code: field types (28), data types, patterns, layer render types |
| `sql/03_forms.sql` | Project row, form definitions (views, pages, fields, check code), page background images |
| `sql/04_code_tables.sql` | 12 code tables used by dropdown fields |
| `sql/05_data_tables.sql` | Base table per form + one table per page (`<Form><PageId>`), joined on `GlobalRecordId` |
| `sql/06_sample_records.sql` | Sample records shipped with Epi Info (CDC public-domain, marked fictional on the forms) |
| `sql/07_synthetic_records.sql` | Synthetic records (generated, deterministic) |
| `sql/99_verify.sql` | Row counts, page-table joins, soft deletes |
| `forms.json` | Form and field snapshot used by the generator and handy for tests |
| `scenarios/check_code_scenarios.json` | 25 check-code, validation and data scenarios with expected results |
| `generate_synthetic.py` | Regenerates `07_synthetic_records.sql` |
| `tools/` | Build, Access reader, legacy-function ports, offline SQL lint |

## Forms and records

| Form | ViewId | Pages / tables | Sample | Synthetic | Total | Soft-deleted |
|---|---|---|---|---|---|---|
| CaseReport (Lyme) | 1 | 1 / `CaseReport1` | 1,159 | 250 | 1,409 | 17 |
| FoodHistory (EColi) | 2 | 2 / `FoodHistory2`, `FoodHistory3` | 359 | 250 | 609 | 12 |

The two sample projects were merged into one database, so EColi IDs were remapped: view 1 to 2, pages 1 and 2 to 2 and 3 (tables renamed to match), field IDs plus 1000, and image and background IDs offset. A second EColi view with no data (`FoodHistory_NoCheckCode`) was left out.

## How the schema was derived

The `.sql` scripts in `Epi.Core/Resources/Scripts` are not what Epi Info runs on SQL Server and are incomplete (no field-type rows, no backgrounds, images or links tables). This build follows the C# code instead:

- **Meta tables:** `MetadataDbProvider.CreateTables()`, using the `SqlDatabase.CreateTable` type map
- **Data tables:** `CollectedDataProvider.CreateDataTableForView` and `CreatePageDataTableForView`
- **Column types per field:** each field's `GetDbSpecificColumnType()`. For example, text is `nvarchar(4000)`, number is `float`, yes/no is `tinyint`, checkbox is `bit`, dropdowns are `nvarchar(255)` and multiline is `ntext`
- **Code tables:** `CreateCodeTable`, with lowercase names and `nvarchar(255) NOT NULL` columns

## Findings that matter for parity

1. **Legacy defect (CaseReport):** check code enables `Pregnant` when `Sex = "1"`, but code 1 is Male. Females get `Pregnant` disabled and cleared. Kept on purpose (scenarios LY-02, LY-03); the target should keep it unless it's listed as an approved behavior change.
2. **YEARS is not a birthday-based age:** it converts the elapsed days into a year count from year 1. It matches all 356 stored ages in the sample, but a leap-day DOB gives a different answer (EC-AGE-03).
3. **Historical data doesn't follow current rules:** only 40 of 326 stored `OnsetWeek` values match `EPIWEEK(OnsetDate)`. Check code runs only on data-entry events, so the new app must not recompute stored values when loading (DATA-01).
4. **Out of scope:** `GEOCODE` needs an external service. It should be logged and skipped (EC-GEO-01).
5. **Schema quirk:** `metaFields.ControlFontSize` is created as `decimal(2)`, so 8.25 is stored as 8. That's legacy behavior and stays as-is.

The expected values in `scenarios/` are derived from legacy source (each one cites the file). The YEARS and EPIWEEK ports are in `tools/epi_functions.py`. This rehearsal validates the target app only (Linux environment, no legacy build), so these scenarios are the parity reference.

## Regenerating

```bash
python3 generate_synthetic.py --count 250 --seed 20261123   # same seed = identical output
python3 tools/build_testdata.py ..                          # rebuild 00-06 from the .mdb samples
python3 tools/lint_sql.py                                   # offline structural check
```

Do not edit the generated SQL by hand.

## Status

- Linted offline: 34 tables, 51 insert statements, 4,986 rows, 0 errors. That covers columns, value counts per row and literal types.
- Loaded on SQL Server 2022 (RTM-CU28, Linux container) on 2026-11-23: all 99_verify checks ok = 1.

## Using it in Blitzy

Commit `/testdata` before ingestion, and don't add it to `.blitzyignore`. Use a Linux environment. Suggested setup instructions: "Start SQL Server 2022 with `testdata/docker-compose.yml` (or any SQL Server 2022 reachable as SQL_HOST), run `testdata/init.sh` with MSSQL_SA_PASSWORD set, and confirm every 99_verify row shows ok = 1. Use only this database for tests. Reset by rerunning init.sh."
