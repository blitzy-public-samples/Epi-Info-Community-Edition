# Epi-Info-Community-Edition

**Epi Info™ for Windows Download**
https://www.cdc.gov/epiinfo/pc.html

Epi Info™ is a public domain suite of interoperable software tools designed for the global community of public health practitioners and researchers. It provides for easy data entry form and database construction, a customized data entry experience, and data analyses with epidemiologic statistics, maps, and graphs for public health professionals who may lack an information technology background.  

The collection of software tools comprising Epi Info™ Desktop equips public health professionals with a wide range of capabilities. These include the ability to: 

- **Conduct Epidemiologic Analysis:** Transform data and perform many types of statistical analyses. Create 2x2 tables and matched-pair case-control studies. Use the new Visual Dashboard feature for regression analysis. Create informative charts using robust chart building capabilities. 

- **Create Forms and Enter Data:** Quickly create questionnaires and data entry forms. Epi Info Desktop will automatically create a database on the basis of a questionnaire designed in the Form Designer module or from an existing data dictionary. Users can enter new data, modify existing data, or search for records. 

- **Generate Maps:** Construct display-quality geographic maps showing data collected using Epi Info Desktop, or display data stored in a variety of other file formats and database systems. More readily identify clusters and trends. Incorporate additional layers to show how data points are related. 

- **Design Custom Solutions:** Leverage Epi Info Desktop’s application programming interface to create custom public health informatics solutions. 

- **Seamless Integration:** Forms and data can easily be shared between Epi Info Desktop and web/cloud products: Epi Info Web Survey, Epi Info Web Analysis and Visualization, and Epi Info Cloud Data Capture as well as with Android and iOS mobile applications. 

Epi Info™ covers the four major functions needed to mitigate a disease outbreak in real time. First, clinical and historical information must be collected from individuals in the affected area as accurately and quickly as possible. This requires an interview questionnaire to systematically collect the data while ensuring the highest possible quality; the Form Designer module supports this function. The Form Designer allows the user to drag and drop different data collection widgets—such as selection menus, check boxes, and text-entry fields—onto a canvas and associate the data collected with variables stored in a database. A system of "check codes" allows the Form Designer to perform logical or mathematical tests to validate the data and disable fields rendered inapplicable by previously entered data. The forms are flexible; new questions can be added during data collection, allowing epidemiologists to pursue new leads in the dynamic environment of a public health emergency. The Enter Data module supports data entry by using the questionnaires developed in Form Designer to prompt the subject and interviewer for specific data. The systematic collection of data makes it possible for the epidemiologist to analyze those data immediately, in contrast to the unstructured interview in which notes need to be collected and interpreted after the fact. 
 

There are two analytical modules in Epi Info™, Classic Analysis and the Visual Dashboard. These offer similar tools but differ in how they are configured and how results are presented. Visual Dashboard is the newest analysis module and has the most up-to-date and user-friendly interface. It allows the user to select standard statistical tests and specialized epidemiological tools from a menu and configure them using a graphical interface. Once the input data are defined, the tool's output, typically in the form of a graph or chart, is dropped onto a canvas. This process can be repeated with as many analyses as needed, turning the canvas into an analytical "dashboard" that is updated every time new data are entered into the system. 

The Map module complements the data collection and analytical modules by allowing the user to plot location data on geographic information system (GIS) base maps or shape files. The locations of individuals can be colored or filtered based on other data such as clinical presentation or risk factors.  Maps can be zoomed in and out and overlaid with demographic data and other reference content. The ability to rapidly create views on the distribution of cases in relation to other factors allows an intuitive exploration of data that can then be used to propose testable hypotheses, shaving weeks or months off the hunt for the source of the outbreak.  This savings of time and effort can not only reduce the financial cost of mitigation efforts, but it can also reduce morbidity and mortality. 

## Epi Info Web Data Entry

