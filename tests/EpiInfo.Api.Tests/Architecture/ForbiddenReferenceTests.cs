using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Storage;

namespace EpiInfo.Api.Tests.Architecture;

/// <summary>
/// Keeps the legacy desktop stack out of the new tree (AAP 0.5.4, 0.7.6): <c>EpiInfo.Web.sln</c> holds exactly the
/// eight SDK-style projects, neither they nor the three props files they import name a forbidden reference, their
/// folders and <c>tests/Shared</c> hold no <c>packages.config</c> or DLL and are the only folders their file items
/// link, the four <c>src/</c> assemblies reference only the shared framework, the pinned packages and each other, and
/// no C# string literal under <c>src/</c> holds DDL statement text.
/// </summary>
/// <remarks>
/// <para>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no collection, so every
/// <c>dotnet test</c>, <c>--filter "Category!=Integration"</c> included, runs it.
/// </para>
/// <para>
/// It reads only <c>EpiInfo.Web.sln</c>, <c>Directory.Packages.props</c>, <c>src/**</c>,
/// <c>tests/Directory.Build.props</c>, the four <c>tests/EpiInfo.*.Tests</c> folders and <c>tests/Shared</c>. It never
/// lists the files at the top of <c>tests/</c> and never recurses into <c>tests/</c> as a whole: on a case-insensitive
/// file system that folder is the legacy <c>Tests/</c>, whose <c>Tests.csproj</c> legitimately holds <c>HintPath</c>
/// and <c>packages.config</c>. Folders are walked one level at a time, and a <c>bin/</c> or <c>obj/</c> folder is
/// pruned by name before anything else, links included, so it is never entered. Every other file the class opens and
/// every folder it lists, whether reached by a walk or by a fixed path such as <c>EpiInfo.Web.sln</c> or
/// <c>tests/</c>, is link-checked first: a symbolic link, junction or other reparse point at it, or at any folder
/// between it and the repository root, fails the scan rather than being followed or skipped, a dangling link included,
/// and every file a walk yields lies inside the folder it walks. Only the walk and read self-tests write, and only to
/// a fresh temporary folder they delete.
/// </para>
/// </remarks>
public sealed partial class ForbiddenReferenceTests
{
    private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    private static readonly string[] SourceProjects =
    [
        "src/EpiInfo.Domain/EpiInfo.Domain.csproj",
        "src/EpiInfo.CheckCode/EpiInfo.CheckCode.csproj",
        "src/EpiInfo.Data/EpiInfo.Data.csproj",
        "src/EpiInfo.Api/EpiInfo.Api.csproj",
    ];

    private static readonly string[] TestProjects =
    [
        "tests/EpiInfo.Domain.Tests/EpiInfo.Domain.Tests.csproj",
        "tests/EpiInfo.CheckCode.Tests/EpiInfo.CheckCode.Tests.csproj",
        "tests/EpiInfo.Data.Tests/EpiInfo.Data.Tests.csproj",
        "tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj",
    ];

    private static readonly string[] PropsFiles =
    [
        "src/Directory.Build.props",
        "tests/Directory.Build.props",
        "Directory.Packages.props",
    ];

    private static readonly string[] Projects = [.. SourceProjects, .. TestProjects];

