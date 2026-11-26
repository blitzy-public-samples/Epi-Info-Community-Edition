using System.Reflection;
using System.Text.RegularExpressions;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Storage;

namespace EpiInfo.Api.Tests.Architecture;

/// <summary>
/// Keeps the legacy desktop stack out of the new tree (AAP 0.5.4, 0.7.6): <c>EpiInfo.Web.sln</c> holds exactly the eight
/// SDK-style projects, neither they nor the three props files they import name a forbidden reference, their folders
/// hold no <c>packages.config</c> or DLL, the four <c>src/</c> assemblies reference only the shared framework, the pinned
/// packages and each other, and no <c>src/</c> source line holds DDL statement text.
/// </summary>
/// <remarks>
/// <para>
/// The class needs no database: it carries no <c>Integration</c> trait and joins no collection, so every
/// <c>dotnet test</c>, <c>--filter "Category!=Integration"</c> included, runs it.
/// </para>
/// <para>
/// It reads only <c>EpiInfo.Web.sln</c>, <c>Directory.Packages.props</c>, <c>src/**</c>, <c>tests/Directory.Build.props</c>,
/// the four <c>tests/EpiInfo.*.Tests</c> folders and <c>tests/Shared</c>. It never lists the files at the top of
/// <c>tests/</c> and never recurses into <c>tests/</c> as a whole: on a case-insensitive file system that folder is the
/// legacy <c>Tests/</c>, whose <c>Tests.csproj</c> legitimately holds <c>HintPath</c> and <c>packages.config</c>.
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

    // Hidden entries are listed and access errors surface, so nothing in a scanned folder escapes the checks. Name
    // matching is case-insensitive on every platform, so an oddly cased project or folder is still found.
    private static readonly EnumerationOptions RecursiveScan = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

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
        string solution = File.ReadAllText(Full(RepositoryPaths.SolutionFileName));
        IEnumerable<string> solutionProjects = SolutionProjectEntry()
            .Matches(solution)
            .Where(entry => !string.Equals(
                entry.Groups["type"].Value, SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Groups["path"].Value.Replace('\\', '/'));
        AssertSameSet(Projects, solutionProjects, RepositoryPaths.SolutionFileName);

        IEnumerable<string> sourceProjects = ProjectFilesUnder(Full("src")).Select(Rel);
        AssertSameSet(SourceProjects, sourceProjects, "src/");

        // Only the EpiInfo.*.Tests folders are listed; the top of tests/ may be the legacy Tests/ folder.
        IEnumerable<string> testProjects = Directory
            .EnumerateDirectories(Full("tests"), "EpiInfo.*.Tests", TopLevelScan)
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
            string path = Full(relative.Split('/'));
            if (!File.Exists(path))
            {
                missing.Add(relative);
                continue;
            }

            foreach (string token in ForbiddenReferenceRules.FindForbiddenProjectTokens(File.ReadAllText(path)))
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
        var missing = new List<string>();
        var offenders = new List<string>();
        foreach (string project in Projects)
        {
            string directory = Path.GetDirectoryName(Full(project.Split('/')))
                ?? throw new InvalidOperationException($"'{project}' has no parent directory.");
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

        AssertNone(missing, "These project directories must exist but do not");
        AssertNone(offenders, "Project folders must hold no packages.config and no vendored DLL (AAP 0.5.4)");
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
            int lineNumber = 0;
            foreach (string line in File.ReadLines(path))
            {
                lineNumber++;
                if (ForbiddenReferenceRules.IsDdlLine(line))
                {
                    hits.Add($"{Rel(path)}:{lineNumber}: {line.Trim()}");
                }
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
    public void ProjectTokenDetector_IgnoresAllowedSnippet(string snippet)
    {
        Assert.Empty(ForbiddenReferenceRules.FindForbiddenProjectTokens(snippet));
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
    public void DdlDetector(string line, bool expected)
    {
        Assert.Equal(expected, ForbiddenReferenceRules.IsDdlLine(line));
    }

    /// <summary>The eleven MSBuild files the token scan reads: the eight projects and the three props files.</summary>
    private static IReadOnlyList<string> ScannedMsBuildFiles() => [.. Projects, .. PropsFiles];

    private static string Full(params string[] relative) => RepositoryPaths.Combine(relative);

    private static string Rel(string fullPath) =>
        Path.GetRelativePath(RepositoryPaths.Root, fullPath).Replace('\\', '/');

    private static bool IsBuildOutput(string fullPath) =>
        Rel(fullPath)
            .Split('/')
            .Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every file below <paramref name="directory"/>, outside <c>bin/</c> and <c>obj/</c>.</summary>
    private static IEnumerable<string> FilesUnder(string directory) =>
        Directory.EnumerateFiles(directory, "*", RecursiveScan).Where(path => !IsBuildOutput(path));

    private static IEnumerable<string> ProjectFilesUnder(string directory) =>
        FilesUnder(directory)
            .Where(path => string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase));

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
/// The only exclusions are XML comments in MSBuild text and comment-only lines in C# source: neither references
/// anything or executes, and both legitimately name legacy projects or injection payloads.
/// </remarks>
internal static partial class ForbiddenReferenceRules
{
    /// <summary>The hit reported for an item that includes a <c>.dll</c> file.</summary>
    internal const string VendoredDllToken = "vendored .dll";

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
    /// The forbidden references named by MSBuild text, outside XML comments, each at most once and in rule order.
    /// </summary>
    /// <param name="msbuildText">The content of a project or props file.</param>
    /// <returns>The display name of every rule that matched; empty when none did.</returns>
    internal static IReadOnlyList<string> FindForbiddenProjectTokens(string msbuildText)
    {
        ArgumentNullException.ThrowIfNull(msbuildText);
        string text = XmlComment().Replace(msbuildText, string.Empty);

        var hits = new List<string>();
        hits.AddRange(PlainTokens.Where(token => text.Contains(token, StringComparison.OrdinalIgnoreCase)));
        hits.AddRange(LegacyNames.Where(rule => rule.Pattern.IsMatch(text)).Select(rule => rule.Name));
        if (VendoredDll().IsMatch(text))
        {
            hits.Add(VendoredDllToken);
        }

        return hits;
    }

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
    /// Whether a C# source line holds DDL statement text. Lines that open with <c>//</c>, <c>/*</c> or <c>*</c> are
    /// comments and never match; a trailing <c>//</c> is not stripped, because it also occurs inside strings.
    /// </summary>
    internal static bool IsDdlLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/*", StringComparison.Ordinal) ||
            trimmed.StartsWith('*'))
        {
            return false;
        }

        return DdlStatement().IsMatch(line);
    }

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

    [GeneratedRegex(@"Include\s*=\s*""[^""]*\.dll""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendoredDll();

    // The whitespace required after the verb keeps CreateAsync, DropDown, AlterValue and Created from matching.
    [GeneratedRegex(
        @"\b(CREATE|ALTER|DROP)\s+(?:OR\s+ALTER\s+|UNIQUE\s+|(?:NON)?CLUSTERED\s+)*" +
        @"(TABLE|INDEX|VIEW|PROCEDURE|PROC|FUNCTION|TRIGGER|SCHEMA|DATABASE|TYPE|LOGIN|USER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DdlStatement();
}