A browser replacement for the legacy Enter data-entry module, built side by side with the desktop suite: a React 18 + TypeScript client built with Vite in `web/`, an ASP.NET Core minimal API on .NET 10 in `src/` (`EpiInfo.Domain`, `EpiInfo.CheckCode`, `EpiInfo.Data`, `EpiInfo.Api`) and xUnit tests in `tests/`, with every .NET project in `EpiInfo.Web.sln`. It works against the SQL Server 2022 `EpiRehearsal` database built from `testdata/`, with the Epi Info metadata and data-table schema unchanged, so records it writes stay readable by Epi Info. The legacy projects and `Epi Info 7.sln` are untouched and are not part of the new build. [`docs/parity-notes.md`](docs/parity-notes.md) is the parity ledger and run report: every difference from legacy behaviour, every assumption checked and the result of each success criterion.

### Why the commands name the solution

The repository root also holds `Epi Info 7.sln`, so a bare `dotnet build` or `dotnet test` there fails with `MSB1011: Specify which project or solution file to use`. Always run `dotnet build EpiInfo.Web.sln` and `dotnet test EpiInfo.Web.sln` from the repository root; they build and test the eight projects of the new tree and no legacy project.

### Prerequisites

- **.NET SDK in the 10.0.1xx feature band.** `global.json` pins `10.0.100` with `rollForward: latestPatch` and `allowPrerelease: false`, so the newest installed 10.0.1xx SDK is used, and a machine holding only a later band such as 10.0.4xx fails with "A compatible .NET SDK was not found". Install the newest 10.0.1xx SDK with `./dotnet-install.sh --channel 10.0.1xx`.
- **Node.js 22 LTS** with its bundled npm; `web/package.json` sets `engines.node` to `>=22 <23`.
- **Docker Engine with Compose v2**, which `docker compose up --wait` needs; or any SQL Server 2022 reachable as `SQL_HOST`, plus a host `sqlcmd` on `PATH`.
- **On Linux, Playwright's Chromium system libraries**, installed once in `web/` after `npm ci`: `npx playwright install-deps chromium`. It installs operating-system packages, so it may need elevated privileges.

### Rehearsal database precondition

Every database-backed step depends on this precondition.