    // Every folder is listed one level at a time with these options. Hidden entries are listed and access errors
    // surface, so nothing in a scanned folder escapes the checks. Name matching is case-insensitive on every platform,
    // so an oddly cased project or folder is still found. Descent is FilesWithin's own walk, never the enumerator's
    // recursion, which would follow directory links and enter bin/ and obj/ before any filter could prune them.
    private static readonly EnumerationOptions TopLevelScan = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    [Fact]
    public void SolutionProjects_AreExactlyTheEightNewProjects()
    {
        // No legacy project joins EpiInfo.Web.sln (AAP 0.9.1): every non-folder entry is one of the eight.
        string solution = ReadRepositoryFile(RepositoryPaths.SolutionFileName)
            ?? throw new FileNotFoundException(
                $"'{RepositoryPaths.SolutionFileName}' does not exist at the repository root.",
                Full(RepositoryPaths.SolutionFileName));
        IEnumerable<string> solutionProjects = SolutionProjectEntry()
            .Matches(solution)
            .Where(entry => !string.Equals(
                entry.Groups["type"].Value, SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Groups["path"].Value.Replace('\\', '/'));
        AssertSameSet(Projects, solutionProjects, RepositoryPaths.SolutionFileName);

        IEnumerable<string> sourceProjects = ProjectFilesUnder(Full("src")).Select(Rel);
        AssertSameSet(SourceProjects, sourceProjects, "src/");

        // Only the EpiInfo.*.Tests folders are listed, once tests/ is known to be a real folder; the top of tests/ may
        // be the legacy Tests/ folder.
        string tests = RealFolderWithin(RepositoryPaths.Root, Full("tests"));
        IEnumerable<string> testProjects = Directory
            .EnumerateDirectories(tests, "EpiInfo.*.Tests", TopLevelScan)
            .SelectMany(ProjectFilesUnder)
            .Select(Rel);
        AssertSameSet(TestProjects, testProjects, "tests/EpiInfo.*.Tests/");

        // tests/Shared is linked source of the database-backed test projects (AAP 0.4.2), never a ninth project.
        string shared = Full("tests", "Shared");
        string[] sharedProjects = Directory.Exists(shared) ? [.. ProjectFilesUnder(shared).Select(Rel)] : [];
        AssertNone(sharedProjects, "tests/Shared must hold no project file, but holds");
    }

    [Fact]
    public void ProjectAndPropsFiles_ContainNoForbiddenToken()
    {
        var missing = new List<string>();
        var hits = new List<string>();
        foreach (string relative in ScannedMsBuildFiles())
        {
            string? text = ReadRepositoryFile(relative);
            if (text is null)
            {
                missing.Add(relative);
                continue;
            }

            foreach (string token in ForbiddenReferenceRules.FindForbiddenProjectTokens(text))
            {
                hits.Add($"{relative}: {token}");
            }
        }

        AssertNone(missing, "These MSBuild files must exist but do not");
        AssertNone(hits, "These MSBuild files name a forbidden reference (AAP 0.5.4)");
    }

    [Fact]
    public void ProjectDirectories_HoldNoPackagesConfigOrVendoredDll()
    {
        string[] allowedRoots = AllowedRoots();
        var missing = new List<string>();
        var offenders = new List<string>();

        // A file item may link only into the allowed tree, which is scanned below. Any other location is reported by
        // path arithmetic alone and never listed or read.
        foreach (string project in Projects)
        {
            foreach (string source in MsBuildSourcesOf(project))
            {
                // ProjectAndPropsFiles_ContainNoForbiddenToken reports an MSBuild file that does not exist.
                string? text = ReadRepositoryFile(source);
                if (text is null)
                {
                    continue;
                }

                string[] itemSpecs;
                try
                {
                    itemSpecs = [.. ForbiddenReferenceRules.FindFileItemIncludes(text)];
                }
                catch (XmlException exception)
                {
                    offenders.Add($"{source}: {ForbiddenReferenceRules.MalformedXmlToken}: {exception.Message}");
                    continue;
                }

                string origin = source == project ? project : $"{source} (imported by {project})";
                offenders.AddRange(itemSpecs
                    .Select(itemSpec => LinkOutsideAllowedTree(project, itemSpec, allowedRoots))
                    .OfType<string>()
                    .Select(problem => $"{origin}: {problem}"));
            }
        }

        foreach (string directory in allowedRoots)
        {
            if (!Directory.Exists(directory))
            {
                missing.Add(Rel(directory));
                continue;
            }

            offenders.AddRange(FilesUnder(directory)
                .Where(path =>
                    string.Equals(Path.GetFileName(path), "packages.config", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                .Select(Rel));
        }

        AssertNone(missing, "These project and shared-source folders must exist but do not");
        AssertNone(
            offenders,
            "The project folders and tests/Shared must hold no packages.config and no vendored DLL, and file items " +
            "may link only those folders (AAP 0.5.4)");
    }

    // The expected name is a second argument so each row can prove its anchor sits in the assembly it stands for.
    [Theory]
    [InlineData(typeof(IDataStore), "EpiInfo.Domain")]
    [InlineData(typeof(ICheckCodeEngine), "EpiInfo.CheckCode")]
    [InlineData(typeof(ISqlCommandObserver), "EpiInfo.Data")]
    [InlineData(typeof(global::Program), "EpiInfo.Api")]
    public void SourceAssembly_ReferencesOnlyAllowedAssemblies(Type anchor, string expectedAssemblyName)
    {
        Assembly assembly = anchor.Assembly;
        Assert.Equal(expectedAssemblyName, assembly.GetName().Name);

        string[] referenced =
        [
            .. assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? "<unnamed>")
                .Order(StringComparer.Ordinal),
        ];
        Assert.NotEmpty(referenced);

        AssertNone(
            [.. referenced.Where(ForbiddenReferenceRules.IsDesktopUiAssembly)],
            $"{expectedAssemblyName} must not reference System.Windows.Forms or System.Drawing, but references");
        AssertNone(
            [.. referenced.Where(name => !ForbiddenReferenceRules.IsAllowedReferencedAssembly(name))],
            $"{expectedAssemblyName} may reference only System.*, Microsoft.*, Dapper, netstandard and the " +
            "EpiInfo product assemblies, but references");
    }

    [Fact]
    public void SourceFiles_ContainNoDdlStatementText()
    {
        int scanned = 0;
        var hits = new List<string>();
        foreach (string path in FilesUnder(Full("src"))
                     .Where(path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)))
        {
            scanned++;

            // The whole file is read, so a statement split across lines of a literal, or across literals, is found.
            string source = File.ReadAllText(path);
            foreach (ForbiddenReferenceRules.DdlHit hit in ForbiddenReferenceRules.FindDdlStatements(source))
            {
                hits.Add($"{Rel(path)}:{hit.Line}: {hit.Excerpt}");
            }
        }

        Assert.True(scanned > 0, $"No C# source file was found under '{Full("src")}'.");
        AssertNone(hits, "src/ must hold no CREATE, ALTER or DROP statement text (AAP 0.7.6)");
    }

    [Theory]
    [InlineData(@"<Reference Include=""System.Drawing"" />", "System.Drawing")]
    [InlineData(@"<PackageReference Include=""System.Drawing.Common"" />", "System.Drawing")]
    [InlineData(@"<Reference Include=""System.Windows.Forms"" />", "System.Windows.Forms")]
    [InlineData("<UseWindowsForms>true</UseWindowsForms>", "UseWindowsForms")]
    [InlineData("<UseWPF>true</UseWPF>", "UseWPF")]
    [InlineData(@"<ProjectReference Include=""..\..\Epi.Core\Epi.Core.csproj"" />", "Epi.Core")]
    [InlineData(
        @"<ProjectReference Include=""..\..\Epi.Core.EnterInterpreter\Epi.Core.EnterInterpreter.csproj"" />",
        "Epi.Core")]
    [InlineData(@"<ProjectReference Include=""..\..\Epi.Windows.Enter\Epi.Windows.Enter.csproj"" />", "Epi.Windows")]
    [InlineData(
        @"<ProjectReference Include=""..\..\Epi.Enter.CheckCodeEngine\Epi.Enter.CheckCodeEngine.csproj"" />",
        "Epi.Enter.CheckCodeEngine")]
    [InlineData(@"<ProjectReference Include=""..\..\Epi.Data.SqlServer\Epi.Data.SqlServer.csproj"" />", "Epi.Data*")]
    [InlineData(@"<ProjectReference Include=""..\..\EpiInfoPlugin\EpiInfo.Plugin.csproj"" />", "EpiInfo.Plugin")]
    [InlineData(
        @"<Reference Include=""GoldParserEngine""><HintPath>..\Assemblies\GoldParserEngine.dll</HintPath></Reference>",
        "GoldParserEngine")]
    [InlineData(
        @"<Reference Include=""GoldParserEngine""><HintPath>..\Assemblies\GoldParserEngine.dll</HintPath></Reference>",
        "HintPath")]
    [InlineData(@"<Reference Include=""CalithaLib"" />", "CalithaLib")]
    [InlineData(@"<!-- a --><Reference Include=""CalithaLib"" /><!-- b -->", "CalithaLib")]
    [InlineData(@"<None Include=""packages.config"" />", "packages.config")]
    [InlineData(@"<Content Include=""lib\Vendor.dll"" />", ForbiddenReferenceRules.VendoredDllToken)]
    [InlineData(@"<Content Include='lib\Vendor.dll' />", ForbiddenReferenceRules.VendoredDllToken)]
    [InlineData(@"<Content Include=""lib\Vendor.dll;readme.txt"" />", ForbiddenReferenceRules.VendoredDllToken)]
    [InlineData(@"<None Include="" readme.txt ; lib\Vendor.DLL ;; "" />", ForbiddenReferenceRules.VendoredDllToken)]
    [InlineData(@"<None Update=""lib\Vendor.dll"" />", ForbiddenReferenceRules.VendoredDllToken)]
    [InlineData(@"<Content Include=""lib\*.dll"" />", ForbiddenReferenceRules.VendoredDllToken)]
    [InlineData(@"<Content Include=""lib\Vendor%2Edll"" />", ForbiddenReferenceRules.VendoredDllToken)]
    [InlineData(@"<Content Include=""lib\Vendor&#46;dll"" />", ForbiddenReferenceRules.VendoredDllToken)]
    [InlineData(@"<Reference Include=""Enter"" />", "Enter")]
    [InlineData(
        @"<Reference Include=""Enter, Version=7.2.0.0, Culture=neutral, processorArchitecture=x86"" />",
        "Enter")]
    [InlineData("<Reference Include='MakeView' />", "MakeView")]
    [InlineData(@"<reference Include=""enter"" />", "Enter")]
    [InlineData(@"<Reference Include=""..\Build\Release\Enter.exe"" />", "Enter")]
    [InlineData(
        @"<?xml version=""1.0"" encoding=""utf-8""?>" +
        @"<Project ToolsVersion=""15.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">" +
        @"<ItemGroup><Reference Include=""Enter"" /></ItemGroup></Project>",
        "Enter")]
    // A name spelt with an XML character reference or an MSBuild %XX escape, in an attribute or a text run.
    [InlineData("<Reference Include='Epi&#46;Core' />", "Epi.Core")]
    [InlineData(@"<Reference Include=""System&#46;Drawing"" />", "System.Drawing")]
    [InlineData(@"<Reference Include=""Epi%2ECore"" />", "Epi.Core")]
    [InlineData(@"<ProjectReference Include=""..\Epi&#46;Windows.Enter\x.csproj"" />", "Epi.Windows")]
    [InlineData(@"<PackageReference Include=""System.Windows&#46;Forms"" />", "System.Windows.Forms")]
    [InlineData(@"<HintPath><![CDATA[..\lib\x]]></HintPath>", "HintPath")]
    [InlineData(@"<None Include=""packages&#46;config"" />", "packages.config")]
    [InlineData(@"<Reference Include=""EpiInfo&#46;Plugin"" />", "EpiInfo.Plugin")]
    [InlineData(@"<Reference Include=""Ent&#101;r"" />", "Enter")]
    [InlineData(@"<Reference Include=""%45nter"" />", "Enter")]
    [InlineData("<AssemblyName>Epi&#x2E;Data.Office</AssemblyName>", "Epi.Data*")]
    [InlineData("<PackageId><![CDATA[Calitha%4Cib]]></PackageId>", "CalithaLib")]
    [InlineData(
        "<DefineConstants>System.Win<![CDATA[dows]]><!-- x -->.Forms</DefineConstants>",
        "System.Windows.Forms")]
    // Malformed text is still scanned as far as it parses.
    [InlineData("<Reference Include='Epi&#46;Core'>", "Epi.Core")]
    public void ProjectTokenDetector_FlagsForbiddenSnippet(string snippet, string expectedToken)
    {
        Assert.Contains(expectedToken, ForbiddenReferenceRules.FindForbiddenProjectTokens(snippet));
    }

    [Theory]
    [InlineData(@"<ProjectReference Include=""..\EpiInfo.Data\EpiInfo.Data.csproj"" />")]
    [InlineData(@"<ProjectReference Include=""..\EpiInfo.Domain\EpiInfo.Domain.csproj"" />")]
    [InlineData(@"<PackageReference Include=""Microsoft.Data.SqlClient"" />")]
    [InlineData(@"<InternalsVisibleTo Include=""EpiInfo.Data.Tests"" />")]
    [InlineData("<!-- legacy Epi.Data.EpiWeb keeps its packages.config and HintPath -->")]
    [InlineData(@"<Compile Include=""../Shared/*.cs"" LinkBase=""Shared"" />")]
    [InlineData(@"<Using Include=""Xunit"" />")]
    [InlineData(@"<Content Remove=""lib\Vendor.dll"" />")]
    [InlineData(@"<Compile Include=""Lexing\Enter.cs"" />")]
    [InlineData(@"<ProjectReference Include=""../EpiInfo.CheckCode/EpiInfo.CheckCode.csproj"" />")]
    [InlineData(@"<!-- <Reference Include=""Enter"" /> -->")]
    [InlineData(@"<Reference Include=""Enterprise.Library"" />")]
    [InlineData(
        @"<?xml version=""1.0"" encoding=""utf-8""?>" +
        @"<Project><ItemGroup><Using Include=""Xunit"" /></ItemGroup></Project>")]
    // An allowed name spelt with an escape, and a legacy name split across two elements, which MSBuild never joins.
    [InlineData(@"<ProjectReference Include=""..\EpiInfo&#46;Data\EpiInfo.Data.csproj"" />")]
    [InlineData(@"<InternalsVisibleTo Include=""EpiInfo%2EData.Tests"" />")]
    [InlineData("<RootNamespace>Epi.</RootNamespace><AssemblyName>Core</AssemblyName>")]
    public void ProjectTokenDetector_IgnoresAllowedSnippet(string snippet)
    {
        Assert.Empty(ForbiddenReferenceRules.FindForbiddenProjectTokens(snippet));
    }

    // Text MSBuild cannot load fails the scan once with the parser's reason rather than passing unread or throwing.
    [Theory]
    [InlineData(@"<Reference Include=""Enter"">")]
    [InlineData(@"<ItemGroup><Content Include=""readme.txt""></ItemGroup>")]
    [InlineData(@"<Content Include=lib\Vendor.dll />")]
    [InlineData(@"<!DOCTYPE Project [<!ENTITY v ""lib\Vendor.dll"">]><Project><Content Include=""&v;"" /></Project>")]
    public void ProjectTokenDetector_ReportsMalformedXml(string snippet)
    {
        Assert.Single(
            ForbiddenReferenceRules.FindForbiddenProjectTokens(snippet),
            hit => hit.StartsWith(ForbiddenReferenceRules.MalformedXmlToken + ": ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj", "../Shared/*.cs", true)]
    [InlineData("tests/EpiInfo.Data.Tests/EpiInfo.Data.Tests.csproj", @"..\Shared\**\*.cs", true)]
    [InlineData("src/EpiInfo.Api/EpiInfo.Api.csproj", @"openapi\**\*.json", true)]
    [InlineData("tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj", "../EpiInfo.Data.Tests/Fixtures/rows.json", true)]
    [InlineData("src/EpiInfo.Domain/EpiInfo.Domain.csproj", "../../tests/Shared/TestDatabase.cs", true)]
    [InlineData("tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj", "../*.cs", false)]
    [InlineData("tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj", "../Sha*/*.cs", false)]
    [InlineData("tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj", "../SharedExtra/*.cs", false)]
    [InlineData("tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj", "../../Tests/CreateMapImage.cs", false)]
    [InlineData("tests/EpiInfo.Api.Tests/EpiInfo.Api.Tests.csproj", "../../testdata/forms.json", false)]
    [InlineData("src/EpiInfo.Data/EpiInfo.Data.csproj", @"..\..\Epi.Core\Data\*.cs", false)]
    [InlineData("src/EpiInfo.Data/EpiInfo.Data.csproj", "../../packages/Vendor/lib/*.dll", false)]
    [InlineData("src/EpiInfo.Data/EpiInfo.Data.csproj", "/opt/vendor/Vendor.cs", false)]
    [InlineData("src/EpiInfo.Data/EpiInfo.Data.csproj", "$(VendorRoot)/Vendor.cs", false)]
    public void LinkedLocationRule(string project, string itemSpec, bool allowed)
    {
        Assert.Equal(allowed, LinkOutsideAllowedTree(project, itemSpec, AllowedRoots()) is null);
    }

    [Theory]
    [InlineData("System.Runtime", true)]
    [InlineData("System", true)]
    [InlineData("netstandard", true)]
    [InlineData("Microsoft.AspNetCore.Http.Abstractions", true)]
    [InlineData("Microsoft.Data.SqlClient", true)]
    [InlineData("Dapper", true)]
    [InlineData("EpiInfo.Domain", true)]
    [InlineData("EpiInfo.Api", true)]
    [InlineData("System.Windows.Forms", false)]
    [InlineData("System.Drawing", false)]
    [InlineData("System.Drawing.Common", false)]
    [InlineData("Epi.Core", false)]
    [InlineData("Enter", false)]
    [InlineData("EpiInfo.Plugin", false)]
    [InlineData("GoldParserEngine", false)]
    [InlineData("CalithaLib", false)]
    [InlineData("Newtonsoft.Json", false)]
    public void AssemblyAllowList(string name, bool allowed)
    {
        Assert.Equal(allowed, ForbiddenReferenceRules.IsAllowedReferencedAssembly(name));
    }

    [Theory]
    [InlineData("const string Sql = \"CREATE TABLE [x] (a int)\";", true)]
    [InlineData("\"drop table #t\"", true)]
    [InlineData("\"ALTER PROCEDURE p AS SELECT 1\"", true)]
    [InlineData("\"CREATE UNIQUE INDEX ix ON t(a)\"", true)]
    [InlineData("\"CREATE NONCLUSTERED INDEX ix ON t(a)\"", true)]
    [InlineData("\"CREATE OR ALTER VIEW v AS SELECT 1\"", true)]
    [InlineData("\"DROP DATABASE EpiRehearsal\"", true)]
    [InlineData("await store.CreateAsync(record, ct);", false)]
    [InlineData("var dropDown = DropDown();", false)]
    [InlineData("// never DROP TABLE here", false)]
    [InlineData("/// Quoting defeats \"]; DROP TABLE x\".", false)]
    [InlineData("\"SELECT [Order] FROM [CaseReport]\"", false)]
    [InlineData("\"UPDATE [CaseReport] SET [RecStatus] = 0\"", false)]
    // A statement split across lines of one literal, across literals joined by +, or by an escape or a SQL comment,
    // T-SQL nesting included. No comment starts inside a SQL string or identifier, and a string is also read as SQL.
    [InlineData("const string Sql = @\"CREATE\n    TABLE t (a int)\";", true)]
    [InlineData("const string Sql = \"\"\"\n    DROP\n    VIEW v\n    \"\"\";", true)]
    [InlineData("\"\"\"\"ALTER TABLE \"t\" ADD c int\"\"\"\"", true)]
    [InlineData("string sql = \"CREATE \" +\n    \"TABLE t (a int)\";", true)]
    [InlineData("\"DROP \" + // kind\n    /* still the kind */ \"TABLE t\"", true)]
    [InlineData("\"DROP \"u8 + \"TABLE t\"u8", true)]
    [InlineData("\"CREATE\\nTABLE t\"", true)]
    [InlineData("\"ALTER\\u0020VIEW v AS SELECT 1\"", true)]
    [InlineData("\"CREATE /* note */ TABLE t\"", true)]
    [InlineData("\"DROP -- note\\nTABLE t\"", true)]
    [InlineData("const string Sql = @\"CREATE /* outer /* inner */ outer */\n    TABLE t (id int)\";", true)]
    [InlineData("\"EXEC('CREATE TABLE t')\"", true)]
    [InlineData("\"EXEC('CREATE /* note */ TABLE t')\"", true)]
    [InlineData("\"SELECT 'it''s -- text'; DROP TABLE t\"", true)]
    [InlineData("\"SELECT [a--b], \\\"c/*d\\\" FROM t; DROP TABLE t\"", true)]
    [InlineData("$\"DROP TABLE {name}\"", true)]
    [InlineData("$@\"CREATE\n    TABLE {name} (a int)\"", true)]
    [InlineData("@$\"DROP TABLE {name}\"", true)]
    [InlineData("$$\"\"\"CREATE TABLE {{name}} (a int)\"\"\"", true)]
    [InlineData("$\"x{(drop ? \"DROP TABLE t\" : \"\")}\"", true)]
    [InlineData("var u = \"http://a\"; var s = \"CREATE TABLE t\";", true)]
    // Literals joined by + are read as the value they concatenate to: a split keyword is joined, two are fused.
    [InlineData("\"DROP \" + \"TABLE t\"", true)]
    [InlineData("\"CRE\" + \"ATE\\nTABLE t\"", true)]
    [InlineData("\"CREATE\" + \"TABLE t\"", false)]
    [InlineData("\"http://a\" + \"CREATE TABLE t\"", false)]
    // Object kinds beyond tables, indexes and routines, and statements that do not end at the object kind.
    [InlineData("\"CREATE SEQUENCE s START WITH 1\"", true)]
    [InlineData("\"CREATE ROLE r\"", true)]
    [InlineData("\"CREATE SYNONYM s FOR t\"", true)]
    [InlineData("\"CREATE ASSEMBLY a FROM 0x00\"", true)]
    [InlineData("\"CREATE SERVER ROLE r\"", true)]
    [InlineData("\"CREATE XML SCHEMA COLLECTION c AS N''\"", true)]
    [InlineData("\"CREATE CLUSTERED COLUMNSTORE INDEX ix ON t\"", true)]
    [InlineData("\"CREATE PRIMARY XML INDEX ix ON t(c)\"", true)]
    [InlineData("\"CREATE MESSAGE TYPE m\"", true)]
    [InlineData("\"CREATE EXTERNAL DATA SOURCE d WITH (LOCATION = 'x')\"", true)]
    [InlineData("\"DROP PARTITION SCHEME ps\"", true)]
    [InlineData("\"ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 1\"", true)]
    [InlineData("\"ALTER AUTHORIZATION ON SCHEMA::s TO u\"", true)]
    [InlineData("\"ALTER TABLE t DROP COLUMN c\"", true)]
    [InlineData("\"DROP TABLE IF EXISTS t\"", true)]
    [InlineData("\"CREATE OR ALTER TRIGGER tr ON t AFTER INSERT AS SELECT 1\"", true)]
    // A comment or literal left open in an #if branch ends at the next conditional directive, so code after it is read.
    [InlineData("#if false\n/*\n#endif\nconst string S = \"CREATE TABLE t\";", true)]
    [InlineData("#if false\nvar v = @\"\n#endif\nconst string S = \"CREATE TABLE t\";", true)]
    [InlineData("#if false\nvar r = \"\"\"\n#endif\nconst string S = \"\"\"CREATE TABLE t\"\"\";", true)]
    [InlineData("#if X\nint a;\n#else\n/*\n#endif\nconst string S = \"CREATE TABLE t\";", true)]
    // C# and SQL comments, preprocessor lines, character literals, code and prose are never statement text.
    [InlineData("var x = 1; // DROP TABLE t", false)]
    [InlineData("/* CREATE TABLE t */ var x = 1;", false)]
    [InlineData("#region CREATE TABLE helpers", false)]
    [InlineData("/*\n#if DEBUG\nconst string S = \"CREATE TABLE t\";\n#endif\n*/\nvar x = 1;", false)]
    [InlineData("#endif\n#if X\n#endif\n/*\n#if DEBUG\nconst string S = \"CREATE TABLE t\";\n#endif\n*/", false)]
    [InlineData("char q = '\"'; // DROP TABLE t", false)]
    [InlineData("char q = '\\''; // DROP TABLE t", false)]
    [InlineData("Create(Table); Drop(Index); Alter(View);", false)]
    [InlineData("\"Create a record from the submitted field values.\"", false)]
    [InlineData("\"You do not have permission to create records.\"", false)]
    [InlineData("\"SELECT [CreatedTable], [AlterValue] FROM t\"", false)]
    [InlineData("\"DROP -- TABLE\"", false)]
    [InlineData("\"SELECT 1 -- DROP TABLE t\"", false)]
    [InlineData("\"/* a /* b */ DROP TABLE t */ SELECT 1\"", false)]
    [InlineData("\"CREATE /* never closed TABLE t\"", false)]
    [InlineData("$\"CREATE {kind} t\"", false)]
    [InlineData("\"CREATE \" + kind + \"TABLE t\"", false)]
    public void DdlDetector(string source, bool expected)
    {
        IReadOnlyList<ForbiddenReferenceRules.DdlHit> hits = ForbiddenReferenceRules.FindDdlStatements(source);
        Assert.True(
            expected == hits.Count > 0,
            $"Expected {(expected ? "a" : "no")} DDL hit, found {hits.Count}: {string.Join("; ", hits)}");
    }

    // Each hit names the line the first character of its verb sits on, counting LF and CRLF breaks, and the head of
    // the joined statement.
    [Theory]
    [InlineData(
        "namespace N;\n\ninternal static class Q\n{\n    private const string Sql = @\"\n        CREATE\n" +
        "        SEQUENCE s\";\n}\n",
        6,
        "CREATE SEQUENCE s")]
    [InlineData(
        "// DROP TABLE x\r\nconst string Sql = \"\"\"\r\n    SELECT 1;\r\n    DROP\r\n    TABLE t;\r\n    \"\"\";\r\n",
        4,
        "DROP TABLE t;")]
    [InlineData("var sql =\n    \"ALTER \" +\n    \"TABLE t ADD c int\";\n", 2, "ALTER TABLE t ADD c int")]
    [InlineData("var sql = \"CR\" +\n    \"EATE\\nTABLE t\";\n", 1, "CREATE TABLE t")]
    [InlineData("var sql = @\"\n    CREATE /* a /* b */ c */\n    TABLE t\";\n", 2, "CREATE TABLE t")]
    [InlineData("#if false\nvar r = \"\"\"\n#endif\nvar s = \"DROP \" +\n    \"VIEW v\";\n", 4, "DROP VIEW v")]
    [InlineData(
        "char q = '\"'; /* one\ntwo */ var s = \"x\";\nvar t = $\"CREATE ROLE {name}\";\n",
        3,
        "CREATE ROLE {...}")]
    public void DdlDetector_ReportsTheVerbLineAndStatementHead(string source, int line, string excerpt)
    {
        ForbiddenReferenceRules.DdlHit hit = Assert.Single(ForbiddenReferenceRules.FindDdlStatements(source));
        Assert.Equal(line, hit.Line);
        Assert.Equal(excerpt, hit.Excerpt);
    }

    // The walk self-tests below build their trees in a fresh temporary folder, never in the repository, and delete it.
    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("Bin")]
    [InlineData("OBJ")]
    public void FileWalk_PrunesBuildOutputFoldersAndYieldsEveryOtherFile(string buildFolder)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("epiinfo-scan-");
        try
        {
            string[] kept =
            [
                "Top.cs",
                ".hidden",
                ".hidden-folder/Hidden.cs",
                "nested/deeper/Keep.cs",
                $"nested/deeper/{buildFolder}",
                $"nested/{buildFolder}-like/Keep.cs",
            ];
            string[] pruned =
            [
                $"{buildFolder}/Top.dll",
                $"nested/{buildFolder}/Vendor.dll",
                $"nested/{buildFolder}/deeper/packages.config",
            ];
            foreach (string file in kept.Concat(pruned))
            {
                WriteEmptyFile(temp.FullName, file);
            }

            IEnumerable<string> yielded = FilesWithin(temp.FullName, temp.FullName)
                .Select(path => RelativeTo(temp.FullName, path));
            Assert.Equal(kept.Order(StringComparer.Ordinal), yielded.Order(StringComparer.Ordinal));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    // One link per row: inside the tree to a folder and to a file outside it, as a file named bin that the folder
    // pruning must not hide, as the scan root itself, and as a folder between the scan root and the boundary.
    [Theory]
    [InlineData("tree/nested/linked", true, "tree")]
    [InlineData("tree/nested/Linked.dll", false, "tree")]
    [InlineData("tree/nested/bin", false, "tree")]
    [InlineData("tree", true, "tree")]
    [InlineData("tree", true, "tree/nested")]
    public void FileWalk_RejectsEveryLinkInsteadOfFollowingOrSkippingIt(string link, bool linksFolder, string scanRoot)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("epiinfo-scan-");
        try
        {
            string boundary = Path.Combine(temp.FullName, "boundary");
            string outside = Path.Combine(temp.FullName, "outside");
            WriteEmptyFile(outside, "nested/Outside.cs");
            WriteEmptyFile(outside, "Outside.dll");
            WriteEmptyFile(boundary, "Beside.cs");
            if (link != "tree")
            {
                WriteEmptyFile(boundary, "tree/nested/Keep.cs");
            }

            string target = linksFolder ? outside : Path.Combine(outside, "Outside.dll");
            if (!TryCreateSymbolicLink(Under(boundary, link), target, linksFolder))
            {
                return;
            }

            var yielded = new List<string>();
            InvalidOperationException rejection = Assert.Throws<InvalidOperationException>(
                () => yielded.AddRange(FilesWithin(boundary, Under(boundary, scanRoot))));

            Assert.Contains($"'{link}' is a symbolic link", rejection.Message, StringComparison.Ordinal);
            Assert.All(yielded, path => Assert.Equal("tree/nested/Keep.cs", RelativeTo(boundary, path)));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    // Build output redirected elsewhere: a bin or obj folder, in any case, that links to a folder outside the tree is
    // pruned by name like a real one, so the walk neither rejects it nor yields anything it holds.
    [Theory]
    [InlineData("bin")]
    [InlineData("OBJ")]
    public void FileWalk_PrunesALinkedBuildOutputFolderWithoutEnteringIt(string buildFolder)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("epiinfo-scan-");
        try
        {
            string boundary = Path.Combine(temp.FullName, "boundary");
            string outside = Path.Combine(temp.FullName, "outside");
            WriteEmptyFile(outside, "nested/Outside.cs");
            WriteEmptyFile(outside, "Outside.dll");
            WriteEmptyFile(boundary, "tree/nested/Keep.cs");
            if (!TryCreateSymbolicLink(Under(boundary, $"tree/nested/{buildFolder}"), outside, linksFolder: true))
            {
                return;
            }

            IEnumerable<string> yielded = FilesWithin(boundary, Under(boundary, "tree"))
                .Select(path => RelativeTo(boundary, path));

            Assert.Equal("tree/nested/Keep.cs", Assert.Single(yielded));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    // The parent, a sibling, and a sibling whose name starts with the boundary's own name.
    [Theory]
    [InlineData("..")]
    [InlineData("../beside")]
    [InlineData("../boundary-twin")]
    public void FileWalk_RejectsARootOutsideItsBoundary(string scanRoot)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("epiinfo-scan-");
        try
        {
            string boundary = Path.Combine(temp.FullName, "boundary");
            WriteEmptyFile(boundary, "Inside.cs");
            WriteEmptyFile(temp.FullName, "beside/Outside.cs");
            WriteEmptyFile(temp.FullName, "boundary-twin/Outside.cs");

            var yielded = new List<string>();
            InvalidOperationException rejection = Assert.Throws<InvalidOperationException>(
                () => yielded.AddRange(FilesWithin(boundary, Under(boundary, scanRoot))));

            Assert.Contains("lies outside", rejection.Message, StringComparison.Ordinal);
            Assert.Empty(yielded);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    // The read self-tests below build their trees the same way. Real files at the top of the boundary and in real
    // folders below it are read; a missing file, or a file in a missing folder, reads as null.
    [Theory]
    [InlineData("Top.props", true)]
    [InlineData("tree/nested/Real.props", true)]
    [InlineData("tree/nested/Missing.props", false)]
    [InlineData("gone/Missing.props", false)]
    public void FileRead_ReturnsTheTextOfARealFileAndNullForAMissingOne(string file, bool exists)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("epiinfo-scan-");
        try
        {
            string boundary = Path.Combine(temp.FullName, "boundary");
            string[] realFiles = ["Top.props", "tree/nested/Real.props"];
            foreach (string real in realFiles)
            {
                WriteEmptyFile(boundary, real);
                File.WriteAllText(Under(boundary, real), $"<Project><!-- {real} --></Project>");
            }

            Assert.Equal(
                exists ? $"<Project><!-- {file} --></Project>" : null,
                ReadFileWithin(boundary, Under(boundary, file)));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    // One link per row, each to the mirror of its own path in a tree outside the boundary: the file itself, to a file
    // or to nothing, and a folder above the file, to a folder that holds the file or to nothing.
    [Theory]
    [InlineData("tree/nested/Linked.props", "tree/nested/Linked.props", false, true)]
    [InlineData("tree/nested/Dangling.props", "tree/nested/Dangling.props", false, false)]
    [InlineData("tree", "tree/nested/Outside.props", true, true)]
    [InlineData("tree/nested", "tree/nested/Outside.props", true, true)]
    [InlineData("tree/nested", "tree/nested/Outside.props", true, false)]
    public void FileRead_RejectsALinkAtTheFileOrAboveIt(string link, string file, bool linksFolder, bool targetExists)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("epiinfo-scan-");
        try
        {
            string boundary = Path.Combine(temp.FullName, "boundary");
            string outside = Path.Combine(temp.FullName, "outside");
            WriteEmptyFile(boundary, "Beside.props");
            if (targetExists)
            {
                WriteEmptyFile(outside, file);
                File.WriteAllText(Under(outside, file), "<Project><Reference Include=\"Enter\" /></Project>");
            }

            string linkPath = Under(boundary, link);
            Directory.CreateDirectory(
                Path.GetDirectoryName(linkPath) ?? throw new InvalidOperationException($"'{linkPath}' has no parent."));
            if (!TryCreateSymbolicLink(linkPath, Under(outside, link), linksFolder))
            {
                return;
            }

            InvalidOperationException rejection = Assert.Throws<InvalidOperationException>(
                () => ReadFileWithin(boundary, Under(boundary, file)));

            Assert.Contains($"'{link}' is a symbolic link", rejection.Message, StringComparison.Ordinal);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    // The boundary's parent, a sibling, and a sibling whose name starts with the boundary's own name.
    [Theory]
    [InlineData("../Outside.props")]
    [InlineData("../beside/Outside.props")]
    [InlineData("../boundary-twin/Outside.props")]
    public void FileRead_RejectsAFileOutsideItsBoundary(string file)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("epiinfo-scan-");
        try
        {
            string boundary = Path.Combine(temp.FullName, "boundary");
            WriteEmptyFile(boundary, "Inside.props");
            WriteEmptyFile(temp.FullName, "Outside.props");
            WriteEmptyFile(temp.FullName, "beside/Outside.props");
            WriteEmptyFile(temp.FullName, "boundary-twin/Outside.props");

            InvalidOperationException rejection = Assert.Throws<InvalidOperationException>(
                () => ReadFileWithin(boundary, Under(boundary, file)));

            Assert.Contains("does not lie below", rejection.Message, StringComparison.Ordinal);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }


    /// <summary>The eleven MSBuild files the token scan reads: the eight projects and the three props files.</summary>
    private static IReadOnlyList<string> ScannedMsBuildFiles() => [.. Projects, .. PropsFiles];

    /// <summary>
    /// The new tree a file item may link into, as full paths: the eight project folders and <c>tests/Shared</c>, the
    /// linked source of the database-backed test projects (AAP 0.4.1.6).
    /// </summary>
    private static string[] AllowedRoots() => [.. Projects.Select(ProjectDirectory), Full("tests", "Shared")];

    /// <summary>
    /// The MSBuild files whose items <paramref name="project"/> evaluates: the project, the
    /// <c>Directory.Build.props</c> of its tree and the root <c>Directory.Packages.props</c>. Item paths in all three
    /// resolve against the project's own folder.
    /// </summary>
    private static string[] MsBuildSourcesOf(string project) =>
    [
        project,
        project.StartsWith("src/", StringComparison.Ordinal)
            ? "src/Directory.Build.props"
            : "tests/Directory.Build.props",
        "Directory.Packages.props",
    ];

    private static string ProjectDirectory(string project) =>
        Path.GetDirectoryName(Full(project.Split('/')))
        ?? throw new InvalidOperationException($"'{project}' has no parent directory.");

    /// <summary>
    /// Why a file item of <paramref name="project"/> whose <c>Include</c> entry is <paramref name="itemSpec"/> links a
    /// folder outside <paramref name="allowedRoots"/>; null when the folder it names lies inside one of them. The
    /// folder is resolved by path arithmetic against the project folder and is never listed or read.
    /// </summary>
    private static string? LinkOutsideAllowedTree(string project, string itemSpec, IReadOnlyList<string> allowedRoots)
    {
        string? folder = ForbiddenReferenceRules.ItemSpecFolder(itemSpec);
        if (folder is null)
        {
            return $"'{itemSpec}' names a folder only MSBuild evaluation can resolve, so it cannot be checked";
        }

        string resolved = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(ProjectDirectory(project), folder)));
        return allowedRoots.Any(root => IsWithin(resolved, root))
            ? null
            : $"'{itemSpec}' links {Rel(resolved)}, outside the eight project folders and tests/Shared";
    }

    // Ordinal, as on a case-sensitive file system a differently cased folder is a different folder.
    private static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) || IsBelow(path, root);

    private static string Full(params string[] relative) => RepositoryPaths.Combine(relative);

    private static string Rel(string fullPath) =>
        Path.GetRelativePath(RepositoryPaths.Root, fullPath).Replace('\\', '/');

    /// <summary>
    /// Every file below <paramref name="directory"/>, outside <c>bin/</c> and <c>obj/</c>, as full paths: the walk of
    /// <see cref="FilesWithin"/> bounded by the repository root, so a link fails the scan with its repository-relative
    /// path.
    /// </summary>
    private static IEnumerable<string> FilesUnder(string directory) => FilesWithin(RepositoryPaths.Root, directory);

    /// <summary>
    /// The text of the file at the repository-relative, slash-separated path <paramref name="relative"/>, read by
    /// <see cref="ReadFileWithin"/> bounded by the repository root, so a link fails the read with its
    /// repository-relative path; <see langword="null"/> when the file does not exist.
    /// </summary>
    private static string? ReadRepositoryFile(string relative) =>
        ReadFileWithin(RepositoryPaths.Root, Full(relative.Split('/')));

    private static IEnumerable<string> ProjectFilesUnder(string directory) =>
        FilesUnder(directory)
            .Where(path => string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every file below <paramref name="root"/>, as full paths, found by listing one folder at a time with
    /// <see cref="TopLevelScan"/>. A folder named <c>bin</c> or <c>obj</c>, in any case, is pruned by name before
    /// anything else, links included, so it is never entered; a file of either name is an ordinary entry. No other
    /// link is followed and none is skipped: a symbolic link, junction or other reparse point among the entries, at the
    /// root, or between the root and <paramref name="boundary"/> fails the scan, because following it could read
    /// outside the tree or loop, and skipping it would hide what it holds, a linked DLL included. Every other entry is
    /// verified to lie inside the root before it is used.
    /// </summary>
    /// <param name="boundary">
    /// The tree the scan stays inside, for the repository scans the repository root. It is where the tree lies, so it
    /// is not itself checked for a link; messages name paths relative to it.
    /// </param>
    /// <param name="root">The folder to walk, at or below <paramref name="boundary"/>.</param>
    /// <returns>The files, yielded lazily; the root and the folders above it are checked before this returns.</returns>
    /// <exception cref="InvalidOperationException">
    /// The root lies outside the boundary, a link lies at the root or between it and the boundary, or the walk meets a
    /// link or an entry outside the root.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The root, or a folder between it and the boundary, is not an existing folder.
    /// </exception>
    internal static IEnumerable<string> FilesWithin(string boundary, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullBoundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));
        return WalkFiles(fullBoundary, RealFolderWithin(fullBoundary, root));
    }

    /// <summary>
    /// The full path of <paramref name="folder"/>, once it and every folder between it and
    /// <paramref name="boundary"/> are verified to be existing real folders: the check made before any folder is
    /// listed, because a link at the folder or above it would move the whole listing out of the boundary.
    /// </summary>
    /// <param name="boundary">
    /// The tree the listing stays inside, for the repository scans the repository root. It is not itself checked for a
    /// link unless it is the folder; messages name paths relative to it.
    /// </param>
    /// <param name="folder">The folder to list, at or below <paramref name="boundary"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// The folder lies outside the boundary, or a link lies at the folder or between it and the boundary.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The folder, or a folder between it and the boundary, is not an existing folder.
    /// </exception>
    internal static string RealFolderWithin(string boundary, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string fullBoundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));
        string fullFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!string.Equals(fullFolder, fullBoundary, StringComparison.Ordinal) && !IsBelow(fullFolder, fullBoundary))
        {
            throw new InvalidOperationException(
                $"The scan root '{fullFolder}' lies outside '{fullBoundary}', the tree the scan must stay inside.");
        }

        string? missing = MissingFolderOnLinkFreePath(fullBoundary, fullFolder);
        if (missing is not null)
        {
            throw new DirectoryNotFoundException(
                $"'{RelativeTo(fullBoundary, missing)}' is not an existing folder, so it cannot be scanned.");
        }

        return fullFolder;
    }