1. Read [`testdata/README.md`](testdata/README.md) first.
2. Initialise only the designated rehearsal target: the Compose `sqlserver` service, or the server named by `SQL_HOST`. `testdata/init.sh` drops and recreates `EpiRehearsal` on every run, so never point it at a database holding anything else. Rerunning it discards every record the tests created.
3. Straight after initialisation, and before any test runs, confirm that the final `99_verify` result set (`check_name`, `actual`, `expected`, `ok`) holds exactly these nine checks, each with `ok = 1`: `metaViews`, `metaPages`, `metaFieldTypes`, `CaseReport records`, `FoodHistory records`, `CaseReport1 orphans`, `FoodHistory2 orphans`, `FoodHistory3 orphans` and `soft-deleted records`. A missing, duplicated or extra row, an empty result or any `ok = 0` fails the precondition. `init.sh` prints the rows but exits 0 even when one shows `ok = 0`, so its exit status is not evidence. The check cannot be made later, because the tests add records and change the counts it reports.
4. If initialisation or the precondition fails, the database-backed steps (the `Integration` tests of `dotnet test` and `npm run e2e`) are not run. The checks that need no database still run; they are listed under [Running subsets of the tests](#running-subsets-of-the-tests).

### Clean-checkout sequence

```bash
cd testdata
export MSSQL_SA_PASSWORD='Rehearsal!Passw0rd'   # development value from testdata/README.md
docker compose up -d --wait sqlserver
docker compose run --rm db-init                 # drops and recreates EpiRehearsal; check every 99_verify row has ok = 1
cd ..
dotnet build EpiInfo.Web.sln
dotnet test EpiInfo.Web.sln
cd web
npm ci
npm run lint
npm run build
npm test
npm run e2e
```

Run it from the repository root of a fresh clone; every command must exit 0. `npm run build` must come before `npm run e2e`, because Playwright starts the API with `dotnet run --no-build`, serving the built client from `web/dist`; that `--no-build` also relies on the API already being built, which the earlier `dotnet build EpiInfo.Web.sln` does.

**Alternative database path.** With any SQL Server 2022 reachable as `SQL_HOST`, run `SQL_HOST=<host,port> MSSQL_SA_PASSWORD=<password> bash testdata/init.sh` with a host `sqlcmd` in place of the Compose steps, under the same precondition, and keep both variables exported for the tests.

### Environment variables

| Variable | Read by | Meaning |
|---|---|---|
| `ConnectionStrings__EpiInfo` | API, test harnesses | The API's only connection source, supplied through the environment and never committed to any file; the tests use it verbatim when it is set. When it is missing, the API still starts, logs a warning naming the key, and the database-backed endpoints and `/health` return 503. |
| `Database__CommandTimeoutSeconds` | API | SQL command timeout in seconds; default 30. |
| `SQL_HOST`, `MSSQL_SA_PASSWORD` | Test harnesses, `testdata/init.sh` | The rehearsal server (`host,port`) and its `sa` password. When `ConnectionStrings__EpiInfo` is unset, the tests build `Server={SQL_HOST or localhost,1433};Database=EpiRehearsal;User Id=sa;Password={MSSQL_SA_PASSWORD};TrustServerCertificate=True`. Integration tests never skip: an unreachable database or a missing password fails them with a message naming these variables. |
| `E2E_BASE_URL` | Playwright | Base URL of the API under test; default `http://localhost:5000`. When it is set, Playwright starts no server, and `ConnectionStrings__EpiInfo` (or `SQL_HOST` and `MSSQL_SA_PASSWORD`) must name the same rehearsal database that API uses, or the smoke test fails. |

### Running subsets of the tests

- Tests that need no database: `dotnet test EpiInfo.Web.sln --filter "Category!=Integration"`. With `dotnet build EpiInfo.Web.sln`, `npm run lint`, `npm run build` and `npm test`, these are the checks that still run when the database precondition fails.
- Integration tests except performance: `dotnet test EpiInfo.Web.sln --filter "Category=Integration&Category!=Performance"`.
- A run without `--filter` excludes `Category=Performance` by default. Any explicit `--filter` replaces that default, so a filtered command must name the exclusion itself, as the two above do.
- Performance tests (p95 of `GET .../records/at/{position}` at most 300 ms and of a `POST` save at most 500 ms, over 100 sequential calls each): `dotnet test tests/EpiInfo.Api.Tests --filter Category=Performance`. They need the rehearsal database and create their own test records.
- With `Microsoft.NET.Test.Sdk` 17.x on SDK 10, `dotnet test` prints an informational "VSTEST_ARTIFACTS_DIRECTORY environment variable was not found" line; it does not affect results.

### Running the app

```bash
export ConnectionStrings__EpiInfo='Server=localhost,1433;Database=EpiRehearsal;User Id=sa;Password=Rehearsal!Passw0rd;TrustServerCertificate=True'
(cd web && npm ci && npm run build)
dotnet run --project src/EpiInfo.Api --no-launch-profile
```

The API serves both the built client from `web/dist` and the HTTP API at `http://127.0.0.1:5000`; its health check is `GET http://127.0.0.1:5000/health`. It listens on the IPv4 loopback address `127.0.0.1` only, and refuses to start when any listen setting (`ASPNETCORE_URLS`, `--urls`, `ASPNETCORE_HTTP_PORTS`, a `Kestrel:Endpoints` URL) names another host, `localhost` included, or gives ports alone. The project commits no `Properties/launchSettings.json`; `--no-launch-profile` stops a locally generated one from supplying a listen URL that this guard would reject.

**Development mode.** Start the API as above, then run `npm run dev` in `web/`: Vite serves `http://localhost:5173` and proxies `/api` and `/health` to `http://127.0.0.1:5000`.

### Changing the API contract

The OpenAPI document `src/EpiInfo.Api/openapi/EpiInfo.Api.json` is the contract between the API and the client, and `web/src/api/schema.ts` holds the client types generated from it; both are committed. After any change to endpoints or DTOs, run `npm run api:generate` in `web/`: it rebuilds the API with build-time OpenAPI generation, which needs no running server or database, and rewrites both files. `npm run lint` fails with "OpenAPI document or generated client is stale; run npm run api:generate" when either is out of date. Never hand-edit either file.

### Docker image (optional)

`src/EpiInfo.Api/Dockerfile` builds one image holding the API and the built client. From the repository root:

```bash
docker build -f src/EpiInfo.Api/Dockerfile -t epiinfo-web .
docker run --rm --network host -e ConnectionStrings__EpiInfo=<value> epiinfo-web
```

The container keeps the `127.0.0.1:5000` binding, hence host networking. The connection string is supplied only at run time; the image holds none.