    /// <summary>
    /// The text of the file at <paramref name="path"/>, read only once neither the file nor any folder between it and
    /// <paramref name="boundary"/> is a link, so a read by fixed path stays inside the boundary as a walk does.
    /// </summary>
    /// <param name="boundary">
    /// The tree the read stays inside, for the repository reads the repository root. It is not itself checked for a
    /// link; messages name paths relative to it.
    /// </param>
    /// <param name="path">The file to read, below <paramref name="boundary"/>.</param>
    /// <returns>
    /// The file's text, or <see langword="null"/> when no file is there, or a folder above it does not exist. A link
    /// is checked without following it, so a dangling link is rejected, never taken for a missing file.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The file lies outside the boundary, or it or a folder between it and the boundary is a link.
    /// </exception>
    internal static string? ReadFileWithin(string boundary, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullBoundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!IsBelow(fullPath, fullBoundary))
        {
            throw new InvalidOperationException(
                $"The file '{fullPath}' does not lie below '{fullBoundary}', the tree the read must stay inside.");
        }

        string folder = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"'{fullPath}' has no parent directory.");
        bool foldersExist = !IsBelow(folder, fullBoundary) ||
                            MissingFolderOnLinkFreePath(fullBoundary, folder) is null;
        var file = new FileInfo(fullPath);
        if (IsLink(file))
        {
            throw LinkRejected(fullBoundary, file);
        }

        return foldersExist && file.Exists ? File.ReadAllText(fullPath) : null;
    }

    // Rejects a link at the folder or at any folder between it and the boundary, and returns the deepest of those
    // folders that does not exist, or null when all exist. Every level is checked for a link, a missing one included,
    // so a link is reported as a link even where a folder below it is missing.
    private static string? MissingFolderOnLinkFreePath(string boundary, string folder)
    {
        string? missing = null;
        string? current = folder;
        do
        {
            var info = new DirectoryInfo(current);
            if (IsLink(info))
            {
                throw LinkRejected(boundary, info);
            }

            if (missing is null && !info.Exists)
            {
                missing = current;
            }

            current = Path.GetDirectoryName(current);
        }
        while (current is not null && IsBelow(current, boundary));

        return missing;
    }

    private static IEnumerable<string> WalkFiles(string boundary, string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            DirectoryInfo folder = pending.Pop();
            foreach (FileSystemInfo entry in folder.GetFileSystemInfos("*", TopLevelScan))
            {
                // Build output is pruned by name before anything else, so a bin/ or obj/ folder is never entered, even
                // as a link to a folder elsewhere, which enumerates as a DirectoryInfo. A file entry of either name, a
                // link to a file included, is checked like any other entry.
                if (entry is DirectoryInfo &&
                    (string.Equals(entry.Name, "bin", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(entry.Name, "obj", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string path = Path.GetFullPath(entry.FullName);
                if (!IsBelow(path, root))
                {
                    throw new InvalidOperationException(
                        $"'{RelativeTo(boundary, path)}' lies outside the scan root '{RelativeTo(boundary, root)}'.");
                }

                if (IsLink(entry))
                {
                    throw LinkRejected(boundary, entry);
                }

                if (entry is DirectoryInfo subfolder)
                {
                    pending.Push(subfolder);
                }
                else
                {
                    yield return path;
                }
            }
        }
    }

    // A dangling link does not exist as a folder or file but still has a target; a missing path has neither, and its
    // attributes read as every flag set, so they are consulted only for an entry that exists.
    private static bool IsLink(FileSystemInfo entry) =>
        entry.LinkTarget is not null || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static InvalidOperationException LinkRejected(string boundary, FileSystemInfo link) =>
        new($"'{RelativeTo(boundary, link.FullName)}' is a symbolic link, junction or other reparse point" +
            (link.LinkTarget is { } target ? $" to '{target}'" : string.Empty) +
            ". The architecture scans follow no link, which could lead outside the scanned tree or into a loop, and " +
            "skip none, which would hide what it holds; replace it with the real file or folder.");

    private static bool IsBelow(string path, string folder) =>
        path.StartsWith(
            Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

    private static string RelativeTo(string boundary, string path) =>
        Path.GetRelativePath(boundary, path).Replace('\\', '/');

    private static string Under(string folder, string relative) => Path.Combine([folder, .. relative.Split('/')]);

    private static void WriteEmptyFile(string folder, string relative)
    {
        string path = Under(folder, relative);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"'{path}' has no parent directory."));
        File.WriteAllBytes(path, []);
    }

    /// <summary>
    /// Creates a symbolic link at <paramref name="path"/>, or returns <see langword="false"/> after asserting that the
    /// operating system refused it for want of the privilege to create one.
    /// </summary>
    private static bool TryCreateSymbolicLink(string path, string target, bool linksFolder)
    {
        const int PrivilegeNotHeld = unchecked((int)0x80070522);
        try
        {
            if (linksFolder)
            {
                Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }

            return true;
        }
        catch (Exception refusal) when (OperatingSystem.IsWindows() &&
                                        refusal is UnauthorizedAccessException or IOException)
        {
            // Windows lets an unprivileged process create a symbolic link only in Developer Mode. Such a host cannot
            // hold the link the walk defends against either: git checks a committed symbolic link out there as a plain
            // file holding the target path, which the walk reads like any other file. The refusal must be that
            // privilege error, so any other failure still fails the test.
            Assert.True(
                refusal is UnauthorizedAccessException || refusal.HResult == PrivilegeNotHeld,
                $"Creating a symbolic link failed for a reason other than the missing privilege: {refusal}");
            return false;
        }
    }

    private static void AssertSameSet(IReadOnlyCollection<string> expected, IEnumerable<string> actual, string scope)
    {
        var found = new SortedSet<string>(actual, StringComparer.Ordinal);
        string[] missing = [.. expected.Where(path => !found.Contains(path)).Order(StringComparer.Ordinal)];
        string[] unexpected = [.. found.Where(path => !expected.Contains(path, StringComparer.Ordinal))];
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            Assert.Fail(
                $"{scope} must hold exactly its {expected.Count} EpiInfo.Web.sln project files." +
                $"{Environment.NewLine}Missing: {Describe(missing)}" +
                $"{Environment.NewLine}Unexpected: {Describe(unexpected)}");
        }
    }

    private static void AssertNone(IReadOnlyCollection<string> offenders, string message)
    {
        if (offenders.Count > 0)
        {
            Assert.Fail($"{message}:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
        }
    }

    private static string Describe(IReadOnlyCollection<string> paths) =>
        paths.Count == 0 ? "none" : string.Join(", ", paths);

    [GeneratedRegex(
        @"^Project\(""\{(?<type>[0-9A-Fa-f-]+)\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SolutionProjectEntry();
}

/// <summary>
/// The detection rules behind <see cref="ForbiddenReferenceTests"/>, pure functions over text so the repository scans
/// and the detector self-tests share one definition.
/// </summary>
/// <remarks>
/// The regular expressions are source-generated, the codebase's convention, so each is built once at compile time.
/// The only exclusions are XML comments in MSBuild text and, because the DDL scan reads only string-literal content,
/// comments and preprocessor lines in C# source: neither references anything or executes, and both legitimately name
/// legacy projects or injection payloads.
/// </remarks>
internal static partial class ForbiddenReferenceRules
{
    /// <summary>The hit reported for an item that includes a <c>.dll</c> file.</summary>
    internal const string VendoredDllToken = "vendored .dll";

    /// <summary>
    /// The start of the hit reported for MSBuild text that is not well-formed XML; the parser's reason follows it.
    /// </summary>
    internal const string MalformedXmlToken = "malformed MSBuild XML";

    // The bare <AssemblyName> each legacy Epi.Windows.* project builds instead of its project name, as the legacy
    // .csproj files set it: Epi.Windows.Enter builds Enter (AAP 0.1.1), Epi.Windows.MakeView builds MakeView, and so
    // on. The list is fixed here so the test never reads a legacy file. LegacyNames covers the prefixed identities.
    private static readonly string[] LegacyAssemblyIdentities =
    [
        "Enter",
        "Analysis",
        "AnalysisDashboard",
        "Dashboard",
        "DataPackager",
        "DataUnpackager",
        "MakeView",
        "Mapping",
        "Menu",
        "WebSurveyExporter",
    ];

    // The item types that bring a file into the build, and so may link a folder outside their project.
    private static readonly string[] FileItemTypes =
    [
        "Compile",
        "Content",
        "None",
        "EmbeddedResource",
        "AdditionalFiles",
    ];

    private static readonly string[] PlainTokens =
    [
        "System.Windows.Forms",
        "System.Drawing",
        "UseWindowsForms",
        "UseWPF",
        "GoldParserEngine",
        "CalithaLib",
        "HintPath",
        "packages.config",
    ];

    private static readonly (Regex Pattern, string Name)[] LegacyNames =
    [
        (EpiCore(), "Epi.Core"),
        (EpiWindows(), "Epi.Windows"),
        (EpiEnter(), "Epi.Enter.CheckCodeEngine"),
        (EpiData(), "Epi.Data*"),
        (EpiInfoPlugin(), "EpiInfo.Plugin"),
    ];

    private static readonly string[] ProductAssemblies =
    [
        "EpiInfo.Domain",
        "EpiInfo.CheckCode",
        "EpiInfo.Data",
        "EpiInfo.Api",
    ];

    /// <summary>
    /// The forbidden references named by MSBuild text, outside XML comments, each at most once.
    /// </summary>
    /// <remarks>
    /// Plain tokens and legacy project names are matched in the comment-free text, so an element such as
    /// <c>UseWindowsForms</c> is caught wherever it stands, and in the decoded content MSBuild reads (see
    /// <see cref="ReadDecodedContent"/>), so a name spelt with an XML character reference such as <c>&amp;#46;</c> or
    /// an MSBuild escape such as <c>%2E</c> is caught as well. The text is also read as XML items (see
    /// <see cref="ReadItemEntries"/>): an <c>Include</c> or <c>Update</c> entry naming a <c>.dll</c> is a vendored DLL
    /// whatever its quoting, casing, escaping or place in a <c>;</c> list, and a <c>Reference</c> entry whose simple
    /// assembly name is a bare legacy identity such as <c>Enter</c> is reported by that name. Text that is not
    /// well-formed XML is reported once as <see cref="MalformedXmlToken"/> with the parser's reason, after the hits
    /// found in the comment-free text and in what the XML passes read before the error.
    /// </remarks>
    /// <param name="msbuildText">The content of a project or props file.</param>
    /// <returns>The display name of every rule that matched; empty when none did.</returns>
    internal static IReadOnlyList<string> FindForbiddenProjectTokens(string msbuildText)
    {
        ArgumentNullException.ThrowIfNull(msbuildText);
        string text = XmlComment().Replace(msbuildText, string.Empty);
        string decoded = ReadDecodedContent(msbuildText, out XmlException? malformed);

        var hits = new List<string>();
        hits.AddRange(PlainTokens.Where(token =>
            text.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains(token, StringComparison.OrdinalIgnoreCase)));
        hits.AddRange(LegacyNames
            .Where(rule => rule.Pattern.IsMatch(text) || rule.Pattern.IsMatch(decoded))
            .Select(rule => rule.Name));
        try
        {
            foreach (MsBuildItemEntry entry in ReadItemEntries(msbuildText))
            {
                if (entry.Value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !hits.Contains(VendoredDllToken))
                {
                    hits.Add(VendoredDllToken);
                }

                if (!string.Equals(entry.ItemType, "Reference", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string simpleName = SimpleAssemblyName(entry.Value);
                string? legacy = LegacyAssemblyIdentities.FirstOrDefault(identity =>
                    string.Equals(identity, simpleName, StringComparison.OrdinalIgnoreCase));
                if (legacy is not null && !hits.Contains(legacy))
                {
                    hits.Add(legacy);
                }
            }
        }
        catch (XmlException exception)
        {
            // Both passes parse through CreateMsBuildReader, so they stop at the same error; it is reported once.
            malformed ??= exception;
        }

        if (malformed is not null)
        {
            hits.Add($"{MalformedXmlToken}: {malformed.Message}");
        }

        return hits;
    }

    /// <summary>
    /// The <c>Include</c> entries of the file items in MSBuild text (<c>Compile</c>, <c>Content</c>, <c>None</c>,
    /// <c>EmbeddedResource</c>, <c>AdditionalFiles</c>), trimmed and unescaped. <c>Update</c> and <c>Remove</c> add no
    /// file, so they link nothing.
    /// </summary>
    /// <param name="msbuildText">The content of a project or props file.</param>
    /// <returns>
    /// A lazy sequence, which throws <see cref="XmlException"/> while enumerated if the text is malformed.
    /// </returns>
    internal static IEnumerable<string> FindFileItemIncludes(string msbuildText)
    {
        ArgumentNullException.ThrowIfNull(msbuildText);
        return ReadItemEntries(msbuildText)
            .Where(entry =>
                entry.Attribute == ItemAttribute.Include &&
                FileItemTypes.Contains(entry.ItemType, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.Value);
    }

    /// <summary>
    /// The folder an item spec names, with <c>/</c> separators and a trailing <c>/</c>: the spec up to its first
    /// wildcard, cut back to its last separator, so <c>../Shared/*.cs</c> names <c>../Shared/</c> and <c>*.cs</c> names
    /// the project folder itself, <c>""</c>. Every file the spec can match lies below that folder.
    /// </summary>
    /// <param name="itemSpec">One trimmed, unescaped entry of an <c>Include</c> list.</param>
    /// <returns>
    /// The folder, or null when the spec holds a property, item or metadata expression, whose value only MSBuild
    /// evaluation knows.
    /// </returns>
    internal static string? ItemSpecFolder(string itemSpec)
    {
        ArgumentNullException.ThrowIfNull(itemSpec);
        if (itemSpec.Contains("$(", StringComparison.Ordinal) ||
            itemSpec.Contains("@(", StringComparison.Ordinal) ||
            itemSpec.Contains("%(", StringComparison.Ordinal))
        {
            return null;
        }

        string spec = itemSpec.Replace('\\', '/');
        int wildcard = spec.IndexOfAny(['*', '?']);
        string literal = wildcard < 0 ? spec : spec[..wildcard];
        return literal[..(literal.LastIndexOf('/') + 1)];
    }

    /// <summary>
    /// Every <c>Include</c> and <c>Update</c> list entry of every element in MSBuild text, in document order.
    /// </summary>
    /// <remarks>
    /// The text is read as an XML fragment, so a single item parses as well as a whole project, with DTDs prohibited,
    /// no resolver and comments skipped. Elements are taken by local name, so a legacy project in the
    /// <c>msbuild/2003</c> namespace reads like an SDK-style one, and only unqualified attributes are MSBuild's own.
    /// Each list is split on <c>;</c>, trimmed, stripped of empty entries and unescaped as MSBuild does (<c>%2E</c> is
    /// a dot). <c>Remove</c> takes items away, so it is not read.
    /// </remarks>
    /// <exception cref="XmlException">
    /// The text is not well-formed XML; raised while the sequence is enumerated.
    /// </exception>
    private static IEnumerable<MsBuildItemEntry> ReadItemEntries(string msbuildText)
    {
        using XmlReader reader = CreateMsBuildReader(msbuildText);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            string itemType = reader.LocalName;
            for (bool more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
            {
                string name = reader.LocalName;
                ItemAttribute? attribute =
                    reader.NamespaceURI.Length > 0 ? null :
                    string.Equals(name, "Include", StringComparison.OrdinalIgnoreCase) ? ItemAttribute.Include :
                    string.Equals(name, "Update", StringComparison.OrdinalIgnoreCase) ? ItemAttribute.Update :
                    null;
                if (attribute is null)
                {
                    continue;
                }

                string[] entries = reader.Value.Split(
                    ';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (string entry in entries)
                {
                    yield return new MsBuildItemEntry(itemType, attribute.Value, Unescape(entry));
                }
            }

            reader.MoveToElement();
        }
    }

    /// <summary>
    /// The content MSBuild reads from its text, one piece per line: the local name of every element and attribute,
    /// every attribute value, and each run of text and CDATA that stands between two tags.
    /// </summary>
    /// <remarks>
    /// The reader of <see cref="CreateMsBuildReader"/> decodes character and entity references, so
    /// <c>Epi&amp;#46;Core</c> reads as <c>Epi.Core</c>, and skips comments. Each value and run is followed by its
    /// MSBuild-unescaped form when that differs, so <c>Epi%2ECore</c> reads as <c>Epi.Core</c> too; splitting a value
    /// on <c>;</c> first would change no match, because no forbidden name holds a <c>;</c>. A run joins the text and
    /// CDATA nodes between two tags across any comment, so a name split by a comment or a CDATA section is read whole,
    /// and the line break after each piece keeps a name from matching across two pieces.
    /// </remarks>
    /// <param name="msbuildText">The content of a project or props file.</param>
    /// <param name="malformed">
    /// The parser's error when the text is not well-formed XML, the content then being what was read before it; null
    /// when the whole text was read.
    /// </param>
    /// <returns>The decoded pieces, each followed by a line break.</returns>
    private static string ReadDecodedContent(string msbuildText, out XmlException? malformed)
    {
        var content = new StringBuilder();
        var run = new StringBuilder();
        malformed = null;
        try
        {
            using XmlReader reader = CreateMsBuildReader(msbuildText);
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or
                    XmlNodeType.SignificantWhitespace)
                {
                    run.Append(reader.Value);
                    continue;
                }

                AppendDecodedValue(content, run.ToString());
                run.Clear();
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                content.Append(reader.LocalName).Append('\n');
                for (bool more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                {
                    content.Append(reader.LocalName).Append('\n');
                    AppendDecodedValue(content, reader.Value);
                }

                reader.MoveToElement();
            }
        }
        catch (XmlException exception)
        {
            malformed = exception;
        }

        AppendDecodedValue(content, run.ToString());
        return content.ToString();
    }

    // One decoded value on its own line, then its MSBuild-unescaped form on the next when that differs.
    private static void AppendDecodedValue(StringBuilder content, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        content.Append(value).Append('\n');
        string unescaped = Unescape(value);
        if (!string.Equals(unescaped, value, StringComparison.Ordinal))
        {
            content.Append(unescaped).Append('\n');
        }
    }

    /// <summary>
    /// A reader over MSBuild text as an XML fragment, so a single item parses as well as a whole project, with DTDs
    /// prohibited, no resolver and comments skipped. Every pass over the text reads through it, so all of them accept
    /// the same text and stop at the same error.
    /// </summary>
    private static XmlReader CreateMsBuildReader(string msbuildText) =>
        XmlReader.Create(
            new StringReader(msbuildText),
            new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
            });

    // MSBuild reads %XX in an item spec or a value as the character with that hexadecimal code.
    private static string Unescape(string entry) =>
        entry.Contains('%')
            ? MsBuildEscape().Replace(entry, match => ((char)Convert.ToInt32(match.Groups["hex"].Value, 16)).ToString())
            : entry;

    // A Reference entry is an assembly name, optionally followed by ", Version=..." and the other identity parts, or
    // the path of the assembly file; its identity is the simple name either way.
    private static string SimpleAssemblyName(string referenceEntry)
    {
        int comma = referenceEntry.IndexOf(',', StringComparison.Ordinal);
        string name = (comma < 0 ? referenceEntry : referenceEntry[..comma]).Trim();
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name.Replace('\\', '/'))
            : name;
    }

    /// <summary>The item attributes whose lists name the files or assemblies an item brings in.</summary>
    private enum ItemAttribute
    {
        Include,
        Update,
    }

    /// <summary>One list entry of an item: its element's local name, its attribute and its value.</summary>
    private readonly record struct MsBuildItemEntry(string ItemType, ItemAttribute Attribute, string Value);

    /// <summary>Whether an assembly name belongs to the Windows Forms or System.Drawing families.</summary>
    internal static bool IsDesktopUiAssembly(string name) =>
        name.StartsWith("System.Windows.Forms", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("System.Drawing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a <c>src/</c> assembly may reference <paramref name="name"/>: the shared framework, the pinned packages
    /// and the four product assemblies. <c>EpiInfo.Plugin</c> is legacy, so no <c>EpiInfo.</c> prefix is allowed.
    /// </summary>
    internal static bool IsAllowedReferencedAssembly(string name)
    {
        if (IsDesktopUiAssembly(name))
        {
            return false;
        }

        return name.StartsWith("System.", StringComparison.Ordinal) ||
               name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
               name is "System" or "Dapper" or "netstandard" ||
               ProductAssemblies.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    /// The <c>CREATE</c>, <c>ALTER</c> and <c>DROP</c> statements held by the string literals of a C# source file, in
    /// source order.
    /// </summary>
    /// <remarks>
    /// Only string-literal content is matched, so comments, preprocessor lines, character literals and code never are.
    /// Regular, verbatim, raw and interpolated literals are all read. Each interpolation hole is lexed as code, so a
    /// literal nested in it is read too, and the hole itself is a break in the outer text that no statement spans.
    /// Literals joined only by <c>+</c> are matched as the value their concatenation produces, character for character
    /// with no separator, so a statement split across lines or literals is still found and two words the join fuses
    /// into one are not. Each unit is matched in its SQL views (<see cref="LiteralUnit.SqlViews"/>), where every SQL
    /// comment is one space, so keywords separated by nested comments are found and keywords inside a comment never
    /// are. A statement found in more than one view is reported once.
    /// </remarks>
    /// <param name="csharpSource">The full text of a <c>.cs</c> file.</param>
    /// <returns>One hit per statement head; empty when there is none.</returns>
    internal static IReadOnlyList<DdlHit> FindDdlStatements(string csharpSource)
    {
        ArgumentNullException.ThrowIfNull(csharpSource);
        List<int> lineStarts = LineStarts(csharpSource);
        var found = new List<(int Offset, DdlHit Hit)>();
        var reported = new HashSet<int>();
        foreach (LiteralUnit unit in LiteralLexer.Read(csharpSource))
        {
            foreach (LiteralUnit view in unit.SqlViews())
            {
                string text = view.Text;
                foreach (Match match in DdlStatement().Matches(text))
                {
                    int offset = view.OffsetOf(match.Index);
                    if (reported.Add(offset))
                    {
                        found.Add((offset, new DdlHit(LineOf(lineStarts, offset), Excerpt(text, match))));
                    }
                }
            }
        }

        return [.. found.OrderBy(entry => entry.Offset).Select(entry => entry.Hit)];
    }

    /// <summary>Where each line of <paramref name="text"/> starts, by C#'s definition of a line break.</summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            if (IsNewLine(current))
            {
                starts.Add(index + 1);
            }
        }

        return starts;
    }

    /// <summary>The 1-based line holding <paramref name="offset"/>.</summary>
    private static int LineOf(List<int> lineStarts, int offset)
    {
        int found = lineStarts.BinarySearch(offset);
        return found >= 0 ? found + 1 : ~found;
    }

    /// <summary>
    /// The matched statement head and the text that follows it, up to <see cref="ExcerptContext"/> characters, with
    /// whitespace collapsed and an interpolation hole shown as <c>{...}</c>.
    /// </summary>
    private static string Excerpt(string text, Match match)
    {
        int length = Math.Min(text.Length - match.Index, match.Length + ExcerptContext);
        string head = text
            .Substring(match.Index, length)
            .Replace(HoleBreak.ToString(), "{...}", StringComparison.Ordinal);
        string collapsed = string.Join(' ', head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return match.Index + length < text.Length ? collapsed + " ..." : collapsed;
    }

    /// <summary>The characters C# treats as line breaks.</summary>
    private static bool IsNewLine(char value) => value is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex XmlComment();

    // Each legacy name is anchored so it cannot match inside EpiInfo.Data, EpiInfo.Domain or Microsoft.Data.SqlClient.
    [GeneratedRegex(@"(?<![A-Za-z0-9])Epi\.Core", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpiCore();

    [GeneratedRegex(@"(?<![A-Za-z0-9])Epi\.Windows", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpiWindows();

    [GeneratedRegex(@"(?<![A-Za-z0-9])Epi\.Enter", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpiEnter();

    [GeneratedRegex(@"(?<![A-Za-z0-9])Epi\.Data", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpiData();

    [GeneratedRegex(@"(?<![A-Za-z0-9])EpiInfo\.Plugin", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpiInfoPlugin();

    [GeneratedRegex("%(?<hex>[0-9A-Fa-f]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex MsBuildEscape();

    /// <summary>How many characters after a statement head its excerpt shows.</summary>
    private const int ExcerptContext = 40;

    /// <summary>
    /// Stands in the lexed text for an interpolation hole: a word break that no gap between keywords spans, because
    /// the hole's value is unknown. A noncharacter, so no literal content is mistaken for it.
    /// </summary>
    private const char HoleBreak = '\uFFFF';

    // The gap between two SQL keywords: whitespace with line breaks. The statement is matched in a SQL view, where each
    // comment is already one space, so an unclosed or nested comment never reaches the regex. The gap must follow each
    // keyword, which with \b before the verb and after the kind keeps CreateAsync, DropDown, AlterValue and Created
    // from matching. An interpolation hole (\uFFFF, HoleBreak) is not whitespace, so no gap spans one.
    private const string SqlGap = @"\s+";

    private const string DdlVerb = @"\b(?:CREATE(?:" + SqlGap + "OR" + SqlGap + "ALTER)?|ALTER|DROP)" + SqlGap;

    private const string DdlIndex =
        "(?:(?:UNIQUE|CLUSTERED|NONCLUSTERED|COLUMNSTORE|(?:PRIMARY|SELECTIVE)" + SqlGap + "XML" +
        "|XML|SPATIAL|JSON|VECTOR|FULLTEXT)" + SqlGap + ")*INDEX";

    // The T-SQL object kinds of the CREATE, ALTER and DROP statement families; a multiword kind is matched as a phrase,
    // so prose such as "create a record" or "create records" never is.
    private const string DdlKind =
        "(?:" + DdlIndex +
        "|TABLE|VIEW|PROC(?:EDURE)?|FUNCTION|TRIGGER|SCHEMA|DATABASE|TYPE|LOGIN|USER|ROLE|SEQUENCE|SYNONYM|ASSEMBLY" +
        "|AGGREGATE|CERTIFICATE|CREDENTIAL|DEFAULT|RULE|STATISTICS|QUEUE|SERVICE|ROUTE|CONTRACT|ENDPOINT" +
        "|AUTHORIZATION|SIGNATURE|FEDERATION" +
        "|MATERIALIZED" + SqlGap + "VIEW" +
        "|(?:APPLICATION|SERVER)" + SqlGap + "ROLE" +
        "|(?:ASYMMETRIC|SYMMETRIC|MASTER|SERVICE" + SqlGap + "MASTER|COLUMN" + SqlGap + "(?:MASTER|ENCRYPTION)" +
        "|DATABASE" + SqlGap + "ENCRYPTION)" + SqlGap + "KEY" +
        "|DATABASE" + SqlGap + "SCOPED" + SqlGap + "(?:CREDENTIAL|CONFIGURATION)" +
        "|(?:SERVER|DATABASE)" + SqlGap + "AUDIT(?:" + SqlGap + "SPECIFICATION)?" +
        "|SERVER" + SqlGap + "CONFIGURATION" +
        "|MESSAGE" + SqlGap + "TYPE" +
        "|REMOTE" + SqlGap + "(?:SERVICE" + SqlGap + "BINDING|TABLE)" +
        "|BROKER" + SqlGap + "PRIORITY" +
        "|EVENT" + SqlGap + "(?:NOTIFICATION|SESSION)" +
        "|FULLTEXT" + SqlGap + "(?:CATALOG|STOPLIST)" +
        "|PARTITION" + SqlGap + "(?:FUNCTION|SCHEME)" +
        "|RESOURCE" + SqlGap + "(?:POOL|GOVERNOR)" +
        "|EXTERNAL" + SqlGap + "(?:TABLE|DATA" + SqlGap + "SOURCE|FILE" + SqlGap + "FORMAT|LIBRARY|LANGUAGE" +
        "|RESOURCE" + SqlGap + "POOL|MODEL|STREAM(?:ING" + SqlGap + "JOB)?)" +
        "|WORKLOAD" + SqlGap + "(?:GROUP|CLASSIFIER)" +
        "|SEARCH" + SqlGap + "PROPERTY" + SqlGap + "LIST" +
        "|SECURITY" + SqlGap + "POLICY" +
        "|AVAILABILITY" + SqlGap + "GROUP" +
        "|CRYPTOGRAPHIC" + SqlGap + "PROVIDER" +
        "|XML" + SqlGap + "SCHEMA" + SqlGap + "COLLECTION" +
        "|DIAGNOSTICS" + SqlGap + "SESSION" +
        "|SENSITIVITY" + SqlGap + "CLASSIFICATION" +
        @")\b";

    [GeneratedRegex(DdlVerb + DdlKind, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DdlStatement();

    /// <summary>One DDL statement found in C# source.</summary>
    /// <param name="Line">The 1-based line of the statement's verb.</param>
    /// <param name="Excerpt">The statement head and a little of what follows, whitespace collapsed.</param>
    internal sealed record DdlHit(int Line, string Excerpt);

    /// <summary>The content of a run of joined string literals, each character with its source offset.</summary>
    private sealed class LiteralUnit
    {
        private readonly StringBuilder text = new();
        private readonly List<int> offsets = [];

        internal int Length => text.Length;

        internal string Text => text.ToString();

        internal int OffsetOf(int index) => offsets[index];

        internal void Append(char value, int offset)
        {
            text.Append(value);
            offsets.Add(offset);
        }

        /// <summary>
        /// The unit's text as SQL Server reads it, then the same reading of the content of each <c>'…'</c> string and
        /// <c>"…"</c> quoted identifier in it, recursively, because <c>EXEC</c> and <c>sp_executesql</c> run a
        /// string's content as SQL. In every view each comment is one space: <c>--</c> to the end of its line, and
        /// <c>/* */</c> with T-SQL nesting, an unclosed one running to the end of the text. A comment never starts
        /// inside a string, a quoted identifier or a <c>[…]</c> identifier, whose content stays in the view.
        /// </summary>
        /// <returns>The views, each character mapped to its source offset; the unit's own view first.</returns>
        internal List<LiteralUnit> SqlViews()
        {
            var views = new List<LiteralUnit>();
            AddSqlViews(text.ToString(), offsets, views);
            return views;
        }

        private static void AddSqlViews(string sql, List<int> sqlOffsets, List<LiteralUnit> views)
        {
            var view = new LiteralUnit();
            views.Add(view);
            int index = 0;
            while (index < sql.Length)
            {
                char current = sql[index];
                char next = index + 1 < sql.Length ? sql[index + 1] : '\0';
                if (current == '-' && next == '-')
                {
                    view.Append(' ', sqlOffsets[index]);
                    while (index < sql.Length && !IsNewLine(sql[index]))
                    {
                        index++;
                    }
                }
                else if (current == '/' && next == '*')
                {
                    view.Append(' ', sqlOffsets[index]);
                    index = BlockCommentEnd(sql, index);
                }
                else if (current is '\'' or '"' or '[')
                {
                    index = AppendQuoted(sql, sqlOffsets, index, view, views);
                }
                else
                {
                    view.Append(current, sqlOffsets[index]);
                    index++;
                }
            }
        }

        /// <summary>
        /// Where the block comment opened at <paramref name="start"/> ends, counting nested openings.
        /// </summary>
        private static int BlockCommentEnd(string sql, int start)
        {
            int depth = 0;
            int index = start;
            while (index + 1 < sql.Length)
            {
                if (sql[index] == '/' && sql[index + 1] == '*')
                {
                    depth++;
                    index += 2;
                }
                else if (sql[index] == '*' && sql[index + 1] == '/')
                {
                    index += 2;
                    if (--depth == 0)
                    {
                        return index;
                    }
                }
                else
                {
                    index++;
                }
            }

            return sql.Length;
        }

        /// <summary>
        /// Copies the string or identifier opened at <paramref name="start"/> into <paramref name="view"/> unchanged,
        /// up to its closing character or the end of the text; a doubled closing character is one character of its
        /// content. The content of a string or a quoted identifier is then read as SQL in a view of its own.
        /// </summary>
        /// <returns>The index just past the closing character.</returns>
        private static int AppendQuoted(
            string sql, List<int> sqlOffsets, int start, LiteralUnit view, List<LiteralUnit> views)
        {
            char opening = sql[start];
            char closing = opening == '[' ? ']' : opening;
            var content = new StringBuilder();
            var contentOffsets = new List<int>();
            view.Append(opening, sqlOffsets[start]);
            int index = start + 1;
            while (index < sql.Length)
            {
                int at = index;
                char current = sql[at];
                view.Append(current, sqlOffsets[at]);
                index++;
                if (current == closing)
                {
                    if (index == sql.Length || sql[index] != closing)
                    {
                        break;
                    }

                    view.Append(closing, sqlOffsets[index]);
                    index++;
                }

                content.Append(current);
                contentOffsets.Add(sqlOffsets[at]);
            }

            if (opening != '[')
            {
                AddSqlViews(content.ToString(), contentOffsets, views);
            }

            return index;
        }
    }

    /// <summary>
    /// A single-pass C# lexer that collects the content of every string literal. It skips comments, preprocessor lines
    /// and character literals, decodes escapes, reads verbatim, raw, interpolated and UTF-8 literals, and lexes each
    /// interpolation hole as code. Literals separated only by <c>+</c>, whitespace and comments form one unit, which
    /// holds their concatenated value. Text in a disabled <c>#if</c> region is lexed as code, so a literal there is
    /// still read. Conditional directives are counted, never evaluated: a comment, verbatim or raw literal, or
    /// interpolation hole that opens inside an <c>#if</c> region ends before the next <c>#if</c>, <c>#elif</c>,
    /// <c>#else</c> or <c>#endif</c> line, because in a disabled branch it is skipped text that must not swallow the
    /// directive and the active code after it. One that opens outside every region keeps directive lines as content,
    /// so a commented-out block holding <c>#if</c> lines stays one comment.
    /// </summary>
    private sealed class LiteralLexer
    {
        private readonly string source;
        private readonly List<LiteralUnit> units = [];
        private int position;

        /// <summary>
        /// How many <c>#if</c> regions enclose the code being lexed, counted from its directive lines.
        /// </summary>
        private int conditionalDepth;

        /// <summary>
        /// Set when a construct that opened inside an <c>#if</c> region ends at a directive line, and cleared where
        /// code lexing resumes; every enclosing literal and hole ends with it.
        /// </summary>
        private bool directiveCut;

        private LiteralLexer(string source) => this.source = source;

        /// <summary>Every literal unit of <paramref name="source"/>, in the order each unit ends.</summary>
        internal static List<LiteralUnit> Read(string source)
        {
            var lexer = new LiteralLexer(source);
            lexer.LexCode(holeBraces: 0, regularFormat: false);
            return lexer.units;
        }

        /// <summary>
        /// Lexes code to the end of the source or, inside an interpolation hole (<paramref name="holeBraces"/> above
        /// zero), through the closing braces that end the hole. <paramref name="regularFormat"/> marks a hole of a
        /// regular interpolated literal, whose format clause holds escapes and ends with the literal's line.
        /// </summary>
        private void LexCode(int holeBraces, bool regularFormat)
        {
            bool inHole = holeBraces > 0;
            bool lineStart = !inHole;
            bool joinPending = false;
            int depth = 0;
            LiteralUnit? unit = null;

            while (position < source.Length)
            {
                if (directiveCut)
                {
                    // What was read before the directive line is complete. A hole ends too, so the cut reaches the
                    // top level, where the line is read as a directive.
                    Complete(unit);
                    unit = null;
                    joinPending = false;
                    if (inHole)
                    {
                        return;
                    }

                    directiveCut = false;
                }

                char current = source[position];
                if (IsNewLine(current))
                {
                    if (inHole && CutsAtDirective())
                    {
                        continue;
                    }

                    position++;
                    lineStart = !inHole;
                    continue;
                }

                if (char.IsWhiteSpace(current))
                {
                    position++;
                    continue;
                }

                if (current == '#' && lineStart)
                {
                    ReadDirective();
                    continue;
                }

                lineStart = false;
                if (current == '/' && At(1) is '/' or '*')
                {
                    SkipComment();
                    continue;
                }

                if (IsLiteralStart(out int dollars, out bool verbatim, out int quote))
                {
                    // A literal joined by + continues its unit with no separator, so the unit holds the value the
                    // concatenation produces.
                    if (unit is null || !joinPending)
                    {
                        Complete(unit);
                        unit = new LiteralUnit();
                    }

                    joinPending = false;
                    ReadLiteral(unit, dollars, verbatim, quote);
                    continue;
                }

                if (current == '+' && At(1) is not ('+' or '=') && unit is not null && !joinPending)
                {
                    joinPending = true;
                    position++;
                    continue;
                }

                // Any other token ends the run of joined literals.
                Complete(unit);
                unit = null;
                joinPending = false;

                if (current == '\'')
                {
                    SkipCharLiteral();
                }
                else if (inHole && depth == 0 && current == '}')
                {
                    SkipRun('}', holeBraces);
                    return;
                }
                else if (inHole && depth == 0 && current == ':' && At(1) != ':')
                {
                    SkipFormatClause(regularFormat);
                    SkipRun('}', holeBraces);
                    return;
                }
                else if (IsIdentifierPart(current))
                {
                    while (position < source.Length && IsIdentifierPart(source[position]))
                    {
                        position++;
                    }
                }
                else
                {
                    if (inHole)
                    {
                        depth = current switch
                        {
                            '(' or '[' or '{' => depth + 1,
                            ')' or ']' or '}' => Math.Max(0, depth - 1),
                            _ => depth,
                        };
                    }

                    position += current == ':' && At(1) == ':' ? 2 : 1;
                }
            }

            Complete(unit);
        }

        /// <summary>
        /// Whether a string literal starts at the current position: <c>"</c>, <c>@"</c>, or any run of <c>$</c> with
        /// an optional <c>@</c> on either side, before <c>"</c>.
        /// </summary>
        private bool IsLiteralStart(out int dollars, out bool verbatim, out int quote)
        {
            int index = position;
            verbatim = CharAt(index) == '@';
            if (verbatim)
            {
                index++;
            }

            dollars = 0;
            while (CharAt(index) == '$')
            {
                dollars++;
                index++;
            }

            if (!verbatim && dollars > 0 && CharAt(index) == '@')
            {
                verbatim = true;
                index++;
            }

            quote = index;
            return CharAt(index) == '"';
        }

        /// <summary>Reads the literal opened at <paramref name="quote"/>, and any <c>u8</c> suffix.</summary>
        private void ReadLiteral(LiteralUnit unit, int dollars, bool verbatim, int quote)
        {
            position = quote;
            int quotes = verbatim ? 1 : CountRun(quote, '"');
            if (quotes >= 3)
            {
                position += quotes;
                ReadRaw(unit, quotes, dollars);
            }
            else
            {
                position++;
                if (verbatim)
                {
                    ReadVerbatim(unit, interpolated: dollars > 0);
                }
                else
                {
                    ReadRegular(unit, interpolated: dollars > 0);
                }
            }

            if (At(0) is 'u' or 'U' && At(1) == '8' && !IsIdentifierPart(At(2)))
            {
                position += 2;
            }
        }

        /// <summary>A regular literal after its opening quote: escapes decoded, ended by a quote or a line.</summary>
        private void ReadRegular(LiteralUnit unit, bool interpolated)
        {
            // A hole that a directive line cut ends the literal with it.
            while (position < source.Length && !directiveCut)
            {
                char current = source[position];
                if (current == '"')
                {
                    position++;
                    return;
                }

                if (IsNewLine(current))
                {
                    return;
                }

                if (current == '\\')
                {
                    ReadEscape(unit);
                }
                else if (!interpolated || !TryReadBrace(unit, current, regularFormat: true))
                {
                    unit.Append(current, position);
                    position++;
                }
            }
        }

        /// <summary>
        /// A verbatim literal after its opening quote: <c>""</c> is a quote; lines are content, except that one opened
        /// inside an <c>#if</c> region ends before a conditional directive line, in its text or in one of its holes.
        /// </summary>
        private void ReadVerbatim(LiteralUnit unit, bool interpolated)
        {
            while (position < source.Length && !directiveCut)
            {
                char current = source[position];
                if (current == '"')
                {
                    if (At(1) != '"')
                    {
                        position++;
                        return;
                    }

                    unit.Append('"', position);
                    position += 2;
                }
                else if (IsNewLine(current) && CutsAtDirective())
                {
                    return;
                }
                else if (!interpolated || !TryReadBrace(unit, current, regularFormat: false))
                {
                    unit.Append(current, position);
                    position++;
                }
            }
        }

        /// <summary>
        /// Handles a brace in a regular or verbatim interpolated literal: a doubled brace is one content brace, and a
        /// single opening brace starts a hole.
        /// </summary>
        /// <returns>Whether the brace was consumed; false leaves a lone <c>}</c> to be read as content.</returns>
        private bool TryReadBrace(LiteralUnit unit, char current, bool regularFormat)
        {
            if (current is not ('{' or '}'))
            {
                return false;
            }

            if (At(1) == current)
            {
                unit.Append(current, position);
                position += 2;
                return true;
            }

            if (current == '}')
            {
                return false;
            }

            unit.Append(HoleBreak, position);
            position++;
            LexCode(holeBraces: 1, regularFormat);
            return true;
        }

        /// <summary>
        /// A raw literal after its opening quotes, single- or multiline, closed by <paramref name="quotes"/> quotes.
        /// With <paramref name="dollars"/> above zero, that many braces open a hole and a shorter run is content. One
        /// opened inside an <c>#if</c> region ends before a conditional directive line, in its text or in one of its
        /// holes.
        /// </summary>
        private void ReadRaw(LiteralUnit unit, int quotes, int dollars)
        {
            while (position < source.Length && !directiveCut)
            {
                char current = source[position];
                if (current == '"')
                {
                    int run = CountRun(position, '"');
                    if (run >= quotes)
                    {
                        AppendRun(unit, '"', run - quotes);
                        position += quotes;
                        return;
                    }

                    AppendRun(unit, '"', run);
                }
                else if (current == '{' && dollars > 0 && CountRun(position, '{') >= dollars)
                {
                    AppendRun(unit, '{', CountRun(position, '{') - dollars);
                    unit.Append(HoleBreak, position);
                    position += dollars;
                    LexCode(holeBraces: dollars, regularFormat: false);
                }
                else if (IsNewLine(current) && CutsAtDirective())
                {
                    return;
                }
                else
                {
                    unit.Append(current, position);
                    position++;
                }
            }
        }

        /// <summary>Decodes the escape sequence at the current backslash, mapped to the backslash's offset.</summary>
        private void ReadEscape(LiteralUnit unit)
        {
            int start = position;
            char kind = At(1);
            if (position + 1 >= source.Length || IsNewLine(kind))
            {
                unit.Append('\\', start);
                position++;
                return;
            }

            position += 2;
            switch (kind)
            {
                case 'x' or 'u':
                    AppendCodePoint(unit, ReadHex(4), start);
                    break;
                case 'U':
                    AppendCodePoint(unit, ReadHex(8), start);
                    break;
                default:
                    unit.Append(
                        kind switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            'v' => '\v',
                            'f' => '\f',
                            'a' => '\a',
                            'b' => '\b',
                            '0' => '\0',
                            'e' => '\u001B',
                            _ => kind,
                        },
                        start);
                    break;
            }
        }

        /// <summary>Reads up to <paramref name="maxDigits"/> hexadecimal digits at the current position.</summary>
        private long ReadHex(int maxDigits)
        {
            long value = 0;
            for (int digits = 0; digits < maxDigits && char.IsAsciiHexDigit(At(0)); digits++)
            {
                char digit = At(0);
                value = (value * 16) + (digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10);
                position++;
            }

            return value;
        }

        private static void AppendCodePoint(LiteralUnit unit, long codePoint, int offset)
        {
            if (codePoint <= char.MaxValue)
            {
                unit.Append((char)codePoint, offset);
                return;
            }

            string text = codePoint <= 0x10FFFF ? char.ConvertFromUtf32((int)codePoint) : "\uFFFD";
            foreach (char value in text)
            {
                unit.Append(value, offset);
            }
        }

        /// <summary>Skips a character literal such as <c>'"'</c> or <c>'\''</c>; it is never statement text.</summary>
        private void SkipCharLiteral()
        {
            position++;
            if (At(0) == '\\')
            {
                position += IsNewLine(At(1)) ? 1 : 2;
            }
            else if (position < source.Length && !IsNewLine(At(0)))
            {
                position++;
            }

            while (position < source.Length && source[position] != '\'' && !IsNewLine(source[position]))
            {
                position++;
            }

            if (At(0) == '\'')
            {
                position++;
            }
        }

        /// <summary>
        /// Skips a hole's format clause, from its colon up to the closing brace. In a regular literal the clause holds
        /// escapes and cannot pass the literal's closing quote or line. One opened inside an <c>#if</c> region cannot
        /// pass a conditional directive line.
        /// </summary>
        private void SkipFormatClause(bool regularFormat)
        {
            position++;
            while (position < source.Length && source[position] != '}')
            {
                char current = source[position];
                if (regularFormat && (current == '"' || IsNewLine(current)))
                {
                    return;
                }

                if (IsNewLine(current) && CutsAtDirective())
                {
                    return;
                }

                position += regularFormat && current == '\\' && position + 1 < source.Length ? 2 : 1;
            }
        }

        /// <summary>
        /// Skips a <c>//</c> comment to the end of its line, or a <c>/* */</c> comment to its end. A block comment
        /// opened inside an <c>#if</c> region also ends before a conditional directive line.
        /// </summary>
        private void SkipComment()
        {
            if (At(1) == '/')
            {
                SkipToLineEnd();
                return;
            }

            if (conditionalDepth == 0)
            {
                int end = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
                position = end < 0 ? source.Length : end + 2;
                return;
            }

            position += 2;
            while (position < source.Length)
            {
                if (source[position] == '*' && At(1) == '/')
                {
                    position += 2;
                    return;
                }

                if (IsNewLine(source[position]) && CutsAtDirective())
                {
                    return;
                }

                position++;
            }
        }

        /// <summary>
        /// Reads a directive line met in code. Conditionals are counted, never evaluated: <c>#if</c> opens a region,
        /// <c>#endif</c> closes one, and a stray <c>#endif</c> leaves the count at zero.
        /// </summary>
        private void ReadDirective()
        {
            string name = DirectiveName(position);
            if (name == "if")
            {
                conditionalDepth++;
            }
            else if (name == "endif" && conditionalDepth > 0)
            {
                conditionalDepth--;
            }

            SkipToLineEnd();
        }

        /// <summary>
        /// At a line break inside a comment, literal, hole or format clause that opened inside an <c>#if</c> region,
        /// whether the next line is an <c>#if</c>, <c>#elif</c>, <c>#else</c> or <c>#endif</c> directive. When it is,
        /// <see cref="directiveCut"/> is set and the construct ends at the break.
        /// </summary>
        private bool CutsAtDirective()
        {
            if (conditionalDepth == 0)
            {
                return false;
            }

            int next = position + (source[position] == '\r' && At(1) == '\n' ? 2 : 1);
            if (DirectiveName(next) is not ("if" or "elif" or "else" or "endif"))
            {
                return false;
            }

            directiveCut = true;
            return true;
        }

        /// <summary>
        /// The name of the directive on the line that starts at <paramref name="index"/>: the word after its
        /// <c>#</c>, with only whitespace before the <c>#</c> and between it and the word. Empty when the line is not
        /// a directive.
        /// </summary>
        private string DirectiveName(int index)
        {
            index = SkipLineSpace(index);
            if (CharAt(index) != '#')
            {
                return string.Empty;
            }

            int start = SkipLineSpace(index + 1);
            int end = start;
            while (end < source.Length && IsIdentifierPart(source[end]))
            {
                end++;
            }

            return source[start..end];
        }

        /// <summary>The first index from <paramref name="index"/> that is not whitespace within the line.</summary>
        private int SkipLineSpace(int index)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index]) && !IsNewLine(source[index]))
            {
                index++;
            }

            return index;
        }

        private void SkipToLineEnd()
        {
            while (position < source.Length && !IsNewLine(source[position]))
            {
                position++;
            }
        }

        private void SkipRun(char value, int max)
        {
            for (int skipped = 0; skipped < max && At(0) == value; skipped++)
            {
                position++;
            }
        }

        private void AppendRun(LiteralUnit unit, char value, int count)
        {
            for (int appended = 0; appended < count; appended++)
            {
                unit.Append(value, position);
                position++;
            }
        }

        private int CountRun(int start, char value)
        {
            int end = start;
            while (end < source.Length && source[end] == value)
            {
                end++;
            }

            return end - start;
        }

        private void Complete(LiteralUnit? unit)
        {
            if (unit is { Length: > 0 })
            {
                units.Add(unit);
            }
        }

        private char At(int ahead) => CharAt(position + ahead);

        private char CharAt(int index) => index < source.Length ? source[index] : '\0';

        private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';
    }
}
