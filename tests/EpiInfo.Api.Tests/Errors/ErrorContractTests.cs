using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EpiInfo.Api.Contracts;
using EpiInfo.Api.Endpoints;
using EpiInfo.Api.Errors;
using EpiInfo.Api.Health;
using EpiInfo.Api.Hosting;
using EpiInfo.Api.Json;
using EpiInfo.Api.Services;
using EpiInfo.Api.Tests.Infrastructure;
using EpiInfo.CheckCode;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Search;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace EpiInfo.Api.Tests.Errors;

/// <summary>
/// The error contract of AAP 0.3.4.2: every error response under <c>/api</c> is a ProblemDetails with <c>type</c>,
/// <c>title</c>, <c>status</c>, <c>detail</c> and <c>traceId</c>, a 500's <c>detail</c> is the fixed generic sentence
/// plus that <c>traceId</c>, and the 500 and 503 of a record write carry its <c>writeOutcome</c>; the CORS row of AAP
/// 0.7.6; and the redaction policy of AAP 0.9.3, proved through the API's error pipeline with the committed
/// <c>appsettings*.json</c> logging configuration.
/// </summary>
/// <remarks>
/// <para>
/// Empty error responses produced inside the pipeline, the route miss 404 and the 405, are requested with no
/// <c>Accept</c> header and with <c>Accept</c> values that admit no JSON type, which the framework's default
/// ProblemDetails writer declines. Every HTTP case runs in the Production and the Development environment.
/// </para>
/// <para>
/// No case needs a database. Each HTTP case starts its own host, so captured logs never mix. Wherever the API as
/// <c>Program.cs</c> composes it can show the behaviour, that host is an <see cref="ApiFactory"/>: with no connection
/// string; over a <see cref="FakeDataStore"/>, which injects the store failures; or with the real SQL Server store
/// pointed at a loopback port that refuses connections. The body-classification cases use the same composition over a
/// <see cref="FakeDataStore"/>, on <see cref="ApiFactory"/> for the TestServer framings and on
/// <see cref="KestrelApiFactory"/> for the Kestrel ones. The redaction cases place a fresh sentinel per placement and
/// search the body and every line <see cref="ApiFactoryBase.Logs"/> captured under the committed filters, after
/// asserting that the line they search exists. The writer cases execute the <see cref="ApiProblems"/> writers on a
/// <see cref="DefaultHttpContext"/>. The response-serialization and exception-boundary cases need a failing JSON
/// converter or an endpoint the API does not have, so they start an <see cref="ErrorPipelineHost"/>, which composes
/// the real exception handler, guards, endpoints and services in the pipeline order <c>Program.cs</c> uses, over a
/// <see cref="FakeDataStore"/>, and guards and freezes its listener as <c>Program.cs</c> does.
/// </para>
/// <para>
/// Assertion messages are fixed texts that never quote a response body. Every raw HTTP/1.1 exchange goes through one
/// client, which bounds the connect, the write and the reads by <see cref="HostedWait"/> and, past it, fails with a
/// fixed message naming the request's method, path and framing, never a byte received; a cancellation the caller
/// requests stays a cancellation. The class carries no <c>Integration</c> trait, joins no database collection and
/// takes no <c>DatabaseWriteLock</c>.
/// </para>
/// </remarks>
public sealed class ErrorContractTests
{
    internal const string ProblemMediaType = "application/problem+json";

    private const string NotFoundType = "https://tools.ietf.org/html/rfc9110#section-15.5.5";

    private const string MethodNotAllowedType = "https://tools.ietf.org/html/rfc9110#section-15.5.6";

    private const string CustomizePath = "Customize";

    private const string StatusCodePagePath = "StatusCodePage";

    private const string UnavailableSource = "Unavailable";

    private const string UnavailableWriteSource = "UnavailableWrite";

    // The 500 detail written out here rather than read from ApiProblems, so a change to the product's text fails
    // these cases: one fixed sentence, then the label that introduces the body's traceId.
    internal const string ExpectedUnexpectedSentence = "An unexpected error occurred.";

    internal const string ExpectedTraceIdLabel = " Trace id: ";

    // The rest of the wire contract, written out the same way so that a changed or swapped product text fails these
    // cases and the other classes of this file: the traceId member, the fixed detail of each status and request-level
    // problem, the errors key and message of a non-integer page, each writeOutcome value and the unhealthy status.
    private const string TraceIdMember = "traceId";

    private const string ExpectedInvalidRequestDetail = "the request is invalid";

    private const string ExpectedValidationFailedDetail = "one or more values are invalid";

    private const string ExpectedMalformedJsonDetail = "malformed JSON";

    private const string ExpectedRequestBodyRequiredDetail = "request body is required";

    private const string ExpectedRequestBodyMustBeObjectDetail = "request body must be a JSON object";

    private const string ExpectedRequestBodyRepeatsMemberDetail = "request body names a member more than once";

    private const string ExpectedDuplicateFieldMessage =
        "This field is given more than once; field names are matched without regard to letter case.";

    // The search body's member messages, each under its member's wire name, and its criteria key.
    private const string IncludeDeletedKey = "includeDeleted";

    private const string ExcludeGlobalRecordIdKey = "excludeGlobalRecordId";

    private const string CriteriaKey = "criteria";

    private const string ExpectedIncludeDeletedMessage = "includeDeleted must be true, false or null.";

    private const string ExpectedExcludeGlobalRecordIdMessage = "excludeGlobalRecordId must be a string or null.";

    private const string ExpectedCriteriaNotArrayMessage = "criteria must be an array of {field, value} objects.";

    private const string ExpectedOverLongCriteriaMessage =
        "criteria must be an array of at most 6 {field, value} objects.";

    private const string ExpectedFieldNotStringMessage = "criteria[0] field must be a string.";

    private const string ExpectedValueNotScalarMessage =
        "criteria[0] value must be a string, number, boolean or null.";

    private const string ExpectedValueNotFiniteMessage = "criteria[0] value must be a finite number.";

    private const string ExpectedValueMissingMessage = "criteria[0] needs a value, which may be null.";

    private const string ExpectedValuesMustBeObjectDetail = "values must be an object";

    private const string ExpectedFormNotFoundDetail = "the form does not exist";

    internal const string ExpectedRecordNotFoundDetail = "the record does not exist";

    internal const string ExpectedRecordDeletedDetail = "the record is deleted";

    private const string ExpectedResourceNotFoundDetail = "the resource does not exist";

    private const string ExpectedMethodNotAllowedDetail = "method not allowed";

    private const string ExpectedRequestTooLargeDetail = "request too large";

    private const string ExpectedUnsupportedMediaTypeDetail = "unsupported media type; use application/json";

    private const string ExpectedUnavailableDetail = "the database is unavailable";

    private const string ExpectedPageErrorsKey = "page";

    private const string ExpectedIntegerExpectedMessage = "The value must be an integer.";

    internal const string ExpectedNotWritten = "notWritten";

    internal const string ExpectedUnknown = "unknown";

    internal const string ExpectedWritten = "written";

    private const string ExpectedUnhealthyStatus = "unhealthy";

    // The members of every plain ProblemDetails the API writes, in ordinal order.
    private static readonly string[] ProblemMembers = ["detail", "status", "title", "traceId", "type"];

    // Accept values that admit neither application/json nor application/problem+json, so the framework's default
    // ProblemDetails writer declines them; the last is a browser navigation without a */* entry.
    private static readonly string[] NonJsonAcceptHeaders =
    [
        "text/html",
        "text/plain",
        "application/xml",
        "text/html,application/xhtml+xml;q=0.9",
    ];

    private static string NewSentinel() => "SENTINEL-" + Guid.NewGuid().ToString("N");

    // The four body routes, on the standard fake forms.
    private const string CreateRoute = "create";

    private const string UpdateRoute = "update";

    private const string SearchRoute = "search";

    private const string FieldEventRoute = "field-event";

    // How a body travels: TestServer's client with a declared or an unknown length (sent chunked), or exact HTTP/1.1
    // bytes on Kestrel.
    private const string TestServerContentLength = "TestServer, Content-Length";

    private const string TestServerUnknownLength = "TestServer, unknown length";

    private const string KestrelContentLength = "Kestrel, Content-Length";

    private const string KestrelChunked = "Kestrel, chunked";

    private const string KestrelUnframed = "Kestrel, neither Content-Length nor Transfer-Encoding";

    private static readonly string RecordsPath =
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.EmptyViewId}/records");

    private static readonly string FieldEventPath =
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.CheckCodeViewId}/checkcode/field-event");

    private static readonly string HandlerCategory = typeof(ApiExceptionHandler).FullName!;

    // The test-only endpoint of the exception-boundary cases, and what its failures carry.
    private const string BoundaryTestPath = "/test/boundary";

    private const string PartialBody = "partial";

    private const string SecretSqlText = "SELECT * FROM [dbo].[Secret]";

    // What StoreCancellation records: SqlClient's acknowledgement number, and a rollback that failed on a reset
    // connection.
    private const int StoreCancellationSqlErrorNumber = 0;

    private const string StoreCancellationRollbackType = "SqlException";

    private const int StoreCancellationRollbackNumber = 10054;

    // The event names of the handler category's lines.
    private const string RequestFailedEvent = "RequestFailed";

    private const string RequestAbortedEvent = "RequestAborted";

    private const string ResponseAbortedEvent = "ResponseAborted";

    private const string ErrorBodyWriteFailedEvent = "ErrorBodyWriteFailed";

    // The longest a hosted case waits for its endpoint to start, for the server to finish its request, or for a raw
    // HTTP/1.1 exchange to end.
    private static readonly TimeSpan HostedWait = TimeSpan.FromSeconds(10);

    /// <summary>The hosting environments every HTTP case runs in.</summary>
    public static TheoryData<string> Environments => new() { "Production", "Development" };

    /// <summary>
    /// Each environment with no <c>Accept</c> header (<see langword="null"/>) and with each non-JSON value.
    /// </summary>
    public static TheoryData<string, string?> EnvironmentsAndAcceptHeaders
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (string environment in EnvironmentNames)
            {
                data.Add(environment, null);
                foreach (string accept in NonJsonAcceptHeaders)
                {
                    data.Add(environment, accept);
                }
            }

            return data;
        }
    }

    /// <summary>Each environment with each non-JSON <c>Accept</c> value.</summary>
    public static TheoryData<string, string> EnvironmentsAndNonJsonAcceptHeaders
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string environment in EnvironmentNames)
            {
                foreach (string accept in NonJsonAcceptHeaders)
                {
                    data.Add(environment, accept);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// A method the client fallback does not allow on each of its two routes, the root and a client route, in each
    /// environment, with no <c>Accept</c> header (<see langword="null"/>) and with each non-JSON value.
    /// </summary>
    public static TheoryData<string, string, string, string?> WrongMethodCases
    {
        get
        {
            (string Method, string Path)[] requests = [("POST", "/client/route"), ("PUT", "/")];
            var data = new TheoryData<string, string, string, string?>();
            foreach (string environment in EnvironmentNames)
            {
                foreach ((string method, string path) in requests)
                {
                    data.Add(environment, method, path, null);
                    foreach (string accept in NonJsonAcceptHeaders)
                    {
                        data.Add(environment, method, path, accept);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Paths holding an empty segment, with the client served, in each environment: a missing file after one, the health
    /// path followed by one, and, beside them, a top-level missing file, each under every method, which are the route
    /// miss; and client routes after one, which keep the <c>index.html</c> fallback for GET and its 405 for any other
    /// method.
    /// </summary>
    public static TheoryData<string, string, string, HttpStatusCode> EmptySegmentPathCases
    {
        get
        {
            string[] missPaths = ["/health//", "/client//missing.js", "/missing.js"];
            string[] missMethods = ["POST", "PUT", "GET", "HEAD"];
            (string Method, string Path, HttpStatusCode Expected)[] clientRoutes =
            [
                ("GET", "/client//route", HttpStatusCode.OK),
                ("GET", "/health//x", HttpStatusCode.OK),
                ("POST", "/client//route", HttpStatusCode.MethodNotAllowed),
            ];

            var data = new TheoryData<string, string, string, HttpStatusCode>();
            foreach (string environment in EnvironmentNames)
            {
                foreach (string path in missPaths)
                {
                    foreach (string method in missMethods)
                    {
                        data.Add(environment, method, path, HttpStatusCode.NotFound);
                    }
                }

                foreach ((string method, string path, HttpStatusCode expected) in clientRoutes)
                {
                    data.Add(environment, method, path, expected);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// The generic 500 results, <see cref="ApiProblems.Unexpected"/> (<see langword="null"/> outcome) and
    /// <see cref="ApiProblems.UnexpectedWrite"/> for each outcome, written with and without a current activity.
    /// </summary>
    public static TheoryData<WriteOutcome?, bool> UnexpectedResults
    {
        get
        {
            var data = new TheoryData<WriteOutcome?, bool> { { null, false }, { null, true } };
            foreach (WriteOutcome outcome in Enum.GetValues<WriteOutcome>())
            {
                data.Add(outcome, false);
                data.Add(outcome, true);
            }

            return data;
        }
    }

    /// <summary>
    /// The two bodies built from a status alone, a framework body shaped by <see cref="ApiProblems.Customize"/> and a
    /// status-code page, for 500 and another generic 5xx, with and without a current activity.
    /// </summary>
    public static TheoryData<string, int, bool> StatusOnlyServerErrors
    {
        get
        {
            int[] statuses = [StatusCodes.Status500InternalServerError, StatusCodes.Status502BadGateway];
            var data = new TheoryData<string, int, bool>();
            foreach (string path in new[] { CustomizePath, StatusCodePagePath })
            {
                foreach (int status in statuses)
                {
                    data.Add(path, status, false);
                    data.Add(path, status, true);
                }
            }

            return data;
        }
    }

    /// <summary>Every writer of a 503 body.</summary>
    public static TheoryData<string> UnavailableSources =>
        new() { UnavailableSource, UnavailableWriteSource, CustomizePath, StatusCodePagePath };

    /// <summary>
    /// Client-error statuses a status-code page can answer, including the routing 415 no mapped route produces yet,
    /// with the fixed detail of each; any other 4xx is the generic invalid-request text.
    /// </summary>
    public static TheoryData<int, string> StatusCodePageClientErrors => new()
    {
        { StatusCodes.Status400BadRequest, ExpectedInvalidRequestDetail },
        { StatusCodes.Status404NotFound, ExpectedResourceNotFoundDetail },
        { StatusCodes.Status405MethodNotAllowed, ExpectedMethodNotAllowedDetail },
        { StatusCodes.Status413PayloadTooLarge, ExpectedRequestTooLargeDetail },
        { StatusCodes.Status415UnsupportedMediaType, ExpectedUnsupportedMediaTypeDetail },
        { StatusCodes.Status418ImATeapot, ExpectedInvalidRequestDetail },
    };

    /// <summary>A status the page never answers, and an error status of a request the client aborted.</summary>
    public static TheoryData<int, bool> StatusCodePagesThatWriteNothing => new()
    {
        { StatusCodes.Status200OK, false },
        { StatusCodes.Status404NotFound, true },
    };

    /// <summary>
    /// The two record writes whose success response is serialised after the commit, on TestServer and on Kestrel, in
    /// each environment.
    /// </summary>
    public static TheoryData<string, string, bool> CommittedWrites
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var environment in EnvironmentNames)
            {
                foreach (var route in new[] { CreateRoute, UpdateRoute })
                {
                    data.Add(environment, route, false);
                    data.Add(environment, route, true);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Bodies the four body routes cannot bind, each with the request-level <c>detail</c> it must get, on every
    /// framing, in each environment.
    /// </summary>
    public static TheoryData<string, string, string, string, string> UnusableBodies
    {
        get
        {
            var data = new TheoryData<string, string, string, string, string>();
            foreach (var environment in EnvironmentNames)
            {
                foreach (var route in new[] { CreateRoute, UpdateRoute, SearchRoute, FieldEventRoute })
                {
                    foreach (var framing in new[]
                             {
                                 TestServerContentLength, TestServerUnknownLength, KestrelContentLength, KestrelChunked,
                             })
                    {
                        // Content that is not JSON: an unterminated object and a bare word.
                        data.Add(environment, framing, route, "{", ExpectedMalformedJsonDetail);
                        data.Add(environment, framing, route, "x", ExpectedMalformedJsonDetail);

                        // Content that is not JSON although its first token starts a value that is not an object: an
                        // unterminated array, a trailing comma, and a number or a boolean followed by more content.
                        // The whole body is judged, never its first token alone.
                        data.Add(environment, framing, route, "[", ExpectedMalformedJsonDetail);
                        data.Add(environment, framing, route, "[1,]", ExpectedMalformedJsonDetail);
                        data.Add(environment, framing, route, "5 x", ExpectedMalformedJsonDetail);
                        data.Add(environment, framing, route, "true false", ExpectedMalformedJsonDetail);

                        // Well-formed JSON that is not an object: an array and a number.
                        data.Add(environment, framing, route, "[]", ExpectedRequestBodyMustBeObjectDetail);
                        data.Add(environment, framing, route, "5", ExpectedRequestBodyMustBeObjectDetail);

                        // The JSON literal null is valid JSON that carries no request, answered as a missing body.
                        data.Add(environment, framing, route, "null", ExpectedRequestBodyRequiredDetail);

                        // No content: Content-Length 0, or a body of unknown length whose first read ends it (on
                        // Kestrel the zero-length chunk alone).
                        data.Add(environment, framing, route, string.Empty, ExpectedRequestBodyRequiredDetail);

                        // One byte that holds no JSON token is content, not an empty body.
                        data.Add(environment, framing, route, " ", ExpectedMalformedJsonDetail);

                        // An object member name that cannot be decoded, an escaped lone surrogate, alone and after a
                        // repeated member: no name of the object can be compared until every one is read.
                        data.Add(environment, framing, route, """{"\uD800":1}""", ExpectedMalformedJsonDetail);
                        data.Add(environment, framing, route, """{"a":1,"A":2,"\uDC00":3}""", ExpectedMalformedJsonDetail);
                    }

                    // A missing body: an HTTP/1.1 request without framing headers cannot carry one.
                    data.Add(environment, KestrelUnframed, route, string.Empty, ExpectedRequestBodyRequiredDetail);
                }
            }

            return data;
        }
    }

    // The placeholder a body template holds where its case puts a fresh sentinel.
    private const string SentinelToken = "%SENTINEL%";

    // The framings of the member cases: TestServer, and exact bytes on real Kestrel.
    private static readonly string[] MemberCaseFramings = [TestServerContentLength, KestrelContentLength];

    /// <summary>
    /// Body templates that name a member more than once, exactly or differing only by letter case: a member of the body
    /// object on each body route, a known member or an unknown one, and a member of a search criterion object. Each
    /// runs on every member-case framing, in each environment.
    /// </summary>
    public static TheoryData<string, string, string, string> RepeatedMemberBodies
    {
        get
        {
            (string Route, string Body)[] bodies =
            [
                (CreateRoute, """{"values":{"LastName":"%SENTINEL%"},"values":{"LastName":"second"}}"""),
                (CreateRoute, """{"values":{"LastName":"%SENTINEL%"},"VALUES":{"LastName":"second"}}"""),
                (CreateRoute, """{"values":{"LastName":"first"},"note":"%SENTINEL%","Note":1}"""),
                (UpdateRoute, """{"values":{"RecStatus":0,"LastName":"%SENTINEL%"},"values":{"LastName":"second"}}"""),
                (UpdateRoute, """{"Values":{"LastName":"%SENTINEL%"},"values":{"LastName":"second"}}"""),
                (SearchRoute, """{"criteria":[{"field":"LastName","value":"%SENTINEL%"}],"criteria":[{"field":"LastName","value":"b"}]}"""),
                (SearchRoute, """{"criteria":[{"field":"LastName","value":"a"}],"includeDeleted":"%SENTINEL%","IncludeDeleted":false}"""),
                (SearchRoute, """{"criteria":[{"field":"LastName","field":"Age","value":"%SENTINEL%"}]}"""),
                (SearchRoute, """{"criteria":[{"field":"Age","value":1},{"field":"LastName","value":"%SENTINEL%","VALUE":"b"}]}"""),
                (FieldEventRoute, """{"event":"FieldAfter","event":"FieldBefore","fieldName":"LastName","values":{"LastName":"%SENTINEL%"}}"""),
                (FieldEventRoute, """{"event":"FieldAfter","fieldName":"LastName","fieldName":"Age","values":{"LastName":"%SENTINEL%"}}"""),
                (FieldEventRoute, """{"event":"FieldAfter","fieldName":"LastName","values":{"LastName":"%SENTINEL%"},"Values":{}}"""),
            ];

            var data = new TheoryData<string, string, string, string>();
            foreach (var environment in EnvironmentNames)
            {
                foreach (var framing in MemberCaseFramings)
                {
                    foreach (var (route, body) in bodies)
                    {
                        data.Add(environment, framing, route, body);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Well-formed search body templates with one member of the wrong JSON kind, each with the <c>errors</c> key and
    /// message it must get, on every member-case framing, in each environment.
    /// </summary>
    public static TheoryData<string, string, string, string, string> WronglyTypedSearchBodies
    {
        get
        {
            (string Body, string Key, string Message)[] bodies =
            [
                (
                    """{"criteria":[{"field":"RecStatus","value":"1"}],"includeDeleted":"true"}""",
                    IncludeDeletedKey,
                    ExpectedIncludeDeletedMessage),
                (
                    """{"criteria":[{"field":"RecStatus","value":"1"}],"includeDeleted":1}""",
                    IncludeDeletedKey,
                    ExpectedIncludeDeletedMessage),
                (
                    """{"criteria":[{"field":"RecStatus","value":"1"}],"includeDeleted":"' OR 1=1-- %SENTINEL%"}""",
                    IncludeDeletedKey,
                    ExpectedIncludeDeletedMessage),
                (
                    """{"criteria":[{"field":"LastName","value":"x"}],"excludeGlobalRecordId":1}""",
                    ExcludeGlobalRecordIdKey,
                    ExpectedExcludeGlobalRecordIdMessage),
                (
                    """{"criteria":[{"field":"LastName","value":"x"}],"excludeGlobalRecordId":["%SENTINEL%"]}""",
                    ExcludeGlobalRecordIdKey,
                    ExpectedExcludeGlobalRecordIdMessage),
                (
                    """{"criteria":{"field":"LastName","value":"%SENTINEL%"}}""",
                    CriteriaKey,
                    ExpectedCriteriaNotArrayMessage),
                (
                    """{"criteria":[{"field":5,"value":"%SENTINEL%"}]}""",
                    CriteriaKey,
                    ExpectedFieldNotStringMessage),
                (
                    """{"criteria":[{"field":"LastName","value":{"%SENTINEL%":1}}]}""",
                    CriteriaKey,
                    ExpectedValueNotScalarMessage),
                (
                    """{"criteria":[{"field":"Age","value":1e400}]}""",
                    CriteriaKey,
                    ExpectedValueNotFiniteMessage),
                (
                    """{"criteria":[{"field":"LastName"}]}""",
                    CriteriaKey,
                    ExpectedValueMissingMessage),

                // Past six items the problems of the criteria are one fixed message, however many items have one.
                (
                    """{"criteria":[{"field":"LastName","value":"a"},null,null,null,null,null,{"field":5,"value":"%SENTINEL%"}]}""",
                    CriteriaKey,
                    ExpectedOverLongCriteriaMessage),
                (
                    "{\"criteria\":["
                        + string.Join(',', Enumerable.Repeat("""{"field":"LastName","value":["%SENTINEL%"]}""", 2_000))
                        + "]}",
                    CriteriaKey,
                    ExpectedOverLongCriteriaMessage),
            ];

            var data = new TheoryData<string, string, string, string, string>();
            foreach (var environment in EnvironmentNames)
            {
                foreach (var framing in MemberCaseFramings)
                {
                    foreach (var (body, key, message) in bodies)
                    {
                        data.Add(environment, framing, body, key, message);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Body templates whose <c>values</c> object names one field twice, differing only by letter case, on the three
    /// routes that take <c>values</c>, on every member-case framing, in each environment.
    /// </summary>
    public static TheoryData<string, string, string, string> RepeatedValuesKeyBodies
    {
        get
        {
            (string Route, string Body)[] bodies =
            [
                (CreateRoute, """{"values":{"LastName":"%SENTINEL%","LASTNAME":"second"}}"""),
                (UpdateRoute, """{"values":{"lastName":"%SENTINEL%","LastName":"second"}}"""),
                (FieldEventRoute, """{"event":"FieldAfter","fieldName":"LastName","values":{"LastName":"%SENTINEL%","lastname":"b"}}"""),
            ];

            var data = new TheoryData<string, string, string, string>();
            foreach (var environment in EnvironmentNames)
            {
                foreach (var framing in MemberCaseFramings)
                {
                    foreach (var (route, body) in bodies)
                    {
                        data.Add(environment, framing, route, body);
                    }
                }
            }

            return data;
        }
    }

    // TheoryData implements more than one IEnumerable<T>, so LINQ is pointed at its object[] rows.
    private static IEnumerable<string> EnvironmentNames =>
        ((IEnumerable<object[]>)Environments).Select(row => (string)row[0]);

    // AAP 0.3.4.2: a route miss is a 404 ProblemDetails whatever the Accept header; the framework's default writer
    // declines a non-JSON Accept and would leave text/plain without traceId. The client is deliberately absent
    // (AbsentClientFolder).
    [Theory]
    [MemberData(nameof(EnvironmentsAndAcceptHeaders))]
    public async Task RouteMissIsProblemDetailsWhateverTheAcceptHeader(string environment, string? accept)
    {
        await using ApiFactory factory = CreateFactory(environment, AbsentClientFolder());
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nope");
        AddAccept(request, accept);

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertFixedProblemAsync(
            response,
            HttpStatusCode.NotFound,
            NotFoundType,
            "Not Found",
            ExpectedResourceNotFoundDetail);
    }

    // AAP 0.7.6: a text/plain POST to an unknown /api route is the route miss, never a 415, and with a non-JSON Accept
    // it is still the 404 ProblemDetails. The client is deliberately absent (AbsentClientFolder).
    [Theory]
    [MemberData(nameof(EnvironmentsAndNonJsonAcceptHeaders))]
    public async Task TextPlainPostToUnknownApiRouteIsNotFound(string environment, string accept)
    {
        await using ApiFactory factory = CreateFactory(environment, AbsentClientFolder());
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nope")
        {
            Content = new StringContent("plain text, not JSON", Encoding.UTF8, "text/plain"),
        };
        AddAccept(request, accept);

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertFixedProblemAsync(
            response,
            HttpStatusCode.NotFound,
            NotFoundType,
            "Not Found",
            ExpectedResourceNotFoundDetail);
    }

    // AAP 0.3.4.2 and 0.3.4.7: a method mismatch stays a 405, becomes the 405 ProblemDetails whatever the Accept
    // header, and keeps the Allow header routing set. The client fallback (GET and HEAD) is the route used, mapped from
    // a temporary client folder.
    [Theory]
    [MemberData(nameof(WrongMethodCases))]
    public async Task WrongMethodIsProblemDetailsAndKeepsAllow(
        string environment,
        string method,
        string path,
        string? accept)
    {
        DirectoryInfo clientFolder = Directory.CreateTempSubdirectory("epiinfo-error-contract-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(clientFolder.FullName, ClientAppHosting.IndexFileName),
                "<!doctype html><title>client</title>");

            await using ApiFactory factory = CreateFactory(environment, clientFolder.FullName);
            using HttpClient client = factory.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            AddAccept(request, accept);

            using HttpResponseMessage response = await client.SendAsync(request);

            await AssertFixedProblemAsync(
                response,
                HttpStatusCode.MethodNotAllowed,
                MethodNotAllowedType,
                "Method Not Allowed",
                ExpectedMethodNotAllowedDetail);

            ICollection<string> allow = response.Content.Headers.Allow;
            Assert.True(
                allow.Contains(HttpMethods.Get) && allow.Contains(HttpMethods.Head),
                "The 405 lost the Allow header listing GET and HEAD.");
        }
        finally
        {
            clientFolder.Delete(recursive: true);
        }
    }

    // AAP 0.3.4.7 and 0.3.4.2: with the client served, routing captures an empty segment's route values as it always
    // does, and the client route's rule decides them before the method check. A missing file after an empty segment, or
    // the health path followed by one, is the route miss 404 whatever the method, never a 405 that borrows the
    // fallback's GET and HEAD; a client route after one keeps index.html for GET and the 405 listing GET and HEAD for
    // any other method.
    [Theory]
    [MemberData(nameof(EmptySegmentPathCases))]
    public async Task EmptySegmentPathsKeepTheRouteMissAndTheClientFallback(
        string environment,
        string method,
        string path,
        HttpStatusCode expected)
    {
        const string IndexDocument = "<!doctype html><title>client</title>";
        DirectoryInfo clientFolder = Directory.CreateTempSubdirectory("epiinfo-error-contract-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(clientFolder.FullName, ClientAppHosting.IndexFileName),
                IndexDocument);

            await using ApiFactory factory = CreateFactory(environment, clientFolder.FullName);
            using HttpClient client = factory.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), path);

            using HttpResponseMessage response = await client.SendAsync(request);

            switch (expected)
            {
                case HttpStatusCode.OK:
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
                    string document = await response.Content.ReadAsStringAsync();
                    Assert.True(
                        string.Equals(IndexDocument, document, StringComparison.Ordinal),
                        "The client route did not answer with the client's index.html.");
                    break;

                case HttpStatusCode.MethodNotAllowed:
                    await AssertFixedProblemAsync(
                        response,
                        HttpStatusCode.MethodNotAllowed,
                        MethodNotAllowedType,
                        "Method Not Allowed",
                        ExpectedMethodNotAllowedDetail);
                    Assert.Equal(
                        new[] { HttpMethods.Get, HttpMethods.Head },
                        response.Content.Headers.Allow.Order(StringComparer.Ordinal));
                    break;

                // A HEAD response carries no body to read.
                case HttpStatusCode.NotFound when method == HttpMethods.Head:
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    break;

                case HttpStatusCode.NotFound:
                    await AssertFixedProblemAsync(
                        response,
                        HttpStatusCode.NotFound,
                        NotFoundType,
                        "Not Found",
                        ExpectedResourceNotFoundDetail);
                    break;

                default:
                    Assert.Fail("The case expects a status this test does not check.");
                    break;
            }
        }
        finally
        {
            clientFolder.Delete(recursive: true);
        }
    }

    // AAP 0.3.4.2: the 500 detail is the fixed generic sentence plus the traceId the same body carries, which is the
    // current activity's id when there is one and the request's trace identifier otherwise.
    [Theory]
    [MemberData(nameof(UnexpectedResults))]
    public async Task UnexpectedResultsQuoteTheirTraceIdInDetail(WriteOutcome? outcome, bool withActivity)
    {
        using Activity? activity = StartActivity(withActivity);
        IResult result = outcome is { } written ? ApiProblems.UnexpectedWrite(written) : ApiProblems.Unexpected();
        (DefaultHttpContext context, MemoryStream body) = NewContext();

        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(ProblemMediaType, context.Response.ContentType);
        JsonElement problem = ApiTestClient.AssertProblemDetails(Parse(body), StatusCodes.Status500InternalServerError);
        string? traceId = StringMember(problem, TraceIdMember);
        AssertTraceId(traceId, activity, context);
        AssertUnexpectedDetail(StringMember(problem, "detail"), traceId);

        if (outcome is { } expected)
        {
            Assert.True(
                string.Equals(
                    ExpectedWireOutcome(expected),
                    StringMember(problem, "writeOutcome"),
                    StringComparison.Ordinal),
                "The write 500 does not carry its writeOutcome.");
        }
        else
        {
            Assert.False(problem.TryGetProperty("writeOutcome", out _), "A non-write 500 carries writeOutcome.");
        }
    }

    // A result written again rebuilds its detail from the constant and the trace id of the request it is written to,
    // never keeping the first request's trace id.
    [Fact]
    public async Task UnexpectedResultWrittenTwiceQuotesEachRequestsTraceId()
    {
        Activity.Current = null;
        IResult result = ApiProblems.Unexpected();
        string[] traceIds = ["first-request", "second-request"];

        foreach (string traceId in traceIds)
        {
            (DefaultHttpContext context, MemoryStream body) = NewContext();
            context.TraceIdentifier = traceId;

            await result.ExecuteAsync(context);

            JsonElement problem = Parse(body);
            Assert.True(
                string.Equals(traceId, StringMember(problem, TraceIdMember), StringComparison.Ordinal),
                "The traceId member is not the trace identifier of the request written to.");
            AssertUnexpectedDetail(StringMember(problem, "detail"), traceId);
        }
    }

    // AAP 0.3.4.2: the framework and status-code page paths give a generic 5xx the same correlated detail as the
    // factories.
    [Theory]
    [MemberData(nameof(StatusOnlyServerErrors))]
    public async Task StatusOnlyServerErrorsQuoteTheirTraceIdInDetail(string path, int status, bool withActivity)
    {
        using Activity? activity = StartActivity(withActivity);
        (DefaultHttpContext context, MemoryStream body) = NewContext();

        (string? detail, string? traceId) = await ProduceStatusOnlyProblemAsync(path, status, context, body);

        AssertTraceId(traceId, activity, context);
        AssertUnexpectedDetail(detail, traceId);
    }

    // AAP 0.3.4.2: a 503 keeps its fixed unavailable text; only the generic 500 detail carries the trace id.
    [Theory]
    [MemberData(nameof(UnavailableSources))]
    public async Task UnavailableBodiesKeepTheFixedDetail(string source)
    {
        Activity.Current = null;
        (DefaultHttpContext context, MemoryStream body) = NewContext();

        string? detail;
        string? traceId;
        switch (source)
        {
            case UnavailableSource:
            case UnavailableWriteSource:
                IResult result = source == UnavailableSource
                    ? ApiProblems.Unavailable()
                    : ApiProblems.UnavailableWrite(WriteOutcome.NotWritten);
                await result.ExecuteAsync(context);
                Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
                JsonElement problem = ApiTestClient.AssertProblemDetails(
                    Parse(body),
                    StatusCodes.Status503ServiceUnavailable);
                detail = StringMember(problem, "detail");
                traceId = StringMember(problem, TraceIdMember);
                break;

            default:
                (detail, traceId) = await ProduceStatusOnlyProblemAsync(
                    source,
                    StatusCodes.Status503ServiceUnavailable,
                    context,
                    body);
                break;
        }

        AssertTraceId(traceId, activity: null, context);
        Assert.True(
            string.Equals(ExpectedUnavailableDetail, detail, StringComparison.Ordinal),
            "The 503 detail is not the fixed unavailable text.");
    }

    // AAP 0.3.4.2: a status-code page is the fixed ProblemDetails of its status, as application/problem+json, and
    // leaves the headers routing set, such as a 405's Allow, as they were.
    [Theory]
    [MemberData(nameof(StatusCodePageClientErrors))]
    public async Task StatusCodePagesWriteTheFixedTextOfTheirStatus(int status, string detail)
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        context.Response.StatusCode = status;
        context.Response.Headers.Allow = "GET, HEAD";

        await ApiProblems.WriteStatusCodePageAsync(
            new StatusCodeContext(context, new StatusCodePagesOptions(), _ => Task.CompletedTask));

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(ProblemMediaType, context.Response.ContentType);
        Assert.True(
            string.Equals("GET, HEAD", context.Response.Headers.Allow, StringComparison.Ordinal),
            "The status-code page changed a header it does not own.");
        JsonElement problem = ApiTestClient.AssertProblemDetails(Parse(body), status);
        Assert.True(
            ProblemMembers.SequenceEqual(
                problem.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal),
                StringComparer.Ordinal),
            "The status-code page does not hold exactly type, title, status, detail and traceId.");
        Assert.True(
            string.Equals(detail, StringMember(problem, "detail"), StringComparison.Ordinal),
            "The status-code page's detail is not the fixed text of its status.");
        AssertTraceId(StringMember(problem, TraceIdMember), Activity.Current, context);
    }

    // A status outside 400 to 599 gets no page, and a client that aborted the request receives no body.
    [Theory]
    [MemberData(nameof(StatusCodePagesThatWriteNothing))]
    public async Task StatusCodePageWritesNothingOutsideAnErrorOrAfterAbort(int status, bool aborted)
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        using var abort = new CancellationTokenSource();
        if (aborted)
        {
            await abort.CancelAsync();
        }

        context.RequestAborted = abort.Token;
        context.Response.StatusCode = status;

        await ApiProblems.WriteStatusCodePageAsync(
            new StatusCodeContext(context, new StatusCodePagesOptions(), _ => Task.CompletedTask));

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Null(context.Response.ContentType);
        Assert.Equal(0, body.Length);
    }

    // AAP 0.3.4.2 and 0.3.4.6: once the store has committed, a failure while the response DTO is serialised, before
    // any header is sent, is the unexpected 500 whose writeOutcome is "written", so the client never resends a save
    // that was applied. It is never the 400 of a malformed request: binding wraps every request-body JSON failure in
    // a BadHttpRequestException, so a bare JsonException comes from server-side work such as this.
    [Theory]
    [MemberData(nameof(CommittedWrites))]
    public async Task ResponseSerializationFailureAfterCommitIsUnexpectedAndWritten(
        string environment,
        string route,
        bool useKestrel)
    {
        var sentinel = NewSentinel();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            UseKestrel = useKestrel,
            ConfigureJson = json => json.Converters.Insert(0, new FailingWriteConverter<RecordDto>(sentinel)),
        });
        var store = host.Store;
        var seeded = route == UpdateRoute
            ? store.SeedRecord(
                FakeForms.EmptyViewId,
                new Dictionary<string, FieldValue> { ["LastName"] = FieldValue.FromText("Before") })
            : null;
        var body = new { values = new Dictionary<string, object?> { ["LastName"] = "After" } };
        using var client = host.CreateClient();

        using var response = seeded is null
            ? await ApiTestClient.PostJsonAsync(client, RecordsPath, body)
            : await ApiTestClient.PutJsonAsync(client, $"{RecordsPath}/{seeded}", body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(
            document.RootElement,
            (int)HttpStatusCode.InternalServerError);
        Assert.Contains(ExpectedUnexpectedSentence, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(ExpectedWritten, problem.GetProperty("writeOutcome").GetString());
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);

        // The save was applied before its response failed.
        var record = Assert.Single(store.GetRecords(FakeForms.EmptyViewId));
        Assert.Equal("After", record.Values["LastName"].AsText());
        var write = Assert.Single(store.WriteCalls);
        if (seeded is null)
        {
            Assert.Equal(nameof(IDataStore.InsertAsync), write.Method);
        }
        else
        {
            Assert.Equal(seeded, record.GlobalRecordId);
            Assert.Equal(nameof(IDataStore.UpdateAsync), write.Method);
        }

        var entry = Assert.Single(host.Logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal((int)HttpStatusCode.InternalServerError, StateValue(entry, "StatusCode"));
        Assert.Equal(typeof(JsonException).FullName, StateValue(entry, "ExceptionType"));
        Assert.Equal(ExpectedWritten, StateValue(entry, "WriteOutcome"));
        Assert.False(host.Logs.ContainsText(sentinel));
    }

    // Outside a record write the same bare JsonException is the plain unexpected 500: no writeOutcome member, which
    // only the record writes document.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task ResponseSerializationFailureOnAReadIsAPlainUnexpectedError(string environment)
    {
        var sentinel = NewSentinel();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            ConfigureJson = json =>
                json.Converters.Insert(0, new FailingWriteConverter<RecordCountResponse>(sentinel)),
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{RecordsPath}/count");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(
            document.RootElement,
            (int)HttpStatusCode.InternalServerError);
        Assert.Contains(ExpectedUnexpectedSentence, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(problem.TryGetProperty("writeOutcome", out _), "The 500 of a read carries a writeOutcome.");
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        Assert.Empty(host.Store.WriteCalls);

        var entry = Assert.Single(host.Logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(typeof(JsonException).FullName, StateValue(entry, "ExceptionType"));
        Assert.Null(StateValue(entry, "WriteOutcome"));
        Assert.False(host.Logs.ContainsText(sentinel));
    }

    // AAP 0.3.4.2 and 0.3.4.7: on each of the four body routes a body that cannot be bound is a request-level 400
    // whose detail names the problem, with no errors map, no writeOutcome and nothing written. The API itself answers,
    // composed by Program.cs over the fake store: on TestServer through ApiFactory, and on real Kestrel through
    // KestrelApiFactory, whose listener the guard checked and froze before it bound.
    [Theory]
    [MemberData(nameof(UnusableBodies))]
    public async Task UnusableBodyIsARequestLevel400NamingTheProblem(
        string environment,
        string framing,
        string route,
        string body,
        string expectedDetail)
    {
        var store = FakeDataStore.WithStandardForms();
        var options = ApiFactoryOptions.ForFake(store, environment) with
        {
            ExtraSettings = ClientPathSetting(AbsentClientFolder()),
        };
        await using ApiFactoryBase factory = framing is KestrelContentLength or KestrelChunked or KestrelUnframed
            ? new KestrelApiFactory(options)
            : new ApiFactory(options);
        var (method, path) = ResolveRoute(store, route);

        var (status, mediaType, text) = await SendBodyAsync(factory, method, path, framing, body);

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Equal(ProblemMediaType, mediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(document.RootElement, (int)HttpStatusCode.BadRequest);
        Assert.Equal(expectedDetail, problem.GetProperty("detail").GetString());
        Assert.False(problem.TryGetProperty("errors", out _), "The request-level 400 carries an errors map.");
        Assert.False(problem.TryGetProperty("writeOutcome", out _), "The request-level 400 carries a writeOutcome.");
        Assert.Empty(store.WriteCalls);
    }

    // A body that names a member more than once is ambiguous, since binding matches member names ignoring case and
    // would let the last occurrence replace the first unseen. On each body route, for a member of the body object and
    // for a member of a search criterion, it is the request-level 400 naming the repeat, with no errors map and no
    // writeOutcome, answered before any store call, so nothing is read or written. Neither the body nor a log line
    // quotes the sentinel the first occurrence carries.
    [Theory]
    [MemberData(nameof(RepeatedMemberBodies))]
    public async Task RepeatedMemberIsARequestLevel400(
        string environment,
        string framing,
        string route,
        string template)
    {
        var sentinel = NewSentinel();
        var store = FakeDataStore.WithStandardForms();
        await using ApiFactoryBase factory = CreateMemberCaseFactory(store, environment, framing);
        var (method, path) = ResolveRoute(store, route);

        var (status, mediaType, text) = await SendBodyAsync(
            factory,
            method,
            path,
            framing,
            template.Replace(SentinelToken, sentinel, StringComparison.Ordinal));

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Equal(ProblemMediaType, mediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(document.RootElement, (int)HttpStatusCode.BadRequest);
        Assert.Equal(ExpectedRequestBodyRepeatsMemberDetail, problem.GetProperty("detail").GetString());
        Assert.False(problem.TryGetProperty("errors", out _), "The request-level 400 carries an errors map.");
        Assert.False(problem.TryGetProperty("writeOutcome", out _), "The request-level 400 carries a writeOutcome.");
        Assert.False(text.Contains(sentinel, StringComparison.Ordinal), "The 400 body quotes the request body.");
        Assert.False(factory.Logs.ContainsText(sentinel), "A log line quotes the request body.");
        Assert.Empty(store.Calls);
    }

    // A well-formed search body whose member has the wrong JSON kind is not malformed JSON. It is the field-level 400
    // naming the problem under the member's wire name, with the fixed message alone and no writeOutcome, answered
    // before any store call, so the form is not looked up and nothing is searched or written. Neither the body nor a
    // log line quotes the sentinel the wrongly typed member carries.
    [Theory]
    [MemberData(nameof(WronglyTypedSearchBodies))]
    public async Task WronglyTypedSearchMemberIsAFieldLevel400NamingIt(
        string environment,
        string framing,
        string template,
        string key,
        string message)
    {
        var sentinel = NewSentinel();
        var store = FakeDataStore.WithStandardForms();
        await using ApiFactoryBase factory = CreateMemberCaseFactory(store, environment, framing);
        var (method, path) = ResolveRoute(store, SearchRoute);

        var (status, mediaType, text) = await SendBodyAsync(
            factory,
            method,
            path,
            framing,
            template.Replace(SentinelToken, sentinel, StringComparison.Ordinal));

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Equal(ProblemMediaType, mediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(document.RootElement, (int)HttpStatusCode.BadRequest);
        Assert.Equal(ExpectedValidationFailedDetail, problem.GetProperty("detail").GetString());
        Assert.Equal(new[] { message }, AssertErrorMessages(problem, key));
        Assert.Equal(new[] { key }, problem.GetProperty("errors").EnumerateObject().Select(entry => entry.Name));
        Assert.False(problem.TryGetProperty("writeOutcome", out _), "The field-level 400 carries a writeOutcome.");
        Assert.False(text.Contains(sentinel, StringComparison.Ordinal), "The 400 body quotes the request body.");
        Assert.False(factory.Logs.ContainsText(sentinel), "A log line quotes the request body.");
        Assert.Empty(store.Calls);
    }

    // The keys of a values object are field names, not members of the body, so a field named twice there, differing
    // only by letter case, keeps the field-level 400 under its canonical name with the fixed duplicate-field message,
    // on each route that takes values, and nothing is written.
    [Theory]
    [MemberData(nameof(RepeatedValuesKeyBodies))]
    public async Task RepeatedValuesKeyKeepsTheFieldLevelDuplicateError(
        string environment,
        string framing,
        string route,
        string template)
    {
        var sentinel = NewSentinel();
        var store = FakeDataStore.WithStandardForms();
        await using ApiFactoryBase factory = CreateMemberCaseFactory(store, environment, framing);
        var (method, path) = ResolveRoute(store, route);

        var (status, mediaType, text) = await SendBodyAsync(
            factory,
            method,
            path,
            framing,
            template.Replace(SentinelToken, sentinel, StringComparison.Ordinal));

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.Equal(ProblemMediaType, mediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(document.RootElement, (int)HttpStatusCode.BadRequest);
        Assert.Equal(ExpectedValidationFailedDetail, problem.GetProperty("detail").GetString());
        Assert.Equal(new[] { ExpectedDuplicateFieldMessage }, AssertErrorMessages(problem, "LastName"));
        Assert.Equal(new[] { "LastName" }, problem.GetProperty("errors").EnumerateObject().Select(entry => entry.Name));
        Assert.False(text.Contains(sentinel, StringComparison.Ordinal), "The 400 body quotes the request body.");
        Assert.False(factory.Logs.ContainsText(sentinel), "A log line quotes the request body.");
        Assert.Empty(store.WriteCalls);
    }

    // AAP 0.3.4.6: a caller that aborts its request gets no response and leaves one Information line. The
    // exception-handler middleware would settle the cancellation itself, at a level the committed filter drops, so the
    // API's exception boundary is what answers and logs it.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task CallerAbortedRequestWritesNoResponseAndLogsOneInformationLine(string environment)
    {
        var started = NewSignal();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(BoundaryTestPath, async context =>
            {
                SignalWhenFinished(context, finished);
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }),
        });
        using var client = host.CreateClient();
        using var cancellation = new CancellationTokenSource();

        var request = client.GetAsync(BoundaryTestPath, cancellation.Token);
        await WaitForAsync(started.Task, "the endpoint to start");
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await WaitForAsync(finished.Task, "the server to finish the aborted request");
        AssertOneClientAbortLine(host.Logs);
    }

    // The boundary alone, for either exception a client abort raises: a cancelled RequestAborted means status 499, no
    // body byte, one Information line, and no trace of the exception's message.
    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(IOException))]
    public async Task BoundaryAnswersCallerAbortWithNoBodyAndOneInformationLine(Type exceptionType)
    {
        var sentinel = NewSentinel();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Response.Body = body;
        var failure = (Exception)Activator.CreateInstance(exceptionType, sentinel)!;
        var boundary = new ApiExceptionBoundaryMiddleware(
            _ => Task.FromException(failure),
            loggerFactory.CreateLogger<ApiExceptionHandler>());

        await boundary.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, body.Length);
        AssertOneClientAbortLine(logs);
        Assert.False(logs.ContainsText(sentinel));
    }

    // AAP 0.3.4.2, handler log line: a client abort's one line carries the SQL error number the store kept on its
    // cancellation, 0 for SqlClient's acknowledgement of a cancelled command, and a failed rollback's type and number,
    // whether the boundary or the handler answers it. A plain abort logs none of them, and no line holds the message.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ClientAbortLineCarriesTheStoreNumbersOfItsCancellation(bool throughBoundary, bool fromStore)
    {
        var sentinel = NewSentinel();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var logger = loggerFactory.CreateLogger<ApiExceptionHandler>();
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Response.Body = body;
        var failure = fromStore
            ? StoreCancellation(sentinel)
            : new OperationCanceledException(sentinel + " " + SecretSqlText);

        if (throughBoundary)
        {
            await new ApiExceptionBoundaryMiddleware(_ => Task.FromException(failure), logger).InvokeAsync(context);
        }
        else
        {
            Assert.True(await new ApiExceptionHandler(logger).TryHandleAsync(context, failure, CancellationToken.None));
        }

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, body.Length);
        AssertOneClientAbortLine(logs);
        AssertStoreNumbers(Assert.Single(logs.Find(captured => captured.Category == HandlerCategory)), fromStore);
        Assert.False(logs.ContainsText(sentinel));
        Assert.False(logs.ContainsText(SecretSqlText));
    }

    // AAP 0.3.4.2: a cancellation the API raises itself while the caller is still connected is not an abort. It is the
    // fixed unexpected 500, logged once at Error with its type, and its message reaches neither the body nor a log.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task InternalCancellationOnAnActiveRequestIsTheFixedUnexpectedError(string environment)
    {
        var sentinel = NewSentinel();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(
                BoundaryTestPath,
                _ => throw new OperationCanceledException(sentinel)),
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync(BoundaryTestPath);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(
            document.RootElement,
            (int)HttpStatusCode.InternalServerError);
        Assert.Contains(ExpectedUnexpectedSentence, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);

        var entry = Assert.Single(host.Logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(RequestFailedEvent, entry.EventId.Name);
        Assert.Null(entry.ExceptionType);
        Assert.Equal(typeof(OperationCanceledException).FullName, StateValue(entry, "ExceptionType"));
        Assert.Empty(host.Logs.Find(IsFrameworkExceptionLine));
        Assert.False(host.Logs.ContainsText(sentinel));
    }

    // The same internal cancellation as the store raises it, carrying its classified SQL error number and a failed
    // rollback's type and number: the one Error line logs them, and its message stays out of every line.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task InternalStoreCancellationLogsItsStoreNumbersOnTheFailureLine(string environment)
    {
        var sentinel = NewSentinel();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(
                BoundaryTestPath,
                _ => throw StoreCancellation(sentinel)),
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync(BoundaryTestPath);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        var entry = Assert.Single(host.Logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(RequestFailedEvent, entry.EventId.Name);
        Assert.Null(entry.ExceptionType);
        AssertStoreNumbers(entry, expected: true);
        Assert.Empty(host.Logs.Find(IsFrameworkExceptionLine));
        Assert.False(host.Logs.ContainsText(sentinel));
        Assert.False(host.Logs.ContainsText(SecretSqlText));
    }

    // Only a cancelled RequestAborted makes a cancellation an abort: otherwise the boundary lets the same exception
    // through, untouched and unlogged, to the exception handler and its 500.
    [Fact]
    public async Task BoundaryRethrowsCancellationOnAnActiveRequestUnchanged()
    {
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var context = new DefaultHttpContext();
        var failure = new OperationCanceledException(NewSentinel());
        var boundary = new ApiExceptionBoundaryMiddleware(
            _ => Task.FromException(failure),
            loggerFactory.CreateLogger<ApiExceptionHandler>());

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => boundary.InvokeAsync(context));

        Assert.Same(failure, thrown);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(logs.Entries);
    }

    // AAP 0.3.4.2 and 0.9.3: a log sink that throws for the handler's line changes nothing the client receives. The
    // failure keeps its exact redacted ProblemDetails, its one line still reaches the working sinks, and neither the
    // exception-handler middleware nor the server logs the original exception, whose text holds the sentinel.
    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task ThrowingLogSinkLeavesTheRedactedErrorResponseUnchanged(
        string environment,
        string endpoint,
        string kind)
    {
        string sentinel = NewSentinel();
        string sqlText = FakeDataStore.SqlTextException(sentinel).Message;
        FakeDataStore store = FakeDataStore.WithStandardForms();
        bool unavailable = kind == UnavailableFailure;

        // Both failures carry the SQL text, the unavailable one as the driver exception a store attaches as its inner
        // exception, so a framework line about the original exception would hold it.
        Exception failure = unavailable
            ? new DataStoreUnavailableException(
                ReadStoreMethod(endpoint),
                WriteOutcome.NotWritten,
                sqlErrorNumber: -2,
                innerException: FakeDataStore.SqlTextException(sentinel))
            : FakeDataStore.SqlTextException(sentinel);
        store.ThrowOn(ReadStoreMethod(endpoint), failure);
        using var throwing = new ThrowingLoggerProvider(typeof(ApiExceptionHandler));
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(
                services => services.AddSingleton<ILoggerProvider>(throwing)))
            .CreateClient();

        using HttpResponseMessage response = await SendReadAsync(client, endpoint);
        string body = await response.Content.ReadAsStringAsync();

        HttpStatusCode status = unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError;
        JsonElement problem = AssertProblemDetails(response, body, status);
        Assert.True(
            ProblemMembers.SequenceEqual(
                problem.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal),
                StringComparer.Ordinal),
            "The error body does not hold exactly type, title, status, detail and traceId.");
        string? detail = StringMember(problem, "detail");
        if (unavailable)
        {
            Assert.Equal(ApiProblems.UnavailableDetail, detail);
        }
        else
        {
            AssertUnexpectedDetail(detail, StringMember(problem, ApiProblems.TraceIdKey));
        }

        Assert.Equal(1, throwing.Throws);
        CapturedLogEntry line = AssertOneHandlerLine(factory, status);
        Assert.Equal(RequestFailedEvent, line.EventId.Name);
        Assert.Equal(StringMember(problem, ApiProblems.TraceIdKey), StateValue(line, "TraceId"));
        Assert.Empty(factory.Logs.Find(IsFrameworkExceptionLine));
        AssertRedacted(factory, body, sentinel, sqlText);
    }

    /// <summary>The cases of <see cref="ThrowingLogSinkNeverStopsTheHandlersAnswer"/>.</summary>
    public static TheoryData<bool, int, string[]> HandlerAnswersUnderAThrowingLogSink => new()
    {
        // A client abort: no body, and the status the exception-handler middleware gives an aborted request.
        { true, StatusCodes.Status499ClientClosedRequest, [RequestAbortedEvent] },

        // A failure whose error body cannot be written: the handler still answers it, with both of its lines.
        { false, StatusCodes.Status500InternalServerError, [RequestFailedEvent, ErrorBodyWriteFailedEvent] },
    };

    // The handler alone, with a log sink that throws for its category: it still answers, never throws, and each of its
    // lines still reaches the working sink.
    [Theory]
    [MemberData(nameof(HandlerAnswersUnderAThrowingLogSink))]
    public async Task ThrowingLogSinkNeverStopsTheHandlersAnswer(bool clientAbort, int status, string[] events)
    {
        using var logs = new CapturingLoggerProvider();
        using var throwing = new ThrowingLoggerProvider(typeof(ApiExceptionHandler));
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs).AddProvider(throwing));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var aborted = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestServices = services };
        Exception failure;
        if (clientAbort)
        {
            await aborted.CancelAsync();
            context.RequestAborted = aborted.Token;
            context.Response.Body = new MemoryStream();
            failure = new OperationCanceledException(NewSentinel());
        }
        else
        {
            context.Response.Body = new FailingWriteStream();
            failure = new InvalidOperationException(NewSentinel());
        }

        var handler = new ApiExceptionHandler(loggerFactory.CreateLogger<ApiExceptionHandler>());

        Assert.True(await handler.TryHandleAsync(context, failure, CancellationToken.None));

        Assert.Equal(status, context.Response.StatusCode);
        if (context.Response.Body is MemoryStream written)
        {
            Assert.Equal(0, written.Length);
        }

        Assert.Equal(events.Length, throwing.Throws);
        Assert.Equal(events, logs.Find(entry => entry.Category == HandlerCategory).Select(entry => entry.EventId.Name));
        Assert.All(logs.Entries, entry => Assert.Null(entry.ExceptionType));
        Assert.False(logs.ContainsText(failure.Message));
    }

    // The boundary alone, with a log sink that throws for its category: a client abort still ends as a body-less 499,
    // and a failure after the response started still aborts the connection with the sent status unchanged. The
    // boundary never throws, and its one line still reaches the working sink.
    [Theory]
    [InlineData(false, StatusCodes.Status499ClientClosedRequest, RequestAbortedEvent)]
    [InlineData(true, StatusCodes.Status200OK, ResponseAbortedEvent)]
    public async Task ThrowingLogSinkNeverStopsTheBoundarysAnswer(bool responseStarted, int status, string eventName)
    {
        var sentinel = NewSentinel();
        using var logs = new CapturingLoggerProvider();
        using var throwing = new ThrowingLoggerProvider(typeof(ApiExceptionHandler));
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs).AddProvider(throwing));
        using var aborted = new CancellationTokenSource();
        using var body = new MemoryStream();
        var lifetime = new RecordingLifetimeFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        Exception failure;
        if (responseStarted)
        {
            context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
            failure = new InvalidOperationException(sentinel);
        }
        else
        {
            await aborted.CancelAsync();
            failure = new OperationCanceledException(sentinel);
        }

        lifetime.RequestAborted = aborted.Token;
        context.Response.Body = body;
        var boundary = new ApiExceptionBoundaryMiddleware(
            _ => Task.FromException(failure),
            loggerFactory.CreateLogger<ApiExceptionHandler>());

        await boundary.InvokeAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(responseStarted, lifetime.Aborted);
        Assert.Equal(0, body.Length);
        Assert.Equal(1, throwing.Throws);
        CapturedLogEntry line = Assert.Single(logs.Find(entry => entry.Category == HandlerCategory));
        Assert.Equal(eventName, line.EventId.Name);
        Assert.Null(line.ExceptionType);
        Assert.False(logs.ContainsText(sentinel));
    }

    // AAP 0.9.3: a failure after the response started cannot change the status the client already holds. The boundary
    // aborts the response, so it stays visibly incomplete (no last chunk), and logs one redacted Error line; neither
    // the exception-handler middleware nor Kestrel logs the exception, its message or its SQL text. The endpoint fails
    // only once the client holds the partial body, because an abort discards output the socket has not sent yet.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task FailureAfterTheResponseStartedOnKestrelLeavesItIncompleteAndLogsOneRedactedLine(
        string environment)
    {
        var sentinel = NewSentinel();
        var received = NewSignal();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            UseKestrel = true,
            MapEndpoints = endpoints => endpoints.MapGet(BoundaryTestPath, async context =>
            {
                SignalWhenFinished(context, finished);
                await WritePartialThenFailAsync(context, sentinel, received.Task);
            }),
        });

        var response = await GetRawUntilClosedAsync(host, BoundaryTestPath, PartialBody, received);
        await WaitForAsync(finished.Task, "the server to finish the failed request");

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("Transfer-Encoding: chunked", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PartialBody, response, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n0\r\n\r\n", response, StringComparison.Ordinal);
        var entry = AssertOneStartedResponseLine(host.Logs, sentinel, StatusCodes.Status200OK);
        Assert.Null(StateValue(entry, "WriteOutcome"));
    }

    // The same failure on TestServer: the client receives the head, its read of the body then fails, and the log is
    // the same single redacted line.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task FailureAfterTheResponseStartedOnTestServerFailsTheReadAndLogsOneRedactedLine(string environment)
    {
        var sentinel = NewSentinel();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(BoundaryTestPath, async context =>
            {
                SignalWhenFinished(context, finished);
                await WritePartialThenFailAsync(context, sentinel);
            }),
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync(BoundaryTestPath, HttpCompletionOption.ResponseHeadersRead);
        var read = await Record.ExceptionAsync(() => response.Content.ReadAsStringAsync());
        await WaitForAsync(finished.Task, "the server to finish the failed request");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(read);
        var entry = AssertOneStartedResponseLine(host.Logs, sentinel, StatusCodes.Status200OK);
        Assert.Null(StateValue(entry, "WriteOutcome"));
    }

    // AAP 0.3.4.2: a record write that committed before its started response failed is logged as "written", with the
    // status already sent and the write's operation.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task FailureAfterTheResponseStartedOnACommittedWriteLogsWritten(string environment)
    {
        var sentinel = NewSentinel();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints
                .MapPost(BoundaryTestPath, async context =>
                {
                    SignalWhenFinished(context, finished);
                    var outcome = WriteOutcomeFeature.GetOrCreate(context);
                    outcome.MarkWriteStarted();
                    outcome.MarkWritten();
                    context.Response.StatusCode = StatusCodes.Status201Created;
                    await WritePartialThenFailAsync(context, sentinel);
                })
                .WithMetadata(WriteEndpointMetadata.Insert),
        });
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, BoundaryTestPath);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var read = await Record.ExceptionAsync(() => response.Content.ReadAsStringAsync());
        await WaitForAsync(finished.Task, "the server to finish the failed request");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(read);
        var entry = AssertOneStartedResponseLine(host.Logs, sentinel, StatusCodes.Status201Created);
        Assert.Equal(ExpectedWritten, StateValue(entry, "WriteOutcome"));
        Assert.Equal(WriteEndpointMetadata.Insert.Operation, StateValue(entry, "Operation"));
    }

    // A caller that aborts after the response started is still an abort, not a failure: one Information line and no
    // Error line.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task CallerAbortAfterTheResponseStartedLogsOnlyTheAbort(string environment)
    {
        var started = NewSignal();
        var finished = NewSignal();
        await using var host = await ErrorPipelineHost.StartAsync(new ErrorPipelineHostOptions
        {
            EnvironmentName = environment,
            MapEndpoints = endpoints => endpoints.MapGet(BoundaryTestPath, async context =>
            {
                SignalWhenFinished(context, finished);
                await context.Response.WriteAsync(PartialBody);
                await context.Response.Body.FlushAsync();
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }),
        });
        using var client = host.CreateClient();

        var response = await client.GetAsync(BoundaryTestPath, HttpCompletionOption.ResponseHeadersRead);
        var status = response.StatusCode;
        await WaitForAsync(started.Task, "the response to start");

        // Once its head has arrived, a TestServer client walks away from a request by disposing the unread response.
        response.Dispose();
        await WaitForAsync(finished.Task, "the server to finish the aborted request");

        Assert.Equal(HttpStatusCode.OK, status);
        AssertOneClientAbortLine(host.Logs);
    }

    // The raw HTTP/1.1 client is bounded. A listener that sends part of a response head and then neither finishes nor
    // closes the connection makes the exchange fail within its timeout, with a fixed message that names the request
    // and none of the bytes that arrived; a cancellation the caller requests ends the exchange as that cancellation.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawExchangeWithAStalledListenerFailsWithoutQuotingTheResponse(bool callerCancels)
    {
        var sentinel = NewSentinel();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"));
        using var stop = new CancellationTokenSource();
        var stalling = StallAsync(listener, "HTTP/1.1 200 OK\r\nX-Sentinel: " + sentinel + "\r\n", stop.Token);
        var partialReceived = NewSignal();
        using var caller = new CancellationTokenSource();
        var timeout = callerCancels ? HostedWait : TimeSpan.FromSeconds(2);
        var clock = Stopwatch.StartNew();

        var failure = await Record.ExceptionAsync(() => RawHttp.SendAsync(
            address,
            "GET /stalled HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n",
            "GET /stalled (stalled listener)",
            timeout,
            caller.Token,
            text =>
            {
                if (text.Contains(sentinel, StringComparison.Ordinal))
                {
                    partialReceived.TrySetResult();
                    if (callerCancels)
                    {
                        caller.Cancel();
                    }
                }
            }));
        clock.Stop();
        await stop.CancelAsync();
        await stalling;

        Assert.True(partialReceived.Task.IsCompleted, "The stalled listener's partial head never reached the client.");
        Assert.True(clock.Elapsed < HostedWait, "The stalled exchange was not ended before HostedWait.");
        if (callerCancels)
        {
            var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(caller.Token, cancelled.CancellationToken);
        }
        else
        {
            var timedOut = Assert.IsType<Xunit.Sdk.FailException>(failure);
            Assert.DoesNotContain(sentinel, timedOut.Message, StringComparison.Ordinal);
            Assert.Equal(
                "The raw HTTP/1.1 exchange GET /stalled (stalled listener) did not end within 2 s.",
                timedOut.Message);
        }
    }

    // The routing and lookup errors a request alone produces.
    private const string UnknownFormCase = "GET of a form that does not exist";

    private const string NonIntegerViewIdCase = "GET with a viewId that is not an integer";

    private const string RecordPatchCase = "PATCH on a record";

    // The field-level 400s: a query parameter outside its range, and a record value outside its field's range.
    private const string PageZeroCase = "records list with page=0";

    private const string AgeOutOfRangeCase = "create with Age above its upper bound";

    // The read endpoints, each of which writes nothing and so documents no writeOutcome.
    private const string FormsListEndpoint = "GET /api/forms";

    private const string RecordsListEndpoint = "GET records";

    private const string CountEndpoint = "GET records/count";

    private const string SearchEndpoint = "POST records/search";

    // The store failures injected through FakeDataStore, named after its factories.
    private const string UnavailableFailure = nameof(FakeDataStore.Unavailable);

    private const string WriteFailureKind = nameof(FakeDataStore.WriteFailure);

    private const string SqlTextFailure = nameof(FakeDataStore.SqlTextException);

    // The third record write, beside CreateRoute and UpdateRoute.
    private const string DeleteRoute = "delete";

    // Where a redaction case places its sentinel.
    private const string QueryFilterPlacement = "q of a failing records list";

    private const string QueryPagePlacement = "page that is not an integer";

    private const string UnparsableCriterionPlacement = "criterion its number field cannot parse";

    private const string FailingSearchPlacement = "criterion of a failing search";

    private const string NumberTypeErrorPlacement = "text value for a number field";

    private const string OverLongTextPlacement = "text value longer than its nvarchar(4000) column";

    private const string FailingInsertPlacement = "valid value of a failing create";

    // The logging configurations the floor cases run under: the committed appsettings*.json alone, and with
    // LoweredLoggingSettings on top.
    private const string CommittedLogging = "committed Logging configuration";

    private const string LoweredLogging = "Logging settings lowering every level and enabling scopes";

    // The category of the hosting request lines, which carry the request path and query string.
    private const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    // The category of the startup lines, which carry the listen address, the environment name and the content root.
    private const string HostingLifetimeCategory = "Microsoft.Hosting.Lifetime";

    // The category of the framework's health-check service, whose per-check line repeats the check's own.
    private const string HealthCheckServiceCategory =
        "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService";

    // The floors the API keeps framework categories at, written out here rather than read from the product: each
    // category prefix, a category under it to probe, and the lowest level it may log at, where None means nothing.
    private static readonly (string CategoryPrefix, string ProbeCategory, LogLevel Floor)[] ProtectedFloors =
    [
        ("Microsoft.AspNetCore", HostingDiagnosticsCategory, LogLevel.Warning),
        (HostingLifetimeCategory, HostingLifetimeCategory, LogLevel.Warning),
        (HealthCheckServiceCategory, HealthCheckServiceCategory, LogLevel.None),
    ];

    // The cross-origin request of the CORS cases.
    private const string ForeignOrigin = "http://example.test";

    // The keyword that introduces a password in a SQL Server connection string.
    private const string PasswordKeyword = "Password=";

    // The members a framework error body could use to carry an exception; no error body may hold one.
    private static readonly string[] ExceptionMembers = ["exception", "stackTrace", "exceptionDetails"];

    private static readonly string TwoPageRecordsPath =
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.TwoPageViewId}/records");

    /// <summary>Each environment with each routing or lookup error a request alone produces.</summary>
    public static TheoryData<string, string> EnvironmentsAndRequestErrors =>
        EnvironmentsWith(UnknownFormCase, NonIntegerViewIdCase, RecordPatchCase);

    /// <summary>Each environment with each field-level 400.</summary>
    public static TheoryData<string, string> EnvironmentsAndFieldLevelErrors =>
        EnvironmentsWith(PageZeroCase, AgeOutOfRangeCase);

    /// <summary>Each environment with each create body whose <c>values</c> is not an object.</summary>
    public static TheoryData<string, string> EnvironmentsAndBodiesWithoutAValuesObject =>
        EnvironmentsWith("""{"values":5}""", "{}");

    /// <summary>Each environment with each search body whose <c>criteria</c> is empty, <c>null</c> or absent.</summary>
    public static TheoryData<string, string> EnvironmentsAndSearchBodiesWithoutCriteria =>
        EnvironmentsWith("""{"criteria":[]}""", """{"criteria":null}""", "{}");

    /// <summary>Each environment with each read endpoint and each store failure that ends it.</summary>
    public static TheoryData<string, string, string> ReadFailures
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            string[] endpoints = [FormsListEndpoint, RecordsListEndpoint, CountEndpoint, SearchEndpoint];
            foreach (string environment in EnvironmentNames)
            {
                foreach (string endpoint in endpoints)
                {
                    data.Add(environment, endpoint, UnavailableFailure);
                    data.Add(environment, endpoint, SqlTextFailure);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Each environment with each record write, each outcome a store failure can report, and each kind of store
    /// failure: unavailability (503) and an unexpected write failure (500).
    /// </summary>
    public static TheoryData<string, string, WriteOutcome, string> WriteFailures
    {
        get
        {
            var data = new TheoryData<string, string, WriteOutcome, string>();
            foreach (string environment in EnvironmentNames)
            {
                foreach (string route in new[] { CreateRoute, UpdateRoute, DeleteRoute })
                {
                    foreach (WriteOutcome outcome in Enum.GetValues<WriteOutcome>())
                    {
                        data.Add(environment, route, outcome, UnavailableFailure);
                        data.Add(environment, route, outcome, WriteFailureKind);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Each environment with each failure of the metadata read a create starts with: each kind of store exception
    /// reporting each outcome, and an exception that carries no outcome (<see langword="null"/>).
    /// </summary>
    public static TheoryData<string, string, WriteOutcome?> EnvironmentsAndMetadataFailures
    {
        get
        {
            var data = new TheoryData<string, string, WriteOutcome?>();
            foreach (string environment in EnvironmentNames)
            {
                foreach (WriteOutcome outcome in Enum.GetValues<WriteOutcome>())
                {
                    data.Add(environment, UnavailableFailure, outcome);
                    data.Add(environment, WriteFailureKind, outcome);
                }

                data.Add(environment, SqlTextFailure, null);
            }

            return data;
        }
    }

    /// <summary>Every outcome a store exception can report.</summary>
    public static TheoryData<WriteOutcome> StoreOutcomes =>
        new() { WriteOutcome.Unknown, WriteOutcome.NotWritten, WriteOutcome.Written };

    /// <summary>Each environment with a cross-origin request and a CORS preflight.</summary>
    public static TheoryData<string, bool> EnvironmentsAndCorsRequests
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string environment in EnvironmentNames)
            {
                data.Add(environment, false);
                data.Add(environment, true);
            }

            return data;
        }
    }

    /// <summary>Each environment with each query-string placement of a sentinel.</summary>
    public static TheoryData<string, string> EnvironmentsAndQueryPlacements =>
        EnvironmentsWith(QueryFilterPlacement, QueryPagePlacement);

    /// <summary>Each environment with each search-criterion placement of a sentinel.</summary>
    public static TheoryData<string, string> EnvironmentsAndCriterionPlacements =>
        EnvironmentsWith(UnparsableCriterionPlacement, FailingSearchPlacement);

    /// <summary>Each environment with each placement of a sentinel in the values of a create that fails.</summary>
    public static TheoryData<string, string> EnvironmentsAndValuePlacements =>
        EnvironmentsWith(NumberTypeErrorPlacement, OverLongTextPlacement, FailingInsertPlacement);

    /// <summary>Each environment with the committed and with the lowered logging configuration.</summary>
    public static TheoryData<string, string> EnvironmentsAndLoggingConfigurations =>
        EnvironmentsWith(CommittedLogging, LoweredLogging);

    // AAP 0.3.4.2: a form that does not exist, a viewId its int route constraint cannot match, and a method the record
    // route does not map are each the fixed ProblemDetails of their status in every environment. The PATCH carries an
    // application/json body, so the media-type guard, which acts only on the body endpoints, never turns its 405 into
    // a 415.
    [Theory]
    [MemberData(nameof(EnvironmentsAndRequestErrors))]
    public async Task RequestErrorsAreProblemDetailsInEveryEnvironment(string environment, string errorCase)
    {
        FakeDataStore store = FakeDataStore.WithStandardForms();
        string recordId = store.SeedRecord(FakeForms.TwoPageViewId);
        (HttpMethod method, string path, HttpStatusCode status, string detail) = errorCase switch
        {
            UnknownFormCase => (
                HttpMethod.Get,
                "/api/forms/999999",
                HttpStatusCode.NotFound,
                ExpectedFormNotFoundDetail),
            NonIntegerViewIdCase => (
                HttpMethod.Get,
                "/api/forms/abc",
                HttpStatusCode.NotFound,
                ExpectedResourceNotFoundDetail),
            RecordPatchCase => (
                HttpMethod.Patch,
                $"{TwoPageRecordsPath}/{recordId}",
                HttpStatusCode.MethodNotAllowed,
                ExpectedMethodNotAllowedDetail),
            _ => throw new ArgumentOutOfRangeException(nameof(errorCase), errorCase, "Unknown request error."),
        };
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Patch)
        {
            request.Content = JsonBody("{}");
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        JsonElement problem = AssertProblemDetails(response, body, status);
        Assert.Equal(detail, StringMember(problem, "detail"));
        Assert.Empty(store.WriteCalls);
    }

    // AAP 0.3.4.2: a field-level 400 holds an errors map keyed by the parameter or the canonical field name, each with
    // fixed messages; on a record write it carries no writeOutcome, which only a write's 500 and 503 hold.
    [Theory]
    [MemberData(nameof(EnvironmentsAndFieldLevelErrors))]
    public async Task FieldLevelErrorsAreKeyedByParameterOrFieldName(string environment, string errorCase)
    {
        FakeDataStore store = FakeDataStore.WithStandardForms();
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();

        string key = errorCase == PageZeroCase ? ExpectedPageErrorsKey : "Age";
        using HttpResponseMessage response = errorCase switch
        {
            PageZeroCase => await client.GetAsync($"{TwoPageRecordsPath}?{RequestParameters.Page}=0"),
            AgeOutOfRangeCase => await ApiTestClient.PostJsonAsync(
                client,
                TwoPageRecordsPath,
                new { values = new Dictionary<string, object?> { ["Age"] = 121 } }),
            _ => throw new ArgumentOutOfRangeException(nameof(errorCase), errorCase, "Unknown field-level error."),
        };
        string body = await response.Content.ReadAsStringAsync();

        JsonElement problem = AssertProblemDetails(response, body, HttpStatusCode.BadRequest);
        Assert.Equal(ExpectedValidationFailedDetail, StringMember(problem, "detail"));
        AssertErrorMessages(problem, key);
        Assert.False(problem.TryGetProperty("writeOutcome", out _), "A 400 carries writeOutcome.");
        Assert.Empty(store.WriteCalls);
    }

    // AAP 0.3.4.2: a create body whose values is not an object is the request-level 400, a plain ProblemDetails whose
    // detail names the problem, with no errors map, no writeOutcome and nothing written.
    [Theory]
    [MemberData(nameof(EnvironmentsAndBodiesWithoutAValuesObject))]
    public async Task BodyWithoutAValuesObjectIsARequestLevel400(string environment, string json)
    {
        FakeDataStore store = FakeDataStore.WithStandardForms();
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, TwoPageRecordsPath) { Content = JsonBody(json) };

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        JsonElement problem = AssertProblemDetails(response, body, HttpStatusCode.BadRequest);
        Assert.Equal(ExpectedValuesMustBeObjectDetail, StringMember(problem, "detail"));
        Assert.False(problem.TryGetProperty("errors", out _), "A request-level 400 carries an errors map.");
        Assert.False(problem.TryGetProperty("writeOutcome", out _), "A 400 carries writeOutcome.");
        Assert.Empty(store.WriteCalls);
    }

    // AAP 0.3.4.2: a search whose criteria is empty, null or absent is the request-level 400, a plain ProblemDetails
    // whose detail is the wire text itself, written out here rather than read from the constant the API sends, with no
    // errors map and no search run.
    [Theory]
    [MemberData(nameof(EnvironmentsAndSearchBodiesWithoutCriteria))]
    public async Task SearchWithoutCriteriaIsARequestLevel400(string environment, string json)
    {
        FakeDataStore store = FakeDataStore.WithStandardForms();
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TwoPageRecordsPath}/search")
        {
            Content = JsonBody(json),
        };

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        JsonElement problem = AssertProblemDetails(response, body, HttpStatusCode.BadRequest);
        Assert.Equal("criteria must not be empty", StringMember(problem, "detail"));
        Assert.False(problem.TryGetProperty("errors", out _), "A request-level 400 carries an errors map.");
        Assert.DoesNotContain(store.Calls, call => call.Method == nameof(IDataStore.SearchAsync));
        Assert.Empty(store.WriteCalls);
    }

    // AAP 0.3.4.2: a store failure on an endpoint that writes nothing, search included although it takes a body, is the
    // plain ProblemDetails of its classification: a 503 with the fixed unavailable text, or a 500 whose detail is the
    // generic sentence plus the body's own traceId, an expectation that does not depend on the environment, so the
    // detail is the same in Production and Development apart from the trace id. Neither carries writeOutcome. The
    // handler logs the failure once, and the SQL text of the unexpected failure reaches neither the body nor a log.
    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task ReadFailureIsPlainProblemDetailsWithoutWriteOutcome(
        string environment,
        string endpoint,
        string kind)
    {
        string sentinel = NewSentinel();
        FakeDataStore store = FakeDataStore.WithStandardForms();
        bool unavailable = kind == UnavailableFailure;
        Exception failure = unavailable
            ? FakeDataStore.Unavailable(WriteOutcome.NotWritten)
            : FakeDataStore.SqlTextException(sentinel);
        store.ThrowOn(ReadStoreMethod(endpoint), failure);
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await SendReadAsync(client, endpoint);
        string body = await response.Content.ReadAsStringAsync();

        HttpStatusCode status = unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError;
        JsonElement problem = AssertProblemDetails(response, body, status);
        string? detail = StringMember(problem, "detail");
        if (unavailable)
        {
            Assert.Equal(ExpectedUnavailableDetail, detail);
        }
        else
        {
            AssertUnexpectedDetail(detail, StringMember(problem, TraceIdMember));
        }

        Assert.False(
            problem.TryGetProperty("writeOutcome", out _),
            "A failure of an endpoint that writes nothing carries writeOutcome.");
        CapturedLogEntry line = AssertOneHandlerLine(factory, status);
        Assert.Null(StateValue(line, "WriteOutcome"));
        AssertRedacted(factory, body, sentinel, failure.Message);
    }

    // AAP 0.3.4.2: a store failure inside a record write is answered with its classification's status, 503 for
    // unavailability and 500 otherwise, and carries the writeOutcome the store reported, in every environment; the
    // outcome never changes the status. Each outcome is injected at the stage whose state agrees with it, so the write
    // log holds the change exactly when the store reported it written.
    [Theory]
    [MemberData(nameof(WriteFailures))]
    public async Task WriteFailureCarriesTheOutcomeTheStoreReported(
        string environment,
        string route,
        WriteOutcome outcome,
        string kind)
    {
        FakeDataStore store = FakeDataStore.WithStandardForms();
        string recordId = store.SeedRecord(
            FakeForms.TwoPageViewId,
            new Dictionary<string, FieldValue> { ["LastName"] = FieldValue.FromText("Original") });
        bool unavailable = kind == UnavailableFailure;
        Exception failure = unavailable ? FakeDataStore.Unavailable(outcome) : FakeDataStore.WriteFailure(outcome);
        FakeFailureStage stage = outcome == WriteOutcome.Written
            ? FakeFailureStage.AfterWrite
            : FakeFailureStage.BeforeWrite;
        string storeMethod = WriteStoreMethod(route);
        store.ThrowOn(storeMethod, failure, stage);
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await SendWriteAsync(client, route, recordId, "Changed");
        string body = await response.Content.ReadAsStringAsync();

        HttpStatusCode status = unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError;
        JsonElement problem = AssertProblemDetails(response, body, status);
        Assert.Equal(ExpectedWireOutcome(outcome), StringMember(problem, "writeOutcome"));
        string? detail = StringMember(problem, "detail");
        if (unavailable)
        {
            Assert.Equal(ExpectedUnavailableDetail, detail);
        }
        else
        {
            AssertUnexpectedDetail(detail, StringMember(problem, TraceIdMember));
        }

        CapturedLogEntry line = AssertOneHandlerLine(factory, status);
        Assert.Equal(ExpectedWireOutcome(outcome), StateValue(line, "WriteOutcome"));
        if (stage == FakeFailureStage.AfterWrite)
        {
            Assert.Equal(storeMethod, Assert.Single(store.WriteCalls).Method);
        }
        else
        {
            Assert.Empty(store.WriteCalls);
        }
    }

    // AAP 0.3.4.2: a failure before any store write, here the metadata read a create starts with, is notWritten,
    // whatever outcome a store exception reports and when the exception carries none: no store write call was issued,
    // so the exception's outcome describes no write. The status still follows the failure's classification, 503 for
    // unavailability and 500 otherwise. MetadataReadFailureStore raises the failure, because FakeDataStore.ThrowOn
    // rejects a read failure that reports written.
    [Theory]
    [MemberData(nameof(EnvironmentsAndMetadataFailures))]
    public async Task MetadataReadFailureOnACreateIsNotWritten(string environment, string kind, WriteOutcome? reported)
    {
        string storeSentinel = NewSentinel();
        string valueSentinel = NewSentinel();
        FakeDataStore store = FakeDataStore.WithStandardForms();
        Exception failure = (kind, reported) switch
        {
            (UnavailableFailure, { } outcome) => FakeDataStore.Unavailable(outcome),
            (WriteFailureKind, { } outcome) => FakeDataStore.WriteFailure(outcome),
            (SqlTextFailure, null) => FakeDataStore.SqlTextException(storeSentinel),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown metadata read failure."),
        };
        await using ApiFactory factory = CreateFactory(environment, store);
        await using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDataStore>();
                services.AddSingleton<IDataStore>(new MetadataReadFailureStore(store, failure));
            }));
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await SendWriteAsync(client, CreateRoute, recordId: null, valueSentinel);
        string body = await response.Content.ReadAsStringAsync();

        HttpStatusCode status = kind == UnavailableFailure
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.InternalServerError;
        JsonElement problem = AssertProblemDetails(response, body, status);
        Assert.Equal(ExpectedNotWritten, StringMember(problem, "writeOutcome"));
        CapturedLogEntry line = AssertOneHandlerLine(factory, status);
        Assert.Equal(ExpectedNotWritten, StateValue(line, "WriteOutcome"));
        Assert.Single(store.Calls, call => call.Method == nameof(IDataStore.GetFormMetadataAsync));
        Assert.Empty(store.WriteCalls);
        Assert.DoesNotContain(store.Calls, call => call.Method == nameof(IDataStore.InsertAsync));
        AssertRedacted(factory, body, storeSentinel, valueSentinel, failure.Message);
    }

    // AAP 0.3.4.2: once the store reported a commit, the request is written, whatever outcome a later store exception
    // reports.
    [Theory]
    [MemberData(nameof(StoreOutcomes))]
    public void CommittedWriteIsWrittenWhateverTheStoreExceptionReports(WriteOutcome reported)
    {
        var feature = new WriteOutcomeFeature();
        feature.MarkWriteStarted();
        feature.MarkWritten();

        foreach (Exception failure in StoreExceptions(reported))
        {
            Assert.Equal(WriteOutcome.Written, ApiExceptionHandler.ResolveWriteOutcome(feature, failure));
        }
    }

    // A started write that has not committed reports the store exception's own outcome.
    [Theory]
    [MemberData(nameof(StoreOutcomes))]
    public void StartedWriteReportsTheStoreExceptionsOwnOutcome(WriteOutcome reported)
    {
        var feature = new WriteOutcomeFeature();
        feature.MarkWriteStarted();

        foreach (Exception failure in StoreExceptions(reported))
        {
            Assert.Equal(reported, ApiExceptionHandler.ResolveWriteOutcome(feature, failure));
        }
    }

    // AAP 0.3.4.2: the feature comes first, so once the store returned a result that wrote nothing, the request is
    // notWritten, whatever outcome a later store exception reports.
    [Theory]
    [MemberData(nameof(StoreOutcomes))]
    public void ReportedNoWriteIsNotWrittenWhateverALaterStoreExceptionReports(WriteOutcome reported)
    {
        var feature = new WriteOutcomeFeature();
        feature.MarkWriteStarted();
        feature.MarkNotWritten();

        foreach (Exception failure in StoreExceptions(reported))
        {
            Assert.Equal(WriteOutcome.NotWritten, ApiExceptionHandler.ResolveWriteOutcome(feature, failure));
        }
    }

    // A store write call started after a no-write result makes that result no longer the latest fact, so the store
    // exception's own outcome applies again.
    [Theory]
    [MemberData(nameof(StoreOutcomes))]
    public void WriteStartedAfterANoWriteResultReportsTheStoreExceptionsOwnOutcome(WriteOutcome reported)
    {
        var feature = new WriteOutcomeFeature();
        feature.MarkWriteStarted();
        feature.MarkNotWritten();
        feature.MarkWriteStarted();

        foreach (Exception failure in StoreExceptions(reported))
        {
            Assert.Equal(reported, ApiExceptionHandler.ResolveWriteOutcome(feature, failure));
        }
    }

    // With no write call issued, a failure that carries no outcome is notWritten: nothing can have been written.
    [Fact]
    public void FailureWithoutAnOutcomeBeforeAnyWriteIsNotWritten()
    {
        var failure = new InvalidOperationException(NewSentinel());

        Assert.Equal(WriteOutcome.NotWritten, ApiExceptionHandler.ResolveWriteOutcome(null, failure));
        Assert.Equal(
            WriteOutcome.NotWritten,
            ApiExceptionHandler.ResolveWriteOutcome(new WriteOutcomeFeature(), failure));
    }

    // AAP 0.3.4.2: with no write call issued, a store exception is notWritten, whatever outcome it reports: a failure
    // before any store write, such as the metadata read, leaves nothing written, so its outcome describes no write.
    [Theory]
    [MemberData(nameof(StoreOutcomes))]
    public void StoreExceptionBeforeAnyWriteIsNotWrittenWhateverItReports(WriteOutcome reported)
    {
        foreach (Exception failure in StoreExceptions(reported))
        {
            Assert.Equal(WriteOutcome.NotWritten, ApiExceptionHandler.ResolveWriteOutcome(null, failure));
            Assert.Equal(
                WriteOutcome.NotWritten,
                ApiExceptionHandler.ResolveWriteOutcome(new WriteOutcomeFeature(), failure));
        }
    }

    // AAP 0.7.6: CORS is not registered, because the client is same-origin, so neither a cross-origin request nor a
    // preflight gets any Access-Control-* header. A preflight's status is not asserted: any non-CORS answer is correct.
    [Theory]
    [MemberData(nameof(EnvironmentsAndCorsRequests))]
    public async Task CrossOriginRequestsGetNoCorsHeaders(string environment, bool preflight)
    {
        await using ApiFactory factory = CreateFactory(environment, FakeDataStore.WithStandardForms());
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(preflight ? HttpMethod.Options : HttpMethod.Get, "/api/forms");
        request.Headers.Add("Origin", ForeignOrigin);
        if (preflight)
        {
            request.Headers.Add("Access-Control-Request-Method", HttpMethods.Get);
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        if (!preflight)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        string[] corsHeaders = response.Headers
            .Concat(response.Content.Headers)
            .Select(header => header.Key)
            .Where(name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(corsHeaders.Length == 0, "The response carries CORS headers: " + string.Join(", ", corsHeaders));
    }

    // AAP 0.9.3: a value sent in the query string reaches neither the error body nor any log line: not the 500 of a
    // records list whose store fails, nor the 400 of a page that is not an integer, whose errors entry is the fixed
    // coercion message. The hosting request lines, which carry the URL, are dropped by the committed Warning floor on
    // Microsoft.AspNetCore, so lowering it fails this case.
    [Theory]
    [MemberData(nameof(EnvironmentsAndQueryPlacements))]
    public async Task QueryStringValuesAreRedacted(string environment, string placement)
    {
        string querySentinel = NewSentinel();
        string storeSentinel = NewSentinel();
        FakeDataStore store = FakeDataStore.WithStandardForms();
        Exception failure = FakeDataStore.SqlTextException(storeSentinel);
        store.ThrowOn(nameof(IDataStore.ListAsync), failure);
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();

        bool filter = placement == QueryFilterPlacement;
        string parameter = filter ? RequestParameters.Query : RequestParameters.Page;
        using HttpResponseMessage response = await client.GetAsync(
            $"{TwoPageRecordsPath}?{parameter}={Uri.EscapeDataString(querySentinel)}");
        string body = await response.Content.ReadAsStringAsync();

        if (filter)
        {
            AssertProblemDetails(response, body, HttpStatusCode.InternalServerError);
            AssertOneHandlerLine(factory, HttpStatusCode.InternalServerError);
        }
        else
        {
            JsonElement problem = AssertProblemDetails(response, body, HttpStatusCode.BadRequest);
            string message = Assert.Single(AssertErrorMessages(problem, ExpectedPageErrorsKey));
            Assert.Equal(ExpectedIntegerExpectedMessage, message);
        }

        AssertRedacted(factory, body, querySentinel, storeSentinel, failure.Message);
    }

    // AAP 0.9.3: a search criterion's value reaches neither the field-level 400 of a value its number field cannot
    // parse nor the 500 of a search whose store fails, nor any log line.
    [Theory]
    [MemberData(nameof(EnvironmentsAndCriterionPlacements))]
    public async Task SearchCriterionValuesAreRedacted(string environment, string placement)
    {
        string criterionSentinel = NewSentinel();
        string storeSentinel = NewSentinel();
        FakeDataStore store = FakeDataStore.WithStandardForms();
        Exception failure = FakeDataStore.SqlTextException(storeSentinel);
        store.ThrowOn(nameof(IDataStore.SearchAsync), failure);
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();

        bool unparsable = placement == UnparsableCriterionPlacement;
        string field = unparsable ? "Age" : "LastName";
        using HttpResponseMessage response = await ApiTestClient.PostJsonAsync(
            client,
            $"{TwoPageRecordsPath}/search",
            new { criteria = new[] { new { field, value = criterionSentinel } } });
        string body = await response.Content.ReadAsStringAsync();

        if (unparsable)
        {
            JsonElement problem = AssertProblemDetails(response, body, HttpStatusCode.BadRequest);
            AssertErrorMessages(problem, field);
        }
        else
        {
            AssertProblemDetails(response, body, HttpStatusCode.InternalServerError);
            AssertOneHandlerLine(factory, HttpStatusCode.InternalServerError);
        }

        AssertRedacted(factory, body, criterionSentinel, storeSentinel, failure.Message);
    }

    // AAP 0.9.3: a field value of a create that fails reaches neither its body nor any log line: not the field-level
    // 400 of a text value sent for a number field or of a text longer than its nvarchar(4000) column (no fake field
    // has a metaFields MaxLength, so the column bound is the length check), nor the 500 of an insert that fails once
    // the write started, which is therefore unknown.
    [Theory]
    [MemberData(nameof(EnvironmentsAndValuePlacements))]
    public async Task FieldValuesOfAFailingCreateAreRedacted(string environment, string placement)
    {
        string valueSentinel = NewSentinel();
        string storeSentinel = NewSentinel();
        FakeDataStore store = FakeDataStore.WithStandardForms();
        Exception failure = FakeDataStore.SqlTextException(storeSentinel);
        store.ThrowOn(nameof(IDataStore.InsertAsync), failure);
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();

        (string field, string value) = placement switch
        {
            NumberTypeErrorPlacement => ("Age", valueSentinel),
            OverLongTextPlacement => ("LastName", valueSentinel.PadRight(4001, 'x')),
            FailingInsertPlacement => ("LastName", valueSentinel),
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown value placement."),
        };
        using HttpResponseMessage response = await ApiTestClient.PostJsonAsync(
            client,
            TwoPageRecordsPath,
            new { values = new Dictionary<string, object?> { [field] = value } });
        string body = await response.Content.ReadAsStringAsync();

        if (placement == FailingInsertPlacement)
        {
            JsonElement problem = AssertProblemDetails(response, body, HttpStatusCode.InternalServerError);
            Assert.Equal(ExpectedUnknown, StringMember(problem, "writeOutcome"));
            AssertOneHandlerLine(factory, HttpStatusCode.InternalServerError);
        }
        else
        {
            JsonElement problem = AssertProblemDetails(response, body, HttpStatusCode.BadRequest);
            AssertErrorMessages(problem, field);
            Assert.DoesNotContain(store.Calls, call => call.Method == nameof(IDataStore.InsertAsync));
        }

        AssertRedacted(factory, body, valueSentinel, storeSentinel, failure.Message);
    }

    // AAP 0.9.3 and 0.3.4.6: with the configured server refusing connections, a read is the 503 ProblemDetails and a
    // create the 503 whose metadata read failed before any write, so notWritten. Neither the connection string, nor
    // its password, nor the Password= keyword, nor the submitted value reaches a body or a log line.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task UnreachableDatabaseIsUnavailableWithoutTheConnectionString(string environment)
    {
        string password = NewSentinel();
        string valueSentinel = NewSentinel();
        string connectionString = UnreachableConnectionString(password);
        await using ApiFactory factory = CreateUnreachableFactory(environment, connectionString);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage list = await client.GetAsync("/api/forms");
        string listBody = await list.Content.ReadAsStringAsync();
        AssertRefusedConnectionIsUnavailable(factory, list);
        JsonElement listProblem = AssertProblemDetails(list, listBody, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(ExpectedUnavailableDetail, StringMember(listProblem, "detail"));
        Assert.False(listProblem.TryGetProperty("writeOutcome", out _), "The forms list 503 carries writeOutcome.");

        using HttpResponseMessage create = await ApiTestClient.PostJsonAsync(
            client,
            "/api/forms/1/records",
            new { values = new Dictionary<string, object?> { ["LastName"] = valueSentinel } });
        string createBody = await create.Content.ReadAsStringAsync();
        AssertRefusedConnectionIsUnavailable(factory, create);
        JsonElement createProblem = AssertProblemDetails(create, createBody, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(ExpectedUnavailableDetail, StringMember(createProblem, "detail"));
        Assert.Equal(ExpectedNotWritten, StringMember(createProblem, "writeOutcome"));

        Assert.Equal(2, factory.Logs.Find(entry => entry.Category == HandlerCategory).Count);
        AssertRedacted(
            factory,
            listBody + createBody,
            password,
            valueSentinel,
            connectionString,
            PasswordKeyword);
    }

    // AAP 0.9.3: the message of a failure that holds SQL text and a value reaches neither the 500 body nor any log
    // line. The handler passes no exception object to the logger, so no entry carries exception text, and its one line
    // names the exception's type only; no framework line reports the exception either.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task ExceptionMessageHoldingSqlTextIsRedacted(string environment)
    {
        string sentinel = NewSentinel();
        FakeDataStore store = FakeDataStore.WithStandardForms();
        store.ThrowOn(
            nameof(IDataStore.GetFormMetadataAsync),
            new InvalidOperationException($"{SecretSqlText} WHERE x = '{sentinel}'"));
        await using ApiFactory factory = CreateFactory(environment, store);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.TwoPageViewId}"));
        string body = await response.Content.ReadAsStringAsync();

        JsonElement problem = AssertProblemDetails(response, body, HttpStatusCode.InternalServerError);
        AssertUnexpectedDetail(StringMember(problem, "detail"), StringMember(problem, TraceIdMember));
        CapturedLogEntry line = AssertOneHandlerLine(factory, HttpStatusCode.InternalServerError);
        Assert.Null(line.ExceptionType);
        Assert.Null(line.ExceptionMessage);
        Assert.Equal(typeof(InvalidOperationException).FullName, StateValue(line, "ExceptionType"));
        Assert.Empty(factory.Logs.Find(IsFrameworkExceptionLine));
        AssertRedacted(factory, body, sentinel, SecretSqlText);
    }

    // AAP 0.3.4.7 and 0.9.3: /health answers a server that refuses connections with exactly {"status":"unhealthy"},
    // the one error body outside ProblemDetails. The health check logs its failure, and neither that line nor any other
    // carries the connection string, its password or the Password= keyword.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task UnhealthyDatabaseAnswersTheFixedBodyWithoutTheConnectionString(string environment)
    {
        string password = NewSentinel();
        string connectionString = UnreachableConnectionString(password);
        await using ApiFactory factory = CreateUnreachableFactory(environment, connectionString);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiTestClient.JsonMediaType, response.Content.Headers.ContentType?.MediaType);
        using (JsonDocument document = JsonDocument.Parse(body))
        {
            JsonProperty member = Assert.Single(document.RootElement.EnumerateObject());
            Assert.Equal("status", member.Name);
            Assert.Equal(ExpectedUnhealthyStatus, member.Value.GetString());
        }

        Assert.True(
            factory.Logs.Find(entry => entry.Category == typeof(SqlServerHealthCheck).FullName).Count > 0,
            "The health check logged no failure, so the redaction check would search no line of it.");
        AssertRedacted(factory, body, password, connectionString, PasswordKeyword);
    }

    // AAP 0.9.3 and 0.3.4.7: whatever the Logging configuration sets, for every provider or for the console alone, no
    // framework category whose low-level lines carry request or configuration values logs below its floor, and a
    // query-string value reaches no captured line. Under the committed configuration the startup lines would otherwise
    // carry the content root and environment name; under the lowered one the hosting request line would also carry the
    // URL and its query string, and the health-check service would write its per-check lines. Every other category
    // keeps its configured level, so the lowered configuration lets an application category log at Trace.
    [Theory]
    [MemberData(nameof(EnvironmentsAndLoggingConfigurations))]
    public async Task ProtectedFrameworkCategoriesStayAtTheirFloorsUnderEveryLoggingConfiguration(
        string environment,
        string configuration)
    {
        string querySentinel = NewSentinel();
        bool lowered = configuration == LoweredLogging;
        await using ApiFactory factory = CreateLoggingFactory(environment, lowered);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage list = await client.GetAsync(
            $"{TwoPageRecordsPath}?{RequestParameters.Query}={Uri.EscapeDataString(querySentinel)}");
        using HttpResponseMessage health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        ILogger application = factory.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RecordService).FullName!);
        Assert.Equal(lowered, application.IsEnabled(LogLevel.Trace));
        AssertNoLineBelowTheFloors(factory.Logs);
        Assert.False(factory.Logs.ContainsText(querySentinel), "A captured log line holds the query-string value.");
    }

    // AAP 0.9.3: the floors hold in every rebuild of the filter options, which a configuration reload makes, and
    // against a console-only rule, which the logger factory prefers to every generic rule. Each rule's filter rejects
    // every protected category below its floor, so whichever rule the factory chooses drops those lines. Before and
    // after a reload no provider's logger for a protected category is enabled below its floor, while an application
    // category keeps the Trace level the configuration gives it.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task LoggingFloorsHoldInEveryRebuildOfTheFilterOptions(string environment)
    {
        await using ApiFactory factory = CreateLoggingFactory(environment, lowered: true);
        IServiceProvider services = factory.Services;

        LoggerFilterOptions rebuilt = services
            .GetRequiredService<IOptionsFactory<LoggerFilterOptions>>()
            .Create(Microsoft.Extensions.Options.Options.DefaultName);
        Assert.Contains(
            rebuilt.Rules,
            rule => string.Equals(rule.ProviderName, "Console", StringComparison.OrdinalIgnoreCase)
                && rule.CategoryName == HostingDiagnosticsCategory
                && rule.LogLevel == LogLevel.Trace);
        AssertEveryRuleKeepsTheFloors(rebuilt);
        AssertLoggerLevels(services);

        ReloadConfiguration(services);

        AssertEveryRuleKeepsTheFloors(
            services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>().CurrentValue);
        AssertLoggerLevels(services);
    }

    // AAP 0.3.4.2 and 0.9.3: the JSON console the API logs through writes no scopes, which carry the request path and
    // connection id, stamps every line with an ISO 8601 UTC timestamp, and writes each event as one unindented line,
    // whatever Logging:Console sets, before and after a configuration reload. The line is written by the host's own
    // formatter with a request-path scope active, under a culture whose date and time separators are not ISO 8601's.
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task JsonConsoleWritesNoScopesAndAUtcTimestampWhateverItsConfiguration(string environment)
    {
        string pathSentinel = NewSentinel();
        await using ApiFactory factory = CreateLoggingFactory(environment, lowered: true);
        IServiceProvider services = factory.Services;

        AssertConsoleLine(services, pathSentinel);

        ReloadConfiguration(services);

        AssertConsoleLine(services, pathSentinel);
    }


    // Every case names its client folder, so none falls back to the committed default (the repository's web/dist),
    // whose presence depends on whether the client was built.
    private static ApiFactory CreateFactory(string environment, string clientFolder) =>
        new(new ApiFactoryOptions
        {
            EnvironmentName = environment,
            ConnectionMode = ApiConnectionMode.None,
            ExtraSettings = ClientPathSetting(clientFolder),
        });

    // A host over store, which replaces the SQL Server store, so no connection string is set and no SQL Server is
    // needed; the client is absent, as for the /api miss cases.
    private static ApiFactory CreateFactory(string environment, FakeDataStore store) =>
        new(new ApiFactoryOptions
        {
            EnvironmentName = environment,
            DataStore = store,
            ConnectionMode = ApiConnectionMode.None,
            ExtraSettings = ClientPathSetting(AbsentClientFolder()),
        });

    // A host whose real SQL Server store is configured with connectionString, so its failures pass through the store's
    // own classification.
    private static ApiFactory CreateUnreachableFactory(string environment, string connectionString) =>
        new(new ApiFactoryOptions
        {
            EnvironmentName = environment,
            ConnectionMode = ApiConnectionMode.Supplied,
            ConnectionString = connectionString,
            ExtraSettings = ClientPathSetting(AbsentClientFolder()),
        });

    // Port 1 on IPv4 loopback refuses the TCP connection at once, so no SQL Server is needed and none is reached. The
    // string names no database, because the server is never reached and the tests never name a database. The password
    // is the sentinel the redaction cases search for.
    private static string UnreachableConnectionString(string password) =>
        "Server=127.0.0.1,1;User Id=sa;" + PasswordKeyword + password +
        ";TrustServerCertificate=True;Connect Timeout=2;Pooling=false";

    private static Dictionary<string, string?> ClientPathSetting(string clientFolder) =>
        new(StringComparer.OrdinalIgnoreCase) { [ApiOptions.ClientPathKey] = clientFolder };

    // A host over the standard fake forms with the committed logging configuration, or with LoweredLoggingSettings on
    // top of it; the client is absent.
    private static ApiFactory CreateLoggingFactory(string environment, bool lowered)
    {
        Dictionary<string, string?> settings = ClientPathSetting(AbsentClientFolder());
        if (lowered)
        {
            foreach ((string key, string? value) in LoweredLoggingSettings())
            {
                settings[key] = value;
            }
        }

        return new ApiFactory(new ApiFactoryOptions
        {
            EnvironmentName = environment,
            DataStore = FakeDataStore.WithStandardForms(),
            ConnectionMode = ApiConnectionMode.None,
            ExtraSettings = settings,
        });
    }

    // Settings that, without the API's own floors, would lower every protected framework category to Trace for every
    // provider, through the generic rules and through the console-only rules the logger factory prefers to them, and
    // would turn the console's scopes on, its timestamp to local time in another pattern, its JSON writer to indented
    // output spread over several lines, and its formatter away from JSON.
    private static Dictionary<string, string?> LoweredLoggingSettings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Logging:LogLevel:Default"] = "Trace",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
        ["Logging:LogLevel:" + HostingDiagnosticsCategory] = "Trace",
        ["Logging:LogLevel:" + HostingLifetimeCategory] = "Trace",
        ["Logging:LogLevel:Microsoft.Extensions.Diagnostics.HealthChecks"] = "Trace",
        ["Logging:Console:LogLevel:Default"] = "Trace",
        ["Logging:Console:LogLevel:" + HostingDiagnosticsCategory] = "Trace",
        ["Logging:Console:LogLevel:" + HostingLifetimeCategory] = "Trace",
        ["Logging:Console:LogLevel:" + HealthCheckServiceCategory] = "Trace",
        ["Logging:Console:FormatterName"] = ConsoleFormatterNames.Simple,
        ["Logging:Console:FormatterOptions:IncludeScopes"] = "true",
        ["Logging:Console:FormatterOptions:TimestampFormat"] = "HH:mm:ss ",
        ["Logging:Console:FormatterOptions:UseUtcTimestamp"] = "false",
        ["Logging:Console:FormatterOptions:JsonWriterOptions:Indented"] = "true",
    };

    // Reloads every configuration source of the host and raises its change token, as a changed appsettings.json does,
    // so every options instance bound to configuration is rebuilt.
    private static void ReloadConfiguration(IServiceProvider services) =>
        ((IConfigurationRoot)services.GetRequiredService<IConfiguration>()).Reload();

    // AAP 0.9.3: no captured line from a protected framework category below its floor.
    private static void AssertNoLineBelowTheFloors(CapturingLoggerProvider logs)
    {
        string[] belowFloors = logs.Entries
            .Where(entry => ProtectedFloors.Any(floor =>
                entry.Category.StartsWith(floor.CategoryPrefix, StringComparison.OrdinalIgnoreCase)
                && entry.Level < floor.Floor))
            .Select(entry => $"{entry.Category} at {entry.Level}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            belowFloors.Length == 0,
            "Captured lines below a protected floor: " + string.Join(", ", belowFloors));
    }

    // Whichever rule the logger factory chooses for the console, its filter drops every protected category's lines
    // below its floor.
    private static void AssertEveryRuleKeepsTheFloors(LoggerFilterOptions options)
    {
        Assert.NotEmpty(options.Rules);
        Assert.All(options.Rules, rule =>
        {
            Assert.NotNull(rule.Filter);
            foreach ((_, string category, LogLevel floor) in ProtectedFloors)
            {
                Assert.False(
                    rule.Filter(typeof(ConsoleLoggerProvider).FullName, category, floor - 1),
                    $"A filter rule lets a {category} line below its floor through.");
            }
        });
    }

    // Through the host's logger factory, which asks every provider's chosen rule: below its floor every protected
    // category is off for every provider, at its floor it is on unless the floor is None, and an application category
    // keeps its configured Trace level.
    private static void AssertLoggerLevels(IServiceProvider services)
    {
        ILoggerFactory loggers = services.GetRequiredService<ILoggerFactory>();
        foreach ((_, string category, LogLevel floor) in ProtectedFloors)
        {
            ILogger logger = loggers.CreateLogger(category);
            Assert.False(logger.IsEnabled(floor - 1), $"A provider logs {category} below its floor.");
            if (floor != LogLevel.None)
            {
                Assert.True(logger.IsEnabled(floor), $"No provider logs {category} at its floor.");
            }
        }

        Assert.True(
            loggers.CreateLogger(typeof(RecordService).FullName!).IsEnabled(LogLevel.Trace),
            "The configured Trace level of an application category does not apply.");
    }

    // One line written by the host's JSON console formatter, with a request-path scope holding pathSentinel active and,
    // as the current culture, one whose date and time separators differ from ISO 8601's: the console is the JSON
    // formatter, the event is a single line holding one JSON object without scopes or the sentinel, and its Timestamp
    // is ISO 8601 in UTC.
    private static void AssertConsoleLine(IServiceProvider services, string pathSentinel)
    {
        Assert.Equal(
            ConsoleFormatterNames.Json,
            services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);
        JsonConsoleFormatterOptions options =
            services.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>().CurrentValue;
        Assert.False(options.IncludeScopes, "The JSON console includes scopes.");
        Assert.NotNull(options.TimestampFormat);
        Assert.True(options.UseUtcTimestamp, "The JSON console writes local time.");
        Assert.False(options.JsonWriterOptions.Indented, "The JSON console indents its output.");

        ConsoleFormatter formatter = Assert.Single(
            services.GetServices<ConsoleFormatter>(),
            candidate => candidate.Name == ConsoleFormatterNames.Json);
        var scopes = new LoggerExternalScopeProvider();
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.DateSeparator = ".";
        culture.DateTimeFormat.TimeSeparator = ".";
        CultureInfo previous = CultureInfo.CurrentCulture;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            CultureInfo.CurrentCulture = culture;
            using (scopes.Push(new[] { new KeyValuePair<string, object?>("RequestPath", "/api/" + pathSentinel) }))
            {
                formatter.Write(
                    new LogEntry<string>(
                        LogLevel.Warning,
                        typeof(RecordService).FullName!,
                        new EventId(1, "Fixed"),
                        "A fixed line.",
                        exception: null,
                        (state, _) => state),
                    scopes,
                    writer);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        string text = writer.ToString();
        Assert.False(text.Contains(pathSentinel, StringComparison.Ordinal), "The console line holds the scope's path.");

        // The formatter ends each event with one new line, so the event is a single line only when nothing before
        // that terminator breaks it.
        Assert.EndsWith(Environment.NewLine, text, StringComparison.Ordinal);
        string body = text[..^Environment.NewLine.Length];
        Assert.False(body.AsSpan().ContainsAny('\r', '\n'), "The console event spans more than one line.");
        using JsonDocument line = JsonDocument.Parse(text);
        Assert.False(line.RootElement.TryGetProperty("Scopes", out _), "The console line carries scopes.");
        Assert.True(
            line.RootElement.TryGetProperty("Timestamp", out JsonElement timestamp)
                && timestamp.ValueKind == JsonValueKind.String,
            "The console line carries no Timestamp string.");
        string written = timestamp.GetString()!;
        Assert.Matches(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|\+00:00)\z", written);
        Assert.Equal(
            TimeSpan.Zero,
            DateTimeOffset.Parse(written, CultureInfo.InvariantCulture, DateTimeStyles.None).Offset);
    }

    // Each environment with each case.
    private static TheoryData<string, string> EnvironmentsWith(params string[] cases)
    {
        var data = new TheoryData<string, string>();
        foreach (string environment in EnvironmentNames)
        {
            foreach (string item in cases)
            {
                data.Add(environment, item);
            }
        }

        return data;
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, ApiTestClient.JsonMediaType);

    // A client folder that does not exist and is never created, so the host serves no client and maps no client
    // fallback. The /api miss cases select it so they do not depend on a built web/dist: with the client served, an
    // /api miss stays a 404 only once the API's own /api routes are mapped.
    private static string AbsentClientFolder() =>
        Path.Combine(Path.GetTempPath(), "epiinfo-absent-client-" + Guid.NewGuid().ToString("N"));

    private static void AddAccept(HttpRequestMessage request, string? accept)
    {
        if (accept is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("Accept", accept), "The Accept header was not added.");
        }
    }

    private static async Task AssertFixedProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string type,
        string title,
        string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(
            string.Equals(ProblemMediaType, response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal),
            "The error response is not application/problem+json.");

        JsonElement body = ApiTestClient.AssertProblemDetails(
            await ApiTestClient.ReadJsonElementAsync(response),
            (int)status);

        Assert.True(
            ProblemMembers.SequenceEqual(
                body.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal),
                StringComparer.Ordinal),
            "The error body does not hold exactly type, title, status, detail and traceId.");
        Assert.True(
            string.Equals(type, StringMember(body, "type"), StringComparison.Ordinal),
            "The error body's type is not the status default.");
        Assert.True(
            string.Equals(title, StringMember(body, "title"), StringComparison.Ordinal),
            "The error body's title is not the status default.");
        Assert.True(
            string.Equals(detail, StringMember(body, "detail"), StringComparison.Ordinal),
            "The error body's detail is not the fixed text of its status.");
    }

    // The detail and trace id a status-only path produces: a framework body shaped by Customize, which writes nothing,
    // or the status-code page written to an empty response.
    private static async Task<(string? Detail, string? TraceId)> ProduceStatusOnlyProblemAsync(
        string path,
        int status,
        DefaultHttpContext context,
        MemoryStream body)
    {
        switch (path)
        {
            case CustomizePath:
                var problemContext = new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = new ProblemDetails { Status = status },
                };
                ApiProblems.Customize(problemContext);
                Assert.Equal(status, problemContext.ProblemDetails.Status);
                return (
                    problemContext.ProblemDetails.Detail,
                    problemContext.ProblemDetails.Extensions.TryGetValue(TraceIdMember, out object? traceId)
                        ? traceId as string
                        : null);

            case StatusCodePagePath:
                context.Response.StatusCode = status;
                await ApiProblems.WriteStatusCodePageAsync(
                    new StatusCodeContext(context, new StatusCodePagesOptions(), _ => Task.CompletedTask));
                Assert.Equal(status, context.Response.StatusCode);
                Assert.Equal(ProblemMediaType, context.Response.ContentType);
                JsonElement problem = ApiTestClient.AssertProblemDetails(Parse(body), status);
                return (StringMember(problem, "detail"), StringMember(problem, TraceIdMember));

            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown status-only path.");
        }
    }

    // The no-activity cases clear any ambient activity, so the request's trace identifier is the expected trace id.
    private static Activity? StartActivity(bool withActivity)
    {
        if (!withActivity)
        {
            Activity.Current = null;
            return null;
        }

        return new Activity(nameof(ErrorContractTests)).Start();
    }

    private static void AssertTraceId(string? traceId, Activity? activity, HttpContext context)
    {
        string expected = activity?.Id ?? context.TraceIdentifier;
        Assert.False(string.IsNullOrWhiteSpace(expected), "The expected trace id is blank.");
        Assert.True(
            string.Equals(expected, traceId, StringComparison.Ordinal),
            activity is null
                ? "The traceId member is not the request's trace identifier."
                : "The traceId member is not the current activity's id.");
    }

    private static void AssertUnexpectedDetail(string? detail, string? traceId)
    {
        Assert.False(string.IsNullOrWhiteSpace(traceId), "The 500 body has no traceId.");
        Assert.True(
            string.Equals(ExpectedUnexpectedSentence, ApiProblems.UnexpectedDetail, StringComparison.Ordinal),
            "The generic 500 text is not the one fixed sentence.");
        Assert.True(
            string.Equals(
                string.Concat(ExpectedUnexpectedSentence, ExpectedTraceIdLabel, traceId),
                detail,
                StringComparison.Ordinal),
            "The 500 detail is not the fixed sentence followed by the body's traceId.");
        Assert.True(
            string.Equals(ApiProblems.UnexpectedDetailFor(traceId), detail, StringComparison.Ordinal),
            "The 500 detail is not the generic sentence plus the body's traceId.");
        Assert.True(
            detail is not null && detail.StartsWith(ExpectedUnexpectedSentence, StringComparison.Ordinal),
            "The 500 detail does not open with the generic sentence.");
    }

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        return (context, body);
    }

    private static JsonElement Parse(MemoryStream body)
    {
        using JsonDocument document = JsonDocument.Parse(body.ToArray());
        return document.RootElement.Clone();
    }

    private static string? StringMember(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // The writeOutcome wire value of each outcome, from the literals above rather than the product's own mapping.
    private static string ExpectedWireOutcome(WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.NotWritten => ExpectedNotWritten,
        WriteOutcome.Unknown => ExpectedUnknown,
        WriteOutcome.Written => ExpectedWritten,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a write outcome."),
    };

    // A host over store as Program.cs composes it, with the client absent: on real Kestrel for a Kestrel framing and on
    // TestServer otherwise.
    private static ApiFactoryBase CreateMemberCaseFactory(FakeDataStore store, string environment, string framing)
    {
        var options = ApiFactoryOptions.ForFake(store, environment) with
        {
            ExtraSettings = ClientPathSetting(AbsentClientFolder()),
        };
        return framing is KestrelContentLength or KestrelChunked or KestrelUnframed
            ? new KestrelApiFactory(options)
            : new ApiFactory(options);
    }

    // The method and path of a body route; the update route gets a freshly seeded active record.
    private static (HttpMethod Method, string Path) ResolveRoute(FakeDataStore store, string route) => route switch
    {
        CreateRoute => (HttpMethod.Post, RecordsPath),
        UpdateRoute => (HttpMethod.Put, $"{RecordsPath}/{store.SeedRecord(FakeForms.EmptyViewId)}"),
        SearchRoute => (HttpMethod.Post, $"{RecordsPath}/search"),
        FieldEventRoute => (HttpMethod.Post, FieldEventPath),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown body route."),
    };

    // Sends body as application/json exactly as framing says, and returns the status, media type and body text: the
    // TestServer framings through the factory's in-memory client, the Kestrel framings as raw HTTP/1.1 over a socket to
    // the address the factory's Kestrel bound.
    private static async Task<(int Status, string? MediaType, string Body)> SendBodyAsync(
        ApiFactoryBase factory,
        HttpMethod method,
        string path,
        string framing,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        if (framing is TestServerContentLength or TestServerUnknownLength)
        {
            HttpContent content = framing == TestServerUnknownLength
                ? new UnknownLengthContent(bytes)
                : new ByteArrayContent(bytes);
            content.Headers.ContentType = new(ApiTestClient.JsonMediaType);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using var response = await client.SendAsync(request);
            return (
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                await response.Content.ReadAsStringAsync());
        }

        var raw = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{method.Method} {path} HTTP/1.1\r\n")
            .Append("Host: 127.0.0.1\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Type: {ApiTestClient.JsonMediaType}\r\n")
            .Append("Connection: close\r\n");
        if (framing == KestrelChunked)
        {
            raw.Append("Transfer-Encoding: chunked\r\n\r\n");
            if (bytes.Length > 0)
            {
                raw.Append(CultureInfo.InvariantCulture, $"{bytes.Length:x}\r\n{body}\r\n");
            }

            // The last chunk: for an empty body it is the only one.
            raw.Append("0\r\n\r\n");
        }
        else if (framing == KestrelUnframed)
        {
            Assert.Empty(bytes);
            raw.Append("\r\n");
        }
        else
        {
            raw.Append(CultureInfo.InvariantCulture, $"Content-Length: {bytes.Length}\r\n\r\n").Append(body);
        }

        var address = factory is KestrelApiFactory kestrel
            ? kestrel.BaseAddress
            : throw new InvalidOperationException("Raw HTTP needs the Kestrel host.");
        var label = $"{method.Method} {path} ({framing})";
        var exchange = await RawHttp.SendAsync(address, raw.ToString(), label, HostedWait);
        Assert.False(
            exchange.ConnectionReset,
            $"The connection of {label} ended with a reset instead of an orderly close.");
        return ParseRawResponse(exchange.Response);
    }

    // Splits a raw HTTP/1.1 response into its status, media type and body, undoing chunked framing.
    private static (int Status, string? MediaType, string Body) ParseRawResponse(string response)
    {
        var headEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headEnd > 0, "The raw response has no complete head.");

        // Parsed without int.Parse, whose FormatException quotes the text it rejects.
        var lines = response[..headEnd].Split("\r\n");
        var statusLine = lines[0].Split(' ', 3);
        var status = 0;
        Assert.True(
            statusLine.Length >= 2
            && statusLine[0] == "HTTP/1.1"
            && int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out status),
            "The raw response's status line is not HTTP/1.1 followed by a status code.");

        string? mediaType = null;
        var chunked = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            Assert.True(colon > 0, "A raw response header line has no name before a colon.");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                mediaType = value.Split(';', 2)[0].Trim();
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                chunked = value.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            }
        }

        var body = response[(headEnd + 4)..];
        return (status, mediaType, chunked ? Unchunk(body) : body);
    }

    // The payload of a chunked body: each chunk's hexadecimal size line, data and CRLF, up to the zero-size chunk.
    private static string Unchunk(string body)
    {
        var payload = new StringBuilder();
        var index = 0;
        while (true)
        {
            var sizeEnd = body.IndexOf("\r\n", index, StringComparison.Ordinal);
            Assert.True(sizeEnd >= 0, "The chunked body ends before its last chunk.");
            Assert.True(
                int.TryParse(
                    body.AsSpan(index, sizeEnd - index),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var size),
                "A chunk of the raw response has no hexadecimal size line.");
            if (size == 0)
            {
                return payload.ToString();
            }

            Assert.True(
                size > 0 && (long)sizeEnd + 2 + size + 2 <= body.Length
                && body.AsSpan(sizeEnd + 2 + size, 2).SequenceEqual("\r\n"),
                "A chunk of the raw response is cut short or not ended by CRLF.");
            payload.Append(body, sizeEnd + 2, size);
            index = sizeEnd + 2 + size + 2;
        }
    }

    private static object? StateValue(CapturedLogEntry entry, string key) =>
        Assert.Single(entry.State, pair => pair.Key == key).Value;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Signals once the server has finished the request, after every middleware has returned, so every line the
    // request logs has been captured by then.
    private static void SignalWhenFinished(HttpContext context, TaskCompletionSource finished) =>
        context.Response.OnCompleted(() =>
        {
            finished.TrySetResult();
            return Task.CompletedTask;
        });

    private static async Task WaitForAsync(Task signal, string what)
    {
        var completed = await Task.WhenAny(signal, Task.Delay(HostedWait));
        Assert.True(completed == signal, "Timed out waiting for " + what + ".");
        await signal;
    }

    // Sends the head and part of the body, waits for clientReceived when given, then fails with an exception whose
    // message holds a sentinel and SQL text, as the inner exception of a store failure can.
    private static async Task WritePartialThenFailAsync(
        HttpContext context,
        string sentinel,
        Task? clientReceived = null)
    {
        await context.Response.WriteAsync(PartialBody);
        await context.Response.Body.FlushAsync();
        if (clientReceived is not null)
        {
            await clientReceived.WaitAsync(HostedWait);
        }

        throw new InvalidOperationException(sentinel + " " + SecretSqlText);
    }

    // Sends a GET for path over a new TCP connection to the Kestrel listener, signals received once the response text
    // holds expected, and reads until the server ends the connection, by a close or a reset, within HostedWait.
    // Returns every byte received, as text.
    private static async Task<string> GetRawUntilClosedAsync(
        ErrorPipelineHost host,
        string path,
        string expected,
        TaskCompletionSource received)
    {
        var address = host.Address ?? throw new InvalidOperationException("Raw HTTP needs the Kestrel host.");
        var request = "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
        var exchange = await RawHttp.SendAsync(
            address,
            request,
            $"GET {path} (Kestrel, read until the connection ends)",
            HostedWait,
            onReceived: text =>
            {
                if (text.Contains(expected, StringComparison.Ordinal))
                {
                    received.TrySetResult();
                }
            });

        // An aborted connection can end with a reset; what arrived before it is the response.
        return exchange.Response;
    }

    // Accepts one connection on listener, sends partial, and then holds the connection open, neither finishing the
    // response nor closing it, until stop is cancelled.
    private static async Task StallAsync(TcpListener listener, string partial, CancellationToken stop)
    {
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(stop);
            await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes(partial), stop);
            await Task.Delay(Timeout.Infinite, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The case is over; the connection was closed as it was disposed.
        }
    }

    // Exactly one line for a client abort: the handler category's Information RequestAborted, with no exception
    // object, no failure line beside it, and no framework line about the exception.
    private static void AssertOneClientAbortLine(CapturingLoggerProvider logs)
    {
        var entry = Assert.Single(logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(RequestAbortedEvent, entry.EventId.Name);
        Assert.Null(entry.ExceptionType);
        Assert.Empty(logs.Find(IsFrameworkExceptionLine));
    }

    // An OperationCanceledException as the store raises it for SqlClient's acknowledgement of a cancelled command after
    // a rollback that failed: the classified SQL error number and the rollback's type name and number in Data, and a
    // message holding the sentinel and SQL text, which no line may contain.
    private static OperationCanceledException StoreCancellation(string sentinel)
    {
        var cancellation = new OperationCanceledException(sentinel + " " + SecretSqlText);
        cancellation.Data[SqlErrorClassifier.SqlErrorNumberKey] = StoreCancellationSqlErrorNumber;
        cancellation.Data[SqlErrorClassifier.RollbackExceptionTypeKey] = StoreCancellationRollbackType;
        cancellation.Data[SqlErrorClassifier.RollbackSqlErrorNumberKey] = StoreCancellationRollbackNumber;
        return cancellation;
    }

    // The line's SQL error number and rollback arguments are those StoreCancellation records when expected, and null
    // for a cancellation that carries none.
    private static void AssertStoreNumbers(CapturedLogEntry entry, bool expected)
    {
        Assert.Equal<object?>(expected ? StoreCancellationSqlErrorNumber : null, StateValue(entry, "SqlErrorNumber"));
        Assert.Equal<object?>(
            expected ? StoreCancellationRollbackType : null,
            StateValue(entry, "RollbackExceptionType"));
        Assert.Equal<object?>(
            expected ? StoreCancellationRollbackNumber : null,
            StateValue(entry, "RollbackSqlErrorNumber"));
    }

    // Exactly one line for a failure after the response started: the handler category's Error ResponseAborted with
    // the status already sent and the exception's type only, no framework line about the exception, and neither the
    // sentinel nor the SQL text of the exception's message in any line.
    private static CapturedLogEntry AssertOneStartedResponseLine(
        CapturingLoggerProvider logs,
        string sentinel,
        int sentStatus)
    {
        var entry = Assert.Single(logs.Find(captured => captured.Category == HandlerCategory));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(ResponseAbortedEvent, entry.EventId.Name);
        Assert.Null(entry.ExceptionType);
        Assert.Equal(sentStatus, StateValue(entry, "StatusCode"));
        Assert.Equal(typeof(InvalidOperationException).FullName, StateValue(entry, "ExceptionType"));
        Assert.Empty(logs.Find(IsFrameworkExceptionLine));
        Assert.False(logs.ContainsText(sentinel));
        Assert.False(logs.ContainsText(SecretSqlText));
        return entry;
    }

    // A line the framework writes about an exception it saw: anything from the diagnostics middleware (the exception
    // handler and the developer exception page), Kestrel's application-error line, or any line carrying an exception.
    private static bool IsFrameworkExceptionLine(CapturedLogEntry entry) =>
        entry.Category.StartsWith("Microsoft.AspNetCore.Diagnostics", StringComparison.Ordinal)
        || entry.EventId.Name == "ApplicationError"
        || entry.ExceptionType is not null;

    // The shape every error under /api has in every environment: the expected status, application/problem+json, a
    // non-empty type, title, detail and traceId, a status member equal to the HTTP status, and no member that could
    // carry an exception. Messages are fixed texts that never quote the body.
    private static JsonElement AssertProblemDetails(HttpResponseMessage response, string body, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.True(
            string.Equals(
                ProblemMediaType,
                response.Content.Headers.ContentType?.MediaType,
                StringComparison.Ordinal),
            "The error response is not application/problem+json.");

        JsonElement problem;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            problem = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new Xunit.Sdk.XunitException("The error body is not JSON.");
        }

        ApiTestClient.AssertProblemDetails(problem, (int)expected);
        foreach (string member in new[] { "type", "title", "detail", TraceIdMember })
        {
            Assert.False(
                string.IsNullOrWhiteSpace(StringMember(problem, member)),
                $"ProblemDetails member '{member}' is empty.");
        }

        foreach (string member in ExceptionMembers)
        {
            Assert.False(problem.TryGetProperty(member, out _), $"The error body carries an '{member}' member.");
        }

        return problem;
    }

    // The messages of a field-level 400 under key: a non-empty array of non-blank strings.
    private static string[] AssertErrorMessages(JsonElement problem, string key)
    {
        Assert.True(
            problem.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object,
            "The field-level 400 has no errors object.");
        Assert.True(
            errors.TryGetProperty(key, out JsonElement messages) && messages.ValueKind == JsonValueKind.Array,
            $"The errors object has no '{key}' array.");

        string[] texts = messages.EnumerateArray()
            .Select(message => message.ValueKind == JsonValueKind.String ? message.GetString() : null)
            .Select(text => text ?? string.Empty)
            .ToArray();
        Assert.True(
            texts.Length > 0 && !texts.Any(string.IsNullOrWhiteSpace),
            $"The '{key}' errors entry is not a non-empty list of messages.");
        return texts;
    }

    // AAP 0.3.4.2: one handler line per failure, carrying the status it answered, at Information for a 4xx and at
    // Error for a 5xx, the 503 of an unavailable store included. A redaction case asserts it first, so its search runs
    // over a line that exists.
    private static CapturedLogEntry AssertOneHandlerLine(ApiFactoryBase factory, HttpStatusCode status)
    {
        CapturedLogEntry line = Assert.Single(factory.Logs.Find(entry => entry.Category == HandlerCategory));
        Assert.Equal((int)status, StateValue(line, "StatusCode"));
        Assert.Equal((int)status >= 500 ? LogLevel.Error : LogLevel.Information, line.Level);
        return line;
    }

    // AAP 0.9.3: no secret in the error body or in any captured log line. A failure names the secret by its position
    // and the offending entries by category and level, never the secret or an entry's text.
    private static void AssertRedacted(ApiFactoryBase factory, string body, params string[] secrets)
    {
        for (int index = 0; index < secrets.Length; index++)
        {
            string secret = secrets[index];
            Assert.False(
                body.Contains(secret, StringComparison.Ordinal),
                string.Create(CultureInfo.InvariantCulture, $"Secret #{index} appears in the response body."));

            if (factory.Logs.ContainsText(secret))
            {
                string entries = string.Join(
                    ", ",
                    factory.Logs
                        .Find(entry => Mentions(entry, secret))
                        .Select(entry => $"{entry.Category} at {entry.Level}"));
                string where = entries.Length == 0 ? "an entry" : entries;
                Assert.Fail(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Secret #{index} appears in a captured log line: {where}."));
            }
        }
    }

    // A coarse search of one entry's texts, used only to name the entries in which ContainsText found a secret.
    private static bool Mentions(CapturedLogEntry entry, string text) =>
        entry.Category.Contains(text, StringComparison.Ordinal)
        || entry.Message.Contains(text, StringComparison.Ordinal)
        || entry.StateMessage?.Contains(text, StringComparison.Ordinal) == true
        || entry.State.Any(pair =>
            pair.Key.Contains(text, StringComparison.Ordinal)
            || Convert.ToString(pair.Value, CultureInfo.InvariantCulture)
                ?.Contains(text, StringComparison.Ordinal) == true)
        || entry.Scopes.Any(scope => scope.Contains(text, StringComparison.Ordinal))
        || entry.ExceptionType?.Contains(text, StringComparison.Ordinal) == true
        || entry.ExceptionMessage?.Contains(text, StringComparison.Ordinal) == true;

    // AAP 0.3.4.6: a refused TCP connection is an evidenced connectivity failure, so 503. Any other status is reported
    // as a classifier defect with the SQL error number the handler logged and the numbers the classifier admits, since
    // the managed network layer used on Linux may report a refused connection under a number the set lacks.
    private static void AssertRefusedConnectionIsUnavailable(ApiFactoryBase factory, HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return;
        }

        CapturedLogEntry? line = factory.Logs.Find(entry => entry.Category == HandlerCategory).LastOrDefault();
        object? number = line?.State.FirstOrDefault(pair => pair.Key == "SqlErrorNumber").Value;
        string admitted = string.Join(", ", EpiInfo.Data.Sql.SqlErrorClassifier.UnavailableErrorNumbers.Order());
        Assert.Fail(
            string.Create(
                CultureInfo.InvariantCulture,
                $"A refused connection was answered {(int)response.StatusCode}, not 503: a SqlErrorClassifier "
                + $"defect. The handler logged SQL error {number ?? "none"}; the unavailable numbers are "
                + $"[{admitted}]."));
    }

    // The store member each read endpoint ends in.
    private static string ReadStoreMethod(string endpoint) => endpoint switch
    {
        FormsListEndpoint => nameof(IDataStore.ListFormsAsync),
        RecordsListEndpoint => nameof(IDataStore.ListAsync),
        CountEndpoint => nameof(IDataStore.CountAsync),
        SearchEndpoint => nameof(IDataStore.SearchAsync),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unknown read endpoint."),
    };

    // A valid request to each read endpoint on the two-page form.
    private static Task<HttpResponseMessage> SendReadAsync(HttpClient client, string endpoint) => endpoint switch
    {
        FormsListEndpoint => client.GetAsync("/api/forms"),
        RecordsListEndpoint => client.GetAsync(TwoPageRecordsPath),
        CountEndpoint => client.GetAsync($"{TwoPageRecordsPath}/count"),
        SearchEndpoint => ApiTestClient.PostJsonAsync(
            client,
            $"{TwoPageRecordsPath}/search",
            new { criteria = new[] { new { field = "LastName", value = "Smith" } } }),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unknown read endpoint."),
    };

    // The store member each record write ends in.
    private static string WriteStoreMethod(string route) => route switch
    {
        CreateRoute => nameof(IDataStore.InsertAsync),
        UpdateRoute => nameof(IDataStore.UpdateAsync),
        DeleteRoute => nameof(IDataStore.SoftDeleteAsync),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown record write."),
    };

    // A valid record write on the two-page form: a create or an update setting LastName, or a delete, of recordId.
    private static Task<HttpResponseMessage> SendWriteAsync(
        HttpClient client,
        string route,
        string? recordId,
        string lastName)
    {
        var body = new { values = new Dictionary<string, object?> { ["LastName"] = lastName } };
        return route switch
        {
            CreateRoute => ApiTestClient.PostJsonAsync(client, TwoPageRecordsPath, body),
            UpdateRoute => ApiTestClient.PutJsonAsync(client, $"{TwoPageRecordsPath}/{recordId}", body),
            DeleteRoute => client.DeleteAsync($"{TwoPageRecordsPath}/{recordId}"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown record write."),
        };
    }

    // Both store exceptions reporting outcome, each built with its full constructor.
    private static Exception[] StoreExceptions(WriteOutcome outcome) =>
    [
        new DataStoreUnavailableException(
            nameof(IDataStore.InsertAsync),
            outcome,
            sqlErrorNumber: -2,
            innerException: new TimeoutException(NewSentinel())),
        new DataStoreWriteException(
            nameof(IDataStore.UpdateAsync),
            outcome,
            sqlErrorNumber: 547,
            innerException: null),
    ];

    /// <summary>
    /// A store that answers every call from <paramref name="inner"/>, except the metadata read, which raises
    /// <paramref name="failure"/> once <paramref name="inner"/> has logged the call. It fails that read with any
    /// exception, a store exception reporting written included, which <see cref="FakeDataStore.ThrowOn"/> rejects for a
    /// read because no real read can have written.
    /// </summary>
    private sealed class MetadataReadFailureStore(FakeDataStore inner, Exception failure) : IDataStore
    {
        public async Task<FormMetadata?> GetFormMetadataAsync(int viewId, CancellationToken cancellationToken)
        {
            _ = await inner.GetFormMetadataAsync(viewId, cancellationToken);
            throw failure;
        }

        public Task<IReadOnlyList<FormSummary>> ListFormsAsync(CancellationToken cancellationToken) =>
            inner.ListFormsAsync(cancellationToken);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<FieldOption>>> GetOptionsAsync(
            FormMetadata form,
            CancellationToken cancellationToken) =>
            inner.GetOptionsAsync(form, cancellationToken);

        public Task<string?> GetCheckCodeAsync(int viewId, CancellationToken cancellationToken) =>
            inner.GetCheckCodeAsync(viewId, cancellationToken);

        public Task<int> CountAsync(FormMetadata form, bool includeDeleted, CancellationToken cancellationToken) =>
            inner.CountAsync(form, includeDeleted, cancellationToken);

        public Task<RecordDto?> GetAtPositionAsync(
            FormMetadata form,
            int position,
            bool includeDeleted,
            CancellationToken cancellationToken) =>
            inner.GetAtPositionAsync(form, position, includeDeleted, cancellationToken);

        public Task<RecordDto?> GetByIdAsync(
            FormMetadata form,
            string globalRecordId,
            CancellationToken cancellationToken) =>
            inner.GetByIdAsync(form, globalRecordId, cancellationToken);

        public Task<NavigationPosition?> GetSetPositionAsync(
            FormMetadata form,
            string globalRecordId,
            bool includeDeleted,
            CancellationToken cancellationToken) =>
            inner.GetSetPositionAsync(form, globalRecordId, includeDeleted, cancellationToken);

        public Task<RecordPage> ListAsync(
            FormMetadata form,
            RecordListQuery query,
            CancellationToken cancellationToken) =>
            inner.ListAsync(form, query, cancellationToken);

        public Task<SearchResponse> SearchAsync(
            FormMetadata form,
            IReadOnlyList<SearchCondition> conditions,
            bool includeDeleted,
            string? excludeGlobalRecordId,
            CancellationToken cancellationToken) =>
            inner.SearchAsync(form, conditions, includeDeleted, excludeGlobalRecordId, cancellationToken);

        public Task<bool> PingAsync(CancellationToken cancellationToken) => inner.PingAsync(cancellationToken);

        public Task<RecordWriteResult> InsertAsync(
            FormMetadata form,
            IReadOnlyDictionary<string, FieldValue> values,
            SaveStamp stamp,
            FormPermissions? permissions,
            CancellationToken cancellationToken) =>
            inner.InsertAsync(form, values, stamp, permissions, cancellationToken);

        public Task<RecordWriteResult> UpdateAsync(
            FormMetadata form,
            string globalRecordId,
            IReadOnlyDictionary<string, FieldValue> submitted,
            Func<SaveStamp> createStamp,
            Func<StoredRecord, RecordValidationResult> requiredGate,
            FormPermissions? permissions,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(
                form,
                globalRecordId,
                submitted,
                createStamp,
                requiredGate,
                permissions,
                cancellationToken);

        public Task<SoftDeleteResult> SoftDeleteAsync(
            FormMetadata form,
            string globalRecordId,
            Func<SaveStamp> createStamp,
            FormPermissions? permissions,
            CancellationToken cancellationToken) =>
            inner.SoftDeleteAsync(form, globalRecordId, createStamp, permissions, cancellationToken);

        public Task<FormPermissions?> GetPermissionsAsync(FormMetadata form, CancellationToken cancellationToken) =>
            inner.GetPermissionsAsync(form, cancellationToken);
    }

    /// <summary>
    /// Fails every serialisation of <typeparamref name="T"/> with the serializer's own exception type, carrying a
    /// sentinel where a real failure would carry the path and value it failed on.
    /// </summary>
    private sealed class FailingWriteConverter<T>(string sentinel) : JsonConverter<T>
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Response types are never read from a request.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            throw new JsonException(sentinel);
    }

    /// <summary>
    /// Request content whose length is not known in advance, as a streamed upload is: the client sends it chunked
    /// and the server sees no <c>Content-Length</c>.
    /// </summary>
    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(content.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A response body whose every write and flush fails with an exception that is not a client abort, as a failing
    /// server stream would.
    /// </summary>
    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw Failure();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(Failure());

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw Failure();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(Failure());

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(Failure());

        private static InvalidOperationException Failure() => new("Response body failure injected by the test.");
    }

    /// <summary>A response the server has started sending, so its status and headers can no longer change.</summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    /// <summary>The request lifetime of a test context, recording whether the connection was aborted.</summary>
    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        public bool Aborted { get; private set; }

        public void Abort() => Aborted = true;
    }

    /// <summary>
    /// The one raw HTTP/1.1 client of these cases: it sends a request byte for byte over a new TCP connection to an
    /// IPv4 loopback listener and reads until the server ends the connection, so a case sees the exact framing, chunk
    /// terminator included. One deadline bounds the connect, the write and every read.
    /// </summary>
    private static class RawHttp
    {
        /// <summary>
        /// Sends <paramref name="request"/> to <paramref name="address"/> and reads until the server ends the
        /// connection, by a close or a reset.
        /// </summary>
        /// <param name="address">The listener: <c>http://127.0.0.1</c> with the port it is bound to.</param>
        /// <param name="request">The complete request, head and body; it should carry <c>Connection: close</c>.</param>
        /// <param name="exchange">The request's method, path and framing, which the timeout failure names.</param>
        /// <param name="timeout">The longest the exchange may take, from the connect to the connection's end.</param>
        /// <param name="cancellationToken">Ends the exchange as a cancellation, never as a timeout failure.</param>
        /// <param name="onReceived">Called after each read with every byte received so far, as text.</param>
        /// <returns>
        /// The raw response text, and whether the connection ended with a reset or I/O failure rather than an orderly
        /// close.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="address"/> is not IPv4 loopback with a bound port.
        /// </exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
        /// <exception cref="Xunit.Sdk.FailException">
        /// The exchange outlived <paramref name="timeout"/>. The message names <paramref name="exchange"/> and the
        /// timeout, never a byte received.
        /// </exception>
        public static async Task<RawExchange> SendAsync(
            Uri address,
            string request,
            string exchange,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Action<string>? onReceived = null)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(request);
            if (!IPAddress.TryParse(address.Host, out var host)
                || !IPAddress.Loopback.Equals(host)
                || address.Port <= 0)
            {
                throw new ArgumentException(
                    "Raw HTTP is sent only to an IPv4 loopback listener on its bound port.",
                    nameof(address));
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var response = new MemoryStream();
            var reset = false;
            try
            {
                await socket.ConnectAsync(new IPEndPoint(host, address.Port), deadline.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);

                var buffer = new byte[4096];
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
                    {
                        response.Write(buffer, 0, read);
                        onReceived?.Invoke(Encoding.UTF8.GetString(response.GetBuffer(), 0, (int)response.Length));
                    }
                }
                catch (IOException) when (!deadline.IsCancellationRequested)
                {
                    // An aborted connection can end with a reset; what arrived before it is the response.
                    reset = true;
                }
            }
            catch (Exception failure) when (
                deadline.IsCancellationRequested
                && failure is OperationCanceledException or IOException or SocketException)
            {
                // The caller's cancellation stays a cancellation. Only the deadline fails the case, with a message that
                // names the request and never what the server sent.
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Fail(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The raw HTTP/1.1 exchange {exchange} did not end within {timeout.TotalSeconds} s."));
            }

            return new RawExchange(Encoding.UTF8.GetString(response.ToArray()), reset);
        }
    }
}

/// <summary>
/// The write-outcome bookkeeping of the three record writes (AAP 0.3.4.2, 0.3.4.6), proved over HTTP through the real
/// record endpoints, <see cref="RecordService"/> and <see cref="ApiExceptionHandler"/>: a commit the store returned is
/// reported <c>written</c>, a definitive result that wrote nothing <c>notWritten</c>, and a started write that fails
/// without a classification <c>unknown</c>; a throwing audit logger never changes a committed response; and the 404
/// and 409 of a write send no data statement and change nothing.
/// </summary>
/// <remarks>
/// <para>
/// The class needs no database: it carries no <c>Integration</c> trait, joins no database collection and takes no
/// <c>DatabaseWriteLock</c>, so <c>--filter "Category!=Integration"</c> selects it. Every case starts its own
/// <see cref="FakeDataStore"/>, clock and host. <see cref="ErrorContractTests"/>, beside it in this file, holds the
/// error-contract, CORS and redaction cases hosted on <c>ApiFactory</c>.
/// </para>
/// <para>
/// A failure after the endpoint has chosen its result is injected by a one-shot seam that makes the response body
/// throw before the response starts, so the exception handler still answers and states what the request recorded.
/// </para>
/// </remarks>
public sealed class RecordWriteOutcomeTests
{
    private const string Post = "POST";
    private const string Put = "PUT";
    private const string Delete = "DELETE";

    private const string PutMissingRecord = "PUT on a missing record";
    private const string PutDeletedRecord = "PUT on a deleted record";
    private const string PutFailingRequiredGate = "PUT failing the stored required gate";
    private const string PutDeniedMissingPageInsert = "PUT needing a denied missing-page INSERT";
    private const string DeleteMissingRecord = "DELETE of a missing record";
    private const string DeleteDeletedRecord = "DELETE of an already-deleted record";

    private const string WriteOutcomeMember = "writeOutcome";
    private const string StoreFailureText = "store failure injected by RecordWriteOutcomeTests";

    private const string LastName = "LastName";
    private const string Age = "Age";
    private const string Sex = "Sex";
    private const string Ill = "Ill";
    private const string Notes = "Notes";
    private const string Comment = "Comment";

    private const string CreatedLastName = "Created";
    private const double CreatedAge = 42;
    private const string CreatedNotes = "first visit";
    private const string SeededLastName = "Seeded";
    private const double SeededAge = 30;
    private const string SeededNotes = "seeded note";
    private const string UpdatedLastName = "Updated";
    private const string SeededComment = "seeded comment";
    private const string UpdatedComment = "updated comment";

    private const int TwoPageViewId = FakeForms.TwoPageViewId;

    private static readonly DateTime WriteTime = new(2024, 6, 3, 10, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The three record writes.</summary>
    public static TheoryData<string> WriteMethods => new() { Post, Put, Delete };

    /// <summary>Each record write with each stage at which the store throws an unclassified failure.</summary>
    public static TheoryData<string, FakeFailureStage> UnclassifiedStoreFailures => new()
    {
        { Post, FakeFailureStage.BeforeWrite },
        { Post, FakeFailureStage.AfterWrite },
        { Put, FakeFailureStage.BeforeWrite },
        { Put, FakeFailureStage.AfterWrite },
        { Delete, FakeFailureStage.BeforeWrite },
        { Delete, FakeFailureStage.AfterWrite },
    };

    /// <summary>Every result by which the store reports that a started write wrote nothing.</summary>
    public static TheoryData<string> DefinitiveNoWriteResults => new()
    {
        PutMissingRecord,
        PutDeletedRecord,
        PutFailingRequiredGate,
        PutDeniedMissingPageInsert,
        DeleteMissingRecord,
        DeleteDeletedRecord,
    };

    /// <summary>A valid <c>PUT</c> and a <c>DELETE</c> naming a record the base table does not hold.</summary>
    public static TheoryData<string> MissingRecordWrites => new() { PutMissingRecord, DeleteMissingRecord };

    // A returned commit is marked written by the service itself, so a failure that follows it, before the
    // response starts, reports written, and the change the store committed is the one the request asked for.
    [Theory]
    [MemberData(nameof(WriteMethods))]
    public async Task CommittedWriteThatFailsBeforeTheResponseStartsReportsWritten(string method)
    {
        var (store, request) = ArrangeCommittableWrite(method);
        await using var host = await RecordWriteHost.StartAsync(store);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);
        host.Seam.Arm();

        using var response = await SendAsync(host.Client, request);

        Assert.True(host.Seam.Fired, "The post-result failure seam never raised its failure.");
        await AssertWriteProblemAsync(response, ErrorContractTests.ExpectedWritten);
        Assert.Equal(StoreWriteMethod(method), Assert.Single(store.WriteCalls).Method);
        AssertCommitted(store, request, before);
    }

    // The post-commit audit line is written so that its own failure cannot change the response (AAP 0.3.4.6).
    [Theory]
    [MemberData(nameof(WriteMethods))]
    public async Task ThrowingAuditLoggerLeavesTheCommittedResponseUnchanged(string method)
    {
        var (store, request) = ArrangeCommittableWrite(method);
        var auditLogger = new ThrowingAuditLoggerProvider();
        await using var host = await RecordWriteHost.StartAsync(store, auditLogger);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);

        using var response = await SendAsync(host.Client, request);

        Assert.Equal(1, auditLogger.Throws);
        switch (method)
        {
            case Post:
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var created = await ApiTestClient.ReadJsonElementAsync(response);
                var createdId = created.GetProperty("globalRecordId").GetString();
                Assert.NotNull(createdId);
                Assert.Equal(RecordPath(request.ViewId, createdId), response.Headers.Location?.OriginalString);
                Assert.Equal(CreatedLastName, created.GetProperty("values").GetProperty(LastName).GetString());
                Assert.Equal(Assert.Single(store.GetRecords(request.ViewId)).GlobalRecordId, createdId);
                break;

            case Put:
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var updated = await ApiTestClient.ReadJsonElementAsync(response);
                Assert.Equal(request.GlobalRecordId, updated.GetProperty("globalRecordId").GetString());
                Assert.Equal(UpdatedLastName, updated.GetProperty("values").GetProperty(LastName).GetString());
                break;

            case Delete:
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Not a record write.");
        }

        Assert.Empty(host.Logs.Find(entry => entry.Category == typeof(ApiExceptionHandler).FullName));
        Assert.Equal(StoreWriteMethod(method), Assert.Single(store.WriteCalls).Method);
        AssertCommitted(store, request, before);
    }

    // Once the store call is issued, a failure that is not a store exception cannot say whether data was
    // written, so it is unknown whether the fake threw before its change or after applying it.
    [Theory]
    [MemberData(nameof(UnclassifiedStoreFailures))]
    public async Task StartedWriteThatFailsUnclassifiedReportsUnknown(string method, FakeFailureStage stage)
    {
        var (store, request) = ArrangeCommittableWrite(method);
        store.ThrowOn(StoreWriteMethod(method), new InvalidOperationException(StoreFailureText), stage);
        await using var host = await RecordWriteHost.StartAsync(store);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);

        using var response = await SendAsync(host.Client, request);

        await AssertWriteProblemAsync(response, ErrorContractTests.ExpectedUnknown);
        if (stage == FakeFailureStage.BeforeWrite)
        {
            Assert.Empty(store.WriteCalls);
            AssertSameRecords(before, store.GetRecords(request.ViewId));
        }
        else
        {
            Assert.Equal(StoreWriteMethod(method), Assert.Single(store.WriteCalls).Method);
            AssertCommitted(store, request, before);
        }
    }

    // Every result other than a commit means the store wrote nothing (IDataStore), so a failure while that
    // result's response is written, before it starts, reports notWritten rather than unknown.
    [Theory]
    [MemberData(nameof(DefinitiveNoWriteResults))]
    public async Task DefinitiveNoWriteResultThatFailsBeforeTheResponseStartsReportsNotWritten(string scenario)
    {
        var (store, request) = ArrangeNoWrite(scenario);
        await using var host = await RecordWriteHost.StartAsync(store);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);
        host.Seam.Arm();

        using var response = await SendAsync(host.Client, request);

        Assert.True(host.Seam.Fired, "The post-result failure seam never raised its failure.");
        await AssertWriteProblemAsync(response, ErrorContractTests.ExpectedNotWritten);
        Assert.Contains(store.Calls, call => call.Method == StoreWriteMethod(request.Method) && !call.IsWrite);
        Assert.Empty(store.WriteCalls);
        AssertSameRecords(before, store.GetRecords(request.ViewId));
    }

    // A PUT on a record the test created and then deleted is the fixed 409, decided by the store's locked read
    // with no data statement, and the record, its stamps included, is unchanged.
    [Fact]
    public async Task PutOnARecordTheTestDeletedIs409AndWritesNothing()
    {
        var store = new FakeDataStore(FakeForms.TwoPage);
        await using var host = await RecordWriteHost.StartAsync(store);
        host.Clock.SetLocalNow(WriteTime);
        var created = await ApiTestClient.CreateRecordAsync(host.Client, TwoPageViewId, CreatedValues());
        var path = RecordPath(TwoPageViewId, created.GlobalRecordId);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var deleted = await host.Client.DeleteAsync(path))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var before = store.GetRecord(TwoPageViewId, created.GlobalRecordId);
        Assert.NotNull(before);
        Assert.Equal(RecordQueryRules.DeletedRecStatus, before.RecStatus);
        store.ClearCalls();
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        using var response = await ApiTestClient.PutJsonAsync(host.Client, path, new { values = UpdatedValues() });

        await AssertPlainProblemAsync(
            response,
            StatusCodes.Status409Conflict,
            ErrorContractTests.ExpectedRecordDeletedDetail);
        Assert.Contains(store.Calls, call => call.Method == nameof(IDataStore.UpdateAsync) && !call.IsWrite);
        Assert.Empty(store.WriteCalls);
        AssertSameRecord(before, store.GetRecord(TwoPageViewId, created.GlobalRecordId));
    }

    // A valid PUT or DELETE naming an id the base table does not hold is the fixed 404, decided by the store's
    // locked read with no data statement, and the form's records are unchanged.
    [Theory]
    [MemberData(nameof(MissingRecordWrites))]
    public async Task ValidWriteOnAMissingRecordIs404AndWritesNothing(string scenario)
    {
        var (store, request) = ArrangeNoWrite(scenario);
        await using var host = await RecordWriteHost.StartAsync(store);
        var before = store.GetRecords(request.ViewId);
        host.Clock.SetLocalNow(WriteTime);

        using var response = await SendAsync(host.Client, request);

        await AssertPlainProblemAsync(
            response,
            StatusCodes.Status404NotFound,
            ErrorContractTests.ExpectedRecordNotFoundDetail);
        Assert.Contains(store.Calls, call => call.Method == StoreWriteMethod(request.Method) && !call.IsWrite);
        Assert.Empty(store.WriteCalls);
        AssertSameRecords(before, store.GetRecords(request.ViewId));
    }

    // A definitive no-write result ends the started write and is recorded as NotWritten, so an unclassified failure
    // after it is notWritten; a started write without one stays unknown; a new write call clears the no-write result;
    // a commit clears it too; and a commit never reverts.
    [Fact]
    public void NotWrittenEndsAStartedWriteButNeverUndoesACommit()
    {
        var failure = new InvalidOperationException(StoreFailureText);

        var started = new WriteOutcomeFeature();
        started.MarkWriteStarted();
        Assert.False(started.NotWritten);
        Assert.Equal(WriteOutcome.Unknown, ApiExceptionHandler.ResolveWriteOutcome(started, failure));

        started.MarkNotWritten();
        Assert.False(started.WriteStarted);
        Assert.False(started.Written);
        Assert.True(started.NotWritten);
        Assert.Equal(WriteOutcome.NotWritten, ApiExceptionHandler.ResolveWriteOutcome(started, failure));

        started.MarkWriteStarted();
        Assert.True(started.WriteStarted);
        Assert.False(started.NotWritten);
        Assert.Equal(WriteOutcome.Unknown, ApiExceptionHandler.ResolveWriteOutcome(started, failure));

        started.MarkNotWritten();
        started.MarkWritten();
        Assert.True(started.Written);
        Assert.False(started.NotWritten);
        Assert.Equal(WriteOutcome.Written, ApiExceptionHandler.ResolveWriteOutcome(started, failure));

        var committed = new WriteOutcomeFeature();
        committed.MarkWritten();
        committed.MarkNotWritten();
        Assert.True(committed.Written);
        Assert.True(committed.WriteStarted);
        Assert.False(committed.NotWritten);
        Assert.Equal(WriteOutcome.Written, ApiExceptionHandler.ResolveWriteOutcome(committed, failure));
    }

    // A two-page store and a write the store commits: a POST of a new record, or a PUT or DELETE of a seeded active
    // one.
    private static (FakeDataStore Store, WriteRequest Request) ArrangeCommittableWrite(string method)
    {
        var store = new FakeDataStore(FakeForms.TwoPage);
        switch (method)
        {
            case Post:
                return (store, new WriteRequest(Post, TwoPageViewId, null, new { values = CreatedValues() }));

            case Put:
                var updated = store.SeedRecord(TwoPageViewId, SeededValues());
                return (store, new WriteRequest(Put, TwoPageViewId, updated, new { values = UpdatedValues() }));

            case Delete:
                var deleted = store.SeedRecord(TwoPageViewId, SeededValues());
                return (store, new WriteRequest(Delete, TwoPageViewId, deleted, null));

            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Not a record write.");
        }
    }

    // A store and a write that passes every check before the store call, for which the store returns a result that
    // wrote nothing. Each store also holds a record the write must leave alone, so "unchanged" is never vacuous.
    private static (FakeDataStore Store, WriteRequest Request) ArrangeNoWrite(string scenario)
    {
        if (scenario == PutFailingRequiredGate)
        {
            // The stored required field is null and the PUT does not submit it, so only the store's gate, run on the
            // stored record overlaid with the submitted values, can reject the save.
            var requiredStore = new FakeDataStore(FakeForms.WithRequiredField);
            var incomplete = requiredStore.SeedRecord(
                FakeForms.RequiredViewId,
                new Dictionary<string, FieldValue> { [Comment] = FieldValue.FromText(SeededComment) });
            var values = new Dictionary<string, object?> { [Comment] = UpdatedComment };
            return (requiredStore, new WriteRequest(Put, FakeForms.RequiredViewId, incomplete, new { values }));
        }

        var form = FakeForms.TwoPage;
        var store = new FakeDataStore(form);
        var update = new { values = UpdatedValues() };
        switch (scenario)
        {
            case PutMissingRecord:
                store.SeedRecord(TwoPageViewId, SeededValues());
                return (store, new WriteRequest(Put, TwoPageViewId, MissingId(), update));

            case PutDeletedRecord:
                var deletedForPut = store.SeedRecord(TwoPageViewId, SeededValues(), RecordQueryRules.DeletedRecStatus);
                return (store, new WriteRequest(Put, TwoPageViewId, deletedForPut, update));

            case PutDeniedMissingPageInsert:
                // UPDATE everywhere passes the service's check; the record's page-two row is missing and INSERT on that
                // page table is denied, which only the store's locked read can find.
                var pages = form.Metadata.Pages;
                var partial = store.SeedRecord(TwoPageViewId, SeededValues(), missingPageIds: [pages[1].PageId]);
                store.SetPermissions(
                    TwoPageViewId,
                    new FormPermissions(
                    [
                        new TablePermission(form.Metadata.BaseTableName, CanInsert: true, CanUpdate: true),
                        new TablePermission(pages[0].TableName, CanInsert: true, CanUpdate: true),
                        new TablePermission(pages[1].TableName, CanInsert: false, CanUpdate: true),
                    ]));
                return (store, new WriteRequest(Put, TwoPageViewId, partial, update));

            case DeleteMissingRecord:
                store.SeedRecord(TwoPageViewId, SeededValues());
                return (store, new WriteRequest(Delete, TwoPageViewId, MissingId(), null));

            case DeleteDeletedRecord:
                var deleted = store.SeedRecord(TwoPageViewId, SeededValues(), RecordQueryRules.DeletedRecStatus);
                return (store, new WriteRequest(Delete, TwoPageViewId, deleted, null));

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown no-write scenario.");
        }
    }

    // The change each committed write makes, stamped with the controlled clock: a new active record holding the
    // submitted values with both stamps; the submitted value and the last-save stamp; or RecStatus 0 and the last-save
    // stamp. Nothing else may differ.
    private static void AssertCommitted(
        FakeDataStore store,
        WriteRequest request,
        IReadOnlyList<FakeRecordState> before)
    {
        var stamp = new SaveStamp(Environment.UserName, WriteTime);
        var after = store.GetRecords(request.ViewId);
        switch (request.Method)
        {
            case Post:
                Assert.Empty(before);
                var created = Assert.Single(after);
                var allPages = FakeForms.TwoPage.Metadata.Pages.Select(page => page.PageId).ToHashSet();
                var expectedValues = new Dictionary<string, FieldValue>(StringComparer.OrdinalIgnoreCase)
                {
                    [LastName] = FieldValue.FromText(CreatedLastName),
                    [Age] = FieldValue.FromNumber(CreatedAge),
                    [Sex] = FieldValue.Null,
                    [Ill] = FieldValue.FromBoolean(true),
                    [Notes] = FieldValue.FromText(CreatedNotes),
                };
                AssertSameRecord(
                    new FakeRecordState(
                        created.GlobalRecordId,
                        created.UniqueKey,
                        RecordQueryRules.ActiveRecStatus,
                        expectedValues,
                        allPages,
                        stamp,
                        stamp),
                    created);
                break;

            case Put:
                var original = Assert.Single(before);
                var updatedValues = new Dictionary<string, FieldValue>(
                    original.Values,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [LastName] = FieldValue.FromText(UpdatedLastName),
                };
                AssertSameRecord(original with { Values = updatedValues, LastSave = stamp }, Assert.Single(after));
                break;

            case Delete:
                var active = Assert.Single(before);
                AssertSameRecord(
                    active with { RecStatus = RecordQueryRules.DeletedRecStatus, LastSave = stamp },
                    Assert.Single(after));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Method, "Not a record write.");
        }
    }

    // FakeRecordState holds dictionaries and sets, which records compare by reference, so the comparison is per field.
    private static void AssertSameRecords(
        IReadOnlyList<FakeRecordState> expected,
        IReadOnlyList<FakeRecordState> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertSameRecord(expected[index], actual[index]);
        }
    }

    private static void AssertSameRecord(FakeRecordState expected, FakeRecordState? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.GlobalRecordId, actual.GlobalRecordId);
        Assert.Equal(expected.UniqueKey, actual.UniqueKey);
        Assert.Equal(expected.RecStatus, actual.RecStatus);
        Assert.Equal(expected.Values.Count, actual.Values.Count);
        foreach (var (name, value) in expected.Values)
        {
            Assert.True(actual.Values.TryGetValue(name, out var stored), $"The record holds no value for '{name}'.");
            Assert.Equal(value, stored);
        }

        Assert.Equal(expected.PresentPageIds.Order(), actual.PresentPageIds.Order());
        Assert.Equal(expected.FirstSave, actual.FirstSave);
        Assert.Equal(expected.LastSave, actual.LastSave);
    }

    // The 500 of a record write: fixed ProblemDetails whose detail is the generic sentence, which may be followed by
    // the trace id (AAP 0.3.4.2), and the expected writeOutcome.
    private static async Task AssertWriteProblemAsync(HttpResponseMessage response, string expectedOutcome)
    {
        var body = await ReadProblemAsync(response, StatusCodes.Status500InternalServerError);
        Assert.StartsWith(
            ErrorContractTests.ExpectedUnexpectedSentence,
            body.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.True(
            body.TryGetProperty(WriteOutcomeMember, out var outcome),
            $"The write failure carries no {WriteOutcomeMember}: {body.GetRawText()}");
        Assert.Equal(expectedOutcome, outcome.GetString());
    }

    // A 4xx of a record write: fixed ProblemDetails with its own detail and no writeOutcome, which only a 500 or 503
    // of a write carries.
    private static async Task AssertPlainProblemAsync(HttpResponseMessage response, int status, string detail)
    {
        var body = await ReadProblemAsync(response, status);
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(
            body.TryGetProperty(WriteOutcomeMember, out _),
            $"Unexpected {WriteOutcomeMember}: {body.GetRawText()}");
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(ErrorContractTests.ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
        return ApiTestClient.AssertProblemDetails(await ApiTestClient.ReadJsonElementAsync(response), status);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, WriteRequest request) => request.Method switch
    {
        Post => ApiTestClient.PostJsonAsync(client, request.Path, request.Body),
        Put => ApiTestClient.PutJsonAsync(client, request.Path, request.Body),
        Delete => client.DeleteAsync(request.Path),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Method, "Not a record write."),
    };

    // The IDataStore member each record write ends in.
    private static string StoreWriteMethod(string method) => method switch
    {
        Post => nameof(IDataStore.InsertAsync),
        Put => nameof(IDataStore.UpdateAsync),
        Delete => nameof(IDataStore.SoftDeleteAsync),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Not a record write."),
    };

    private static string RecordsPath(int viewId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/forms/{viewId}/records");

    private static string RecordPath(int viewId, string globalRecordId) => RecordsPath(viewId) + "/" + globalRecordId;

    // The format the stores write, for an id no store holds.
    private static string MissingId() => Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> CreatedValues() => new()
    {
        [LastName] = CreatedLastName,
        [Age] = CreatedAge,
        [Ill] = true,
        [Notes] = CreatedNotes,
    };

    private static Dictionary<string, FieldValue> SeededValues() => new()
    {
        [LastName] = FieldValue.FromText(SeededLastName),
        [Age] = FieldValue.FromNumber(SeededAge),
        [Notes] = FieldValue.FromText(SeededNotes),
    };

    private static Dictionary<string, object?> UpdatedValues() => new() { [LastName] = UpdatedLastName };

    /// <summary>One record write as the test sends it.</summary>
    /// <param name="Method">The HTTP method: <c>POST</c>, <c>PUT</c> or <c>DELETE</c>.</param>
    /// <param name="ViewId">The form's view id.</param>
    /// <param name="GlobalRecordId">
    /// The record a <c>PUT</c> or <c>DELETE</c> names; <see langword="null"/> for a <c>POST</c>.
    /// </param>
    /// <param name="Body">The JSON body; <see langword="null"/> for a <c>DELETE</c>, which sends none.</param>
    private sealed record WriteRequest(string Method, int ViewId, string? GlobalRecordId, object? Body)
    {
        /// <summary>The records path for a <c>POST</c>, or the record's own path.</summary>
        public string Path => GlobalRecordId is null ? RecordsPath(ViewId) : RecordPath(ViewId, GlobalRecordId);
    }

    /// <summary>
    /// An independent TestServer host built for these cases: the record endpoints and the services behind them over a
    /// <see cref="FakeDataStore"/>, with a <see cref="PostResultFailureSeam"/> in the pipeline. It is not the API as
    /// <c>Program.cs</c> composes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It registers ProblemDetails customised by <see cref="ApiProblems.Customize"/>,
    /// <see cref="ApiExceptionHandler"/>, the API's JSON settings (<see cref="JsonSetup.AddApiJson"/>),
    /// <c>ThrowOnBadRequest</c>, the store, a <see cref="CheckCodeEngine"/>, a <see cref="ControlledTimeProvider"/> as
    /// the clock, <see cref="SaveStampProvider"/>, <see cref="FormService"/> and <see cref="RecordService"/>. Its
    /// pipeline is the exception handler, the default status-code pages, routing, the seam, the JSON media-type guard
    /// (<see cref="JsonContentTypeMiddleware"/>) and the record endpoints.
    /// </para>
    /// <para>
    /// Everything else <c>Program.cs</c> adds is left out, among it the listen-setting and request <c>Host</c> checks
    /// of <see cref="LoopbackBindingGuard"/>, the JSON console and the floors of <see cref="ProtectedLogLevels"/>,
    /// <see cref="ApiExceptionBoundaryMiddleware"/>, the status-code page writer
    /// <see cref="ApiProblems.WriteStatusCodePageAsync"/>, <see cref="RouteConstraintMatcherPolicy"/>,
    /// <see cref="RequestBodyLimitMiddleware"/>, the form, field-event, health and OpenAPI endpoints, and
    /// <see cref="ClientAppHosting"/>. Record writes through the API as <c>Program.cs</c> composes it are proved on
    /// <see cref="ApiFactory"/>: their store failures and write outcomes in <see cref="ErrorContractTests"/>, and
    /// writes in a process without an OS user name in <see cref="StoreFailureWriteOutcomeTests"/>.
    /// </para>
    /// <para>
    /// The host runs in the Production environment with <c>src/EpiInfo.Api</c> as its content root, so the committed
    /// <c>appsettings.json</c> logging filters apply, and its only logger providers are a
    /// <see cref="CapturingLoggerProvider"/> and, when given, one more. The seam sits after routing, so its failure is
    /// raised inside the matched write endpoint's request.
    /// </para>
    /// </remarks>
    private sealed class RecordWriteHost : IAsyncDisposable
    {
        private readonly WebApplication app;

        private RecordWriteHost(
            WebApplication app,
            ControlledTimeProvider clock,
            PostResultFailureSeam seam,
            CapturingLoggerProvider logs)
        {
            this.app = app;
            Clock = clock;
            Seam = seam;
            Logs = logs;
            Client = app.GetTestClient();
        }

        /// <summary>The client of the started host.</summary>
        public HttpClient Client { get; }

        /// <summary>The clock the save stamps are read from; set it before each write.</summary>
        public ControlledTimeProvider Clock { get; }

        /// <summary>The one-shot post-result failure; arm it before the request it should fail.</summary>
        public PostResultFailureSeam Seam { get; }

        /// <summary>Every log entry the committed filters let through.</summary>
        public CapturingLoggerProvider Logs { get; }

        /// <summary>Builds and starts a host over <paramref name="store"/>.</summary>
        /// <param name="store">The store the services use.</param>
        /// <param name="extraLoggerProvider">One more logger provider, or <see langword="null"/>.</param>
        public static async Task<RecordWriteHost> StartAsync(
            FakeDataStore store,
            ILoggerProvider? extraLoggerProvider = null)
        {
            ArgumentNullException.ThrowIfNull(store);

            var clock = new ControlledTimeProvider();
            var seam = new PostResultFailureSeam();
            var logs = new CapturingLoggerProvider();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production,
                ContentRootPath = RepositoryPaths.ApiProjectDirectory,
            });
            builder.WebHost.UseTestServer();

            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            if (extraLoggerProvider is not null)
            {
                builder.Logging.AddProvider(extraLoggerProvider);
            }

            var services = builder.Services;
            services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.Customize);
            services.AddExceptionHandler<ApiExceptionHandler>();
            services.AddApiJson();
            services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
            services.AddSingleton<IDataStore>(store);
            services.AddSingleton<ICheckCodeEngine>(new CheckCodeEngine());
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<SaveStampProvider>();
            services.AddScoped<FormService>();
            services.AddScoped<RecordService>();

            var app = builder.Build();
            try
            {
                app.UseExceptionHandler();
                app.UseStatusCodePages();
                app.UseRouting();
                app.Use(seam.InvokeAsync);
                app.UseJsonContentTypeGuard();
                app.MapRecordEndpoints();
                await app.StartAsync();
                return new RecordWriteHost(app, clock, seam, logs);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Middleware that, once armed, fails the next request after its endpoint has chosen a result and before the
    /// response starts, as a failing serialisation or delivery would. Disarmed, it only passes the request on.
    /// </summary>
    /// <remarks>
    /// The armed request writes to a replacement body whose every write and flush throws
    /// <see cref="InjectedResponseFailureException"/>. The replacement has no prior feature, so starting it never
    /// starts the real response. The real body is restored before the failure leaves the seam, so the exception
    /// handler writes its 500 to it. A result that writes no body, such as 204, is failed after it returns, while the
    /// response has still not started.
    /// </remarks>
    private sealed class PostResultFailureSeam
    {
        private int armed;
        private int fired;

        /// <summary>Whether the seam has raised its failure.</summary>
        public bool Fired => Volatile.Read(ref fired) == 1;

        /// <summary>Arms the seam for the next request only.</summary>
        public void Arm() => Interlocked.Exchange(ref armed, 1);

        /// <summary>The middleware body.</summary>
        /// <param name="context">The current request.</param>
        /// <param name="next">The rest of the pipeline.</param>
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
            {
                await next(context);
                return;
            }

            var original = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new FailingBodyStream(this)));
            try
            {
                await next(context);
            }
            finally
            {
                context.Features.Set(original);
            }

            if (!context.Response.HasStarted)
            {
                throw Raise();
            }
        }

        /// <summary>Records that the failure was raised and returns it.</summary>
        public InjectedResponseFailureException Raise()
        {
            Interlocked.Exchange(ref fired, 1);
            return new InjectedResponseFailureException();
        }
    }

    /// <summary>A write-only response body whose every write and flush raises the seam's failure.</summary>
    /// <param name="seam">The seam that records the failure.</param>
    private sealed class FailingBodyStream(PostResultFailureSeam seam) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw seam.Raise();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(seam.Raise());

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw seam.Raise();

        public override void Write(ReadOnlySpan<byte> buffer) => throw seam.Raise();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(seam.Raise());

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(seam.Raise());
    }

    /// <summary>
    /// The failure the seam injects. It is neither an <see cref="IOException"/> nor an
    /// <see cref="OperationCanceledException"/>, so the exception handler never takes it for a client abort.
    /// </summary>
    private sealed class InjectedResponseFailureException()
        : Exception("Response failure injected by RecordWriteOutcomeTests.");

    /// <summary>
    /// A logger provider whose loggers for the <see cref="RecordService"/> category are enabled at every level and
    /// throw from every log call, counting each throw; every other category gets a logger that writes nothing.
    /// </summary>
    private sealed class ThrowingAuditLoggerProvider : ILoggerProvider
    {
        private static readonly string AuditCategory = typeof(RecordService).FullName
            ?? throw new InvalidOperationException("RecordService has no full type name.");

        private int throws;
        private int disposed;

        /// <summary>How many log calls have thrown.</summary>
        public int Throws => Volatile.Read(ref throws);

        /// <inheritdoc />
        /// <exception cref="ObjectDisposedException">The provider has been disposed.</exception>
        public ILogger CreateLogger(string categoryName)
        {
            ArgumentNullException.ThrowIfNull(categoryName);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
            return string.Equals(categoryName, AuditCategory, StringComparison.Ordinal)
                ? new ThrowingLogger(this)
                : NullLogger.Instance;
        }

        /// <summary>Marks the provider disposed; <see cref="Throws"/> stays readable.</summary>
        public void Dispose() => Volatile.Write(ref disposed, 1);

        private sealed class ThrowingLogger(ThrowingAuditLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Interlocked.Increment(ref provider.throws);
                throw new InvalidOperationException("Audit log failure injected by RecordWriteOutcomeTests.");
            }
        }
    }
}

/// <summary>What <see cref="ErrorPipelineHost.StartAsync"/> builds.</summary>
internal sealed record ErrorPipelineHostOptions
{
    /// <summary>The host environment, which selects the committed <c>appsettings.{Environment}.json</c>.</summary>
    public string EnvironmentName { get; init; } = "Production";

    /// <summary>
    /// <see langword="true"/> to serve on real Kestrel at <c>http://127.0.0.1:0</c>; otherwise TestServer, which
    /// applies no Kestrel limit and no HTTP/1.1 framing.
    /// </summary>
    public bool UseKestrel { get; init; }

    /// <summary>The store the services use; <see cref="FakeDataStore.WithStandardForms"/> when not set.</summary>
    public FakeDataStore? Store { get; init; }

    /// <summary>
    /// Runs after the API's own JSON settings on the minimal-API serializer options, for example to add a converter
    /// whose failure stands in for a response-serialization failure.
    /// </summary>
    public Action<JsonSerializerOptions>? ConfigureJson { get; init; }

    /// <summary>Maps extra endpoints after the API's own, for failures no API endpoint can be made to raise.</summary>
    public Action<IEndpointRouteBuilder>? MapEndpoints { get; init; }
}

/// <summary>
/// A host of the API's error pipeline for the tests that need a hook the real composition cannot be given, a failing
/// JSON converter or an endpoint the API does not have, and no database: the real <see cref="ApiExceptionHandler"/>,
/// its <see cref="ApiExceptionBoundaryMiddleware"/>, <see cref="ApiProblems.Customize"/>, the status-code pages of
/// <see cref="ApiProblems.WriteStatusCodePageAsync"/>, the request <c>Host</c> guard, the request guards, endpoints and
/// services, composed in the order <c>Program.cs</c> uses, over a <see cref="FakeDataStore"/>,
/// with the committed configuration of the API project and its logging as <c>Program.cs</c> composes it: the JSON
/// console, with a <see cref="CapturingLoggerProvider"/> added beside it as one more sink.
/// </summary>
/// <remarks>
/// <para>
/// The content root is <c>src/EpiInfo.Api</c>, so <c>appsettings.json</c> and <c>appsettings.{Environment}.json</c>
/// supply the <c>Logging:LogLevel</c> filters, the body limit and the listen endpoint. Those committed filters, held to
/// the protected framework floors of <see cref="ProtectedLogLevels"/> as <c>Program.cs</c> holds them, govern the JSON
/// console and the capture provider alike; the host changes no level, filter or console option beyond what
/// <c>Program.cs</c> sets, and the provider has no filter of its own, so it captures exactly what production emits.
/// </para>
/// <para>
/// The listener is held to the binding rule as <c>Program.cs</c> holds the API's. The committed endpoint is
/// overridden to <c>http://127.0.0.1:0</c>, so a Kestrel host never binds the committed port, and
/// <see cref="LoopbackBindingGuard.Validate"/> checks every effective listen setting before the host is built and
/// again on the built configuration, on TestServer and on Kestrel. On Kestrel the server loads the fixed copy of the
/// verified <c>Kestrel</c> section that <see cref="LoopbackBindingGuard.FreezeListenSettings"/> returns, with reload
/// off, and applies the committed body cap. Once started, the address the server reports is checked without the
/// guard's predicate: exactly one, an <c>http</c> URL whose host parses as the IPv4 <see cref="IPAddress.Loopback"/>,
/// on a port other than 0. A host that fails to build, start or prove its address is disposed before the failure
/// reaches the test.
/// </para>
/// <para>Each host is independent: create one per test and dispose it, so captured logs never mix.</para>
/// </remarks>
internal sealed class ErrorPipelineHost : IAsyncDisposable
{
    private const string KestrelUrlKey = "Kestrel:Endpoints:Http:Url";

    private const string EphemeralLoopbackUrl = "http://127.0.0.1:0";

    private ErrorPipelineHost(WebApplication app, CapturingLoggerProvider logs, FakeDataStore store, Uri? address)
    {
        App = app;
        Logs = logs;
        Store = store;
        Address = address;
    }

    /// <summary>The running application.</summary>
    public WebApplication App { get; }

    /// <summary>Every log entry the committed filters let through.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>The store behind every endpoint.</summary>
    public FakeDataStore Store { get; }

    /// <summary>The Kestrel listen address, or <see langword="null"/> on TestServer.</summary>
    public Uri? Address { get; }

    /// <summary>Builds and starts the host.</summary>
    /// <param name="options">What to build; the defaults when <see langword="null"/>.</param>
    /// <returns>The started host.</returns>
    public static async Task<ErrorPipelineHost> StartAsync(ErrorPipelineHostOptions? options = null)
    {
        options ??= new ErrorPipelineHostOptions();
        var store = options.Store ?? FakeDataStore.WithStandardForms();
        var logs = new CapturingLoggerProvider();
        WebApplication? app = null;
        var started = false;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = RepositoryPaths.ApiProjectDirectory,
                EnvironmentName = options.EnvironmentName,
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KestrelUrlKey] = EphemeralLoopbackUrl,
            });

            // As Program.cs does, on either server: every effective listen setting, from the committed files, the
            // process environment and the override above alike, is checked before the host is built.
            LoopbackBindingGuard.Validate(builder.Configuration);

            if (options.UseKestrel)
            {
                // As Program.cs does: Kestrel loads the guard's frozen copy of the verified Kestrel section with reload
                // off, the host's urls and ports keys are pinned to that copy, and, because loading the section binds
                // endpoints and not limits, the body cap is applied from configuration here.
                builder.WebHost.ConfigureKestrel((context, kestrel) =>
                {
                    kestrel.Configure(
                        LoopbackBindingGuard.FreezeListenSettings(context.Configuration),
                        reloadOnChange: false);
                    kestrel.Limits.MaxRequestBodySize =
                        ApiOptions.FromConfiguration(context.Configuration).MaxRequestBodySize;
                });
            }
            else
            {
                builder.WebHost.UseTestServer();
            }

            // As Program.cs does, with the capture provider added beside the JSON console rather than in its place: the
            // console's scopes, UTC timestamp and unindented writer are fixed, and the protected framework floors hold
            // over every configured rule, so the capture provider sees the levels production applies.
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(console =>
            {
                console.IncludeScopes = false;
                console.TimestampFormat = "O";
                console.UseUtcTimestamp = true;
                console.JsonWriterOptions = console.JsonWriterOptions with { Indented = false };
            });
            builder.Logging.AddProvider(logs);

            var services = builder.Services;
            services.PostConfigure<LoggerFilterOptions>(ProtectedLogLevels.Apply);
            services.AddSingleton(
                provider => ApiOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
            services.AddProblemDetails(problem => problem.CustomizeProblemDetails = ApiProblems.Customize);
            services.AddExceptionHandler<ApiExceptionHandler>();
            services.Configure<RouteHandlerOptions>(routing => routing.ThrowOnBadRequest = true);
            services.AddApiJson();
            if (options.ConfigureJson is { } configureJson)
            {
                services.ConfigureHttpJsonOptions(json => configureJson(json.SerializerOptions));
            }

            services.AddSingleton<IDataStore>(store);
            services.AddSingleton<ICheckCodeEngine, CheckCodeEngine>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<SaveStampProvider>();
            services.AddScoped<FormService>();
            services.AddScoped<RecordService>();
            services.AddScoped<FieldEventService>();

            app = builder.Build();

            // As Program.cs does: checked again on the built configuration, which holds the sources added during Build.
            LoopbackBindingGuard.Validate(app.Configuration);

            app.UseExceptionHandler();
            app.UseApiExceptionBoundary();
            app.UseStatusCodePages(ApiProblems.WriteStatusCodePageAsync);
            app.UseLoopbackHostGuard();
            app.UseRouting();
            app.UseRequestBodyLimit();
            app.UseJsonContentTypeGuard();

            app.MapFormEndpoints();
            app.MapRecordEndpoints();
            app.MapCheckCodeEndpoints();
            options.MapEndpoints?.Invoke(app);

            await app.StartAsync();
            started = true;

            var address = options.UseKestrel ? VerifyLoopbackAddress(app) : null;
            return new ErrorPipelineHost(app, logs, store, address);
        }
        catch
        {
            // A host that fails to build, start or prove its address is torn down before the failure reaches the test.
            if (app is not null)
            {
                if (started)
                {
                    await app.StopAsync();
                }

                await app.DisposeAsync();
            }

            logs.Dispose();
            throw;
        }
    }

    /// <summary>A client for the host: TestServer's own, or one addressed to the Kestrel listener.</summary>
    /// <returns>A client the caller disposes.</returns>
    public HttpClient CreateClient() =>
        Address is null ? App.GetTestClient() : new HttpClient { BaseAddress = Address };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Logs.Dispose();
    }

    // The proof of the binding, made on the server's own report and without the guard's predicate: exactly one
    // address, an http URL whose host parses as the IPv4 loopback address, on the port the operating system chose.
    private static Uri VerifyLoopbackAddress(WebApplication app)
    {
        var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = new Uri(Assert.Single(feature?.Addresses ?? []), UriKind.Absolute);
        Assert.True(
            string.Equals(Uri.UriSchemeHttp, address.Scheme, StringComparison.Ordinal),
            "The Kestrel listener is not plain http.");
        Assert.True(
            IPAddress.TryParse(address.Host, out var host)
            && host.AddressFamily == AddressFamily.InterNetwork
            && host.Equals(IPAddress.Loopback),
            "The Kestrel listener is not bound to the IPv4 loopback address 127.0.0.1.");
        Assert.True(address.Port > 0, "The Kestrel listener reports no bound port.");
        return address;
    }
}

/// <summary>A raw HTTP/1.1 exchange with a Kestrel listener.</summary>
/// <param name="Response">Every byte the server sent, as text.</param>
/// <param name="ConnectionReset">Whether the connection ended with a reset or I/O failure.</param>
internal sealed record RawExchange(string Response, bool ConnectionReset);

/// <summary>
/// The write outcome of a record write whose store failure reports its own outcome agrees with the stored data and
/// the store's write log, proved through <see cref="RecordService"/> and <see cref="ApiExceptionHandler"/> over
/// <see cref="FakeDataStore"/>; and a write the save stamp refuses, because the process has no OS user name, is
/// <c>notWritten</c> with nothing stored, proved through <see cref="SaveStampProvider"/> and the API's own pipeline.
/// </summary>
/// <remarks>
/// Each case asserts the error response, the stored record and the store's write log together. The class needs no
/// database: it carries no <c>Integration</c> trait, joins no database collection and takes no database lock.
/// </remarks>
public sealed class StoreFailureWriteOutcomeTests
{
    private const string Insert = nameof(IDataStore.InsertAsync);
    private const string Update = nameof(IDataStore.UpdateAsync);
    private const string SoftDelete = nameof(IDataStore.SoftDeleteAsync);

    private const string Unavailable = nameof(FakeDataStore.Unavailable);
    private const string WriteFailure = nameof(FakeDataStore.WriteFailure);

    private const string OriginalLastName = "Original";
    private const string ChangedLastName = "Changed";

    // The refusal of a stamp for a process without a user name, written out here rather than read from the product so
    // a changed text fails these cases.
    private const string NoUserNameMessage =
        "The API process has no OS user name, so the save stamp cannot name who saved; nothing was written.";

    private static readonly string[] WriteMethods = [Insert, Update, SoftDelete];

    // Each store failure kind with the status the handler gives it.
    private static readonly (string Kind, int Status)[] FailureKinds =
    [
        (Unavailable, StatusCodes.Status503ServiceUnavailable),
        (WriteFailure, StatusCodes.Status500InternalServerError),
    ];

    /// <summary>
    /// Every write method and failure kind with each outcome at a stage whose state agrees with it. An unknown outcome
    /// appears at both stages, because it covers both states: the change lost and the change kept.
    /// </summary>
    public static TheoryData<string, string, int, WriteOutcome, FakeFailureStage> CoherentFailures()
    {
        var data = new TheoryData<string, string, int, WriteOutcome, FakeFailureStage>();
        foreach (var method in WriteMethods)
        {
            foreach (var (kind, status) in FailureKinds)
            {
                data.Add(method, kind, status, WriteOutcome.NotWritten, FakeFailureStage.BeforeWrite);
                data.Add(method, kind, status, WriteOutcome.Written, FakeFailureStage.AfterWrite);
                data.Add(method, kind, status, WriteOutcome.Unknown, FakeFailureStage.BeforeWrite);
                data.Add(method, kind, status, WriteOutcome.Unknown, FakeFailureStage.AfterWrite);
            }
        }

        return data;
    }

    /// <summary>
    /// Every write method and failure kind with each outcome its injection point's state contradicts: written before
    /// the write, not written after it, and written through <see cref="FakeDataStore.ThrowOnAny"/>, which raises
    /// before anything runs. A <see langword="null"/> stage stands for <see cref="FakeDataStore.ThrowOnAny"/>.
    /// </summary>
    public static TheoryData<string, string, WriteOutcome, FakeFailureStage?> IncoherentFailures()
    {
        var data = new TheoryData<string, string, WriteOutcome, FakeFailureStage?>();
        foreach (var method in WriteMethods)
        {
            foreach (var (kind, _) in FailureKinds)
            {
                data.Add(method, kind, WriteOutcome.Written, FakeFailureStage.BeforeWrite);
                data.Add(method, kind, WriteOutcome.NotWritten, FakeFailureStage.AfterWrite);
                data.Add(method, kind, WriteOutcome.Written, null);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CoherentFailures))]
    public async Task FailedWriteReportsTheOutcomeTheStoredDataAndWriteLogShow(
        string method,
        string kind,
        int status,
        WriteOutcome outcome,
        FakeFailureStage stage)
    {
        var (store, records, id) = CreateServices();
        var injected = Failure(kind, outcome);
        store.ThrowOn(method, injected, stage);

        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var body = new MemoryStream();
        var context = WriteContext(method, services, body);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => WriteAsync(records, context, method, id));
        Assert.Same(injected, thrown);

        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);
        Assert.True(await handler.TryHandleAsync(context, thrown, CancellationToken.None));

        // The response: the failure's status, a problem body, and the outcome the injected failure reports.
        Assert.Equal(status, context.Response.StatusCode);
        var contentType = context.Response.ContentType;
        Assert.NotNull(contentType);
        Assert.Equal("application/problem+json", MediaTypeHeaderValue.Parse(contentType).MediaType);
        body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(body);
        Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());
        var writeOutcome = problem.RootElement.GetProperty("writeOutcome");
        Assert.Equal(JsonValueKind.String, writeOutcome.ValueKind);
        Assert.Equal(WireValue(outcome), writeOutcome.GetString());
        Assert.Equal(outcome.ToWireValue(), writeOutcome.GetString());

        // The write log: one data-changing call once the change is kept, none before it.
        var applied = stage == FakeFailureStage.AfterWrite;
        if (applied)
        {
            Assert.Equal(method, Assert.Single(store.WriteCalls).Method);
        }
        else
        {
            Assert.Empty(store.WriteCalls);
        }

        // The stored data: exactly the change the write method makes once it is kept, and nothing before it.
        var inserted = applied && method == Insert;
        var updated = applied && method == Update;
        var deleted = applied && method == SoftDelete;

        var stored = store.GetRecords(FakeForms.TwoPageViewId);
        Assert.Equal(inserted ? 2 : 1, stored.Count);
        var seeded = Assert.Single(stored, record => record.GlobalRecordId == id);
        Assert.Equal(updated ? ChangedLastName : OriginalLastName, seeded.Values["LastName"].AsText());
        Assert.Equal(deleted ? RecordQueryRules.DeletedRecStatus : RecordQueryRules.ActiveRecStatus, seeded.RecStatus);
        Assert.Null(seeded.FirstSave);
        if (updated || deleted)
        {
            Assert.NotNull(seeded.LastSave);
        }
        else
        {
            Assert.Null(seeded.LastSave);
        }

        if (inserted)
        {
            var created = Assert.Single(stored, record => record.GlobalRecordId != id);
            Assert.Equal(ChangedLastName, created.Values["LastName"].AsText());
            Assert.Equal(RecordQueryRules.ActiveRecStatus, created.RecStatus);
            Assert.NotNull(created.FirstSave);
            Assert.NotNull(created.LastSave);
        }
    }

    [Theory]
    [MemberData(nameof(IncoherentFailures))]
    public async Task IncoherentFailureIsRejectedAndSetsNoFailure(
        string method,
        string kind,
        WriteOutcome outcome,
        FakeFailureStage? stage)
    {
        var (store, records, id) = CreateServices();
        var injected = Failure(kind, outcome);

        var rejected = Assert.Throws<ArgumentException>(() =>
        {
            if (stage is { } injectedAt)
            {
                store.ThrowOn(method, injected, injectedAt);
            }
            else
            {
                store.ThrowOnAny(injected);
            }
        });
        Assert.Equal("exception", rejected.ParamName);

        // No failure was set, so the same write succeeds and logs its one data-changing call.
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var body = new MemoryStream();
        var context = WriteContext(method, services, body);
        var result = await WriteAsync(records, context, method, id);

        var expectedStatus = method switch
        {
            Insert => StatusCodes.Status201Created,
            Update => StatusCodes.Status200OK,
            _ => StatusCodes.Status204NoContent,
        };
        Assert.Equal(expectedStatus, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(method, Assert.Single(store.WriteCalls).Method);
    }

    /// <summary>
    /// Each hosting environment, which selects the committed logging configuration, with each write method.
    /// </summary>
    public static TheoryData<string, string> EnvironmentsAndWriteMethods()
    {
        var data = new TheoryData<string, string>();
        foreach (var environment in new[] { Environments.Production, Environments.Development })
        {
            foreach (var method in WriteMethods)
            {
                data.Add(environment, method);
            }
        }

        return data;
    }

    // AAP 0.3.4.6 and A11: a user name that is missing, empty or whitespace names nobody, so no stamp is made, and the
    // refusal is the one fixed sentence, which carries no value.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void StampForAProcessWithoutAUserNameIsRefused(string? userName)
    {
        var provider = new SaveStampProvider(new ControlledTimeProvider(), () => userName);

        var refused = Assert.Throws<InvalidOperationException>(() => provider.Create());

        Assert.Equal(NoUserNameMessage, refused.Message);
    }

    // AAP 0.3.4.6: the stamp names the user the source gives, read again for each stamp, at the clock's local time,
    // never UTC, cut to whole seconds with its Unspecified kind kept. The public constructor, the one dependency
    // injection uses, names Environment.UserName of the process (A11).
    [Fact]
    public void StampNamesTheProcessUserAtTheClocksLocalTimeInWholeSeconds()
    {
        // Two hours east of UTC, so a stamp taken in UTC would be two hours early.
        var zone = TimeZoneInfo.CreateCustomTimeZone("Stamp+02", TimeSpan.FromHours(2), "Stamp+02", "Stamp+02");
        var clock = new ControlledTimeProvider(
            zone,
            new DateTime(2024, 6, 3, 10, 15, 30, 789, DateTimeKind.Unspecified));
        var expectedTime = new DateTime(2024, 6, 3, 10, 15, 30, DateTimeKind.Unspecified);
        var names = new Queue<string?>(["first-user", "second-user"]);
        var provider = new SaveStampProvider(clock, names.Dequeue);

        var first = provider.Create();
        var second = provider.Create();
        var process = new SaveStampProvider(clock).Create();

        Assert.Equal("first-user", first.LogonName);
        Assert.Equal("second-user", second.LogonName);
        Assert.Equal(Environment.UserName, process.LogonName);
        foreach (var stamp in new[] { first, second, process })
        {
            Assert.Equal(expectedTime, stamp.Time);
            Assert.Equal(DateTimeKind.Unspecified, stamp.Time.Kind);
        }
    }

    // Both constructors refuse a missing clock, and the one with a user-name source refuses a missing source.
    [Fact]
    public void ConstructorsRefuseAMissingClockOrUserNameSource()
    {
        Assert.Equal(
            "timeProvider",
            Assert.Throws<ArgumentNullException>(() => new SaveStampProvider(null!)).ParamName);
        Assert.Equal(
            "timeProvider",
            Assert.Throws<ArgumentNullException>(() => new SaveStampProvider(null!, () => "epi-user")).ParamName);
        Assert.Equal(
            "userName",
            Assert.Throws<ArgumentNullException>(() => new SaveStampProvider(TimeProvider.System, null!)).ParamName);
    }

    /// <summary>A request the store answers without writing, so it needs no save stamp.</summary>
    public enum UnstampedRequest
    {
        /// <summary>A <c>PUT</c> of an id the base table does not hold: 404.</summary>
        UpdateOfAnUnknownRecord,

        /// <summary>A <c>PUT</c> of a deleted record: 409.</summary>
        UpdateOfADeletedRecord,

        /// <summary>
        /// A <c>PUT</c> whose stored record, overlaid with the submitted values, fails the required gate: 400.
        /// </summary>
        UpdateFailingTheRequiredGate,

        /// <summary>
        /// A <c>PUT</c> of a record lacking its page row, whose table the store's in-transaction permission read denies
        /// INSERT on: 403.
        /// </summary>
        UpdateDeniedAMissingPageRow,

        /// <summary>A <c>DELETE</c> of an id the base table does not hold: 404.</summary>
        DeleteOfAnUnknownRecord,

        /// <summary>A <c>DELETE</c> of an already-deleted record: 204.</summary>
        DeleteOfADeletedRecord,
    }

    // AAP 0.3.4.2, 0.3.4.6 and 0.9.3, through the API as Program.cs composes it: a write that passes its checks in a
    // process without a user name stops at its stamp, before any data statement. A POST asks for the stamp before the
    // store call, so the store's insert never runs; a PUT and a DELETE of an active record hand the stamp source to the
    // store, which asks it only after its locked read has decided the write, so its locked read ran and nothing else
    // did. Each is the generic 500 with writeOutcome notWritten; the source was asked exactly once; no write was
    // recorded, the seeded record is unchanged and no record was added; and the one handler line names the exception
    // type, operation and outcome, with no exception object, no submitted value and not the refusal's text.
    [Theory]
    [MemberData(nameof(EnvironmentsAndWriteMethods))]
    public async Task WriteInAProcessWithoutAUserNameIsRefusedBeforeAnyDataStatementAsNotWritten(
        string environment,
        string method)
    {
        var (store, _, id) = CreateServices();
        var before = store.GetRecords(FakeForms.TwoPageViewId);
        var valueSentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
        var user = new NamelessUser();

        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store, environment));
        await using var host = WithUserNameSource(factory, user);
        using var client = host.CreateClient();
        store.ClearCalls();

        using var response = await SendWriteAsync(client, method, id, valueSentinel);
        var text = await response.Content.ReadAsStringAsync();

        // The response: the generic 500 with the body's trace id, and notWritten.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ErrorContractTests.ProblemMediaType, response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(text);
        var problem = ApiTestClient.AssertProblemDetails(
            document.RootElement,
            StatusCodes.Status500InternalServerError);
        var traceId = problem.GetProperty("traceId").GetString();
        Assert.Equal(
            ErrorContractTests.ExpectedUnexpectedSentence + ErrorContractTests.ExpectedTraceIdLabel + traceId,
            problem.GetProperty("detail").GetString());
        Assert.Equal(ErrorContractTests.ExpectedNotWritten, problem.GetProperty("writeOutcome").GetString());
        Assert.DoesNotContain(valueSentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NoUserNameMessage, text, StringComparison.Ordinal);

        // The stamp source was asked once, for the one stamp the write would have stored.
        Assert.Equal(1, user.Calls);

        // The store: an insert was never called; an update or a delete ran only its locked read. Nothing changed.
        FakeStoreCall[] expectedCalls = method == Insert ? [] : [new FakeStoreCall(method, IsWrite: false)];
        Assert.Equal(expectedCalls, store.Calls.Where(call => call.Method == method));
        Assert.Empty(store.WriteCalls);
        AssertRecordsUnchanged(before, store.GetRecords(FakeForms.TwoPageViewId));
        var stored = Assert.Single(store.GetRecords(FakeForms.TwoPageViewId));
        Assert.Equal(
            (id, RecordQueryRules.ActiveRecStatus, OriginalLastName),
            (stored.GlobalRecordId, stored.RecStatus, stored.Values["LastName"].AsText()));
        Assert.Null(stored.FirstSave);
        Assert.Null(stored.LastSave);

        // The logs: one handler line, and no post-commit line, because nothing was committed. The insert's refusal
        // reaches the handler as itself under the endpoint's operation; the store reports an update's or a delete's
        // as its own failure, with nothing written, under the store's operation name.
        var line = Assert.Single(
            factory.Logs.Find(entry => entry.Category == typeof(ApiExceptionHandler).FullName));
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("RequestFailed", line.EventId.Name);
        Assert.Null(line.ExceptionType);
        Assert.Equal<object?>(StatusCodes.Status500InternalServerError, LogValue(line, "StatusCode"));
        Assert.Equal<object?>(
            (method == Insert ? typeof(InvalidOperationException) : typeof(DataStoreWriteException)).FullName,
            LogValue(line, "ExceptionType"));
        Assert.Equal<object?>(method == Insert ? OperationName(method) : method, LogValue(line, "Operation"));
        Assert.Null(LogValue(line, "SqlErrorNumber"));
        Assert.Equal<object?>(ErrorContractTests.ExpectedNotWritten, LogValue(line, "WriteOutcome"));
        Assert.Equal<object?>(traceId, LogValue(line, "TraceId"));
        Assert.Empty(factory.Logs.Find(entry => entry.Category == typeof(RecordService).FullName));
        Assert.Empty(factory.Logs.Find(entry => entry.ExceptionType is not null));
        Assert.False(factory.Logs.ContainsText(valueSentinel), "A log line holds the submitted value.");
        Assert.False(factory.Logs.ContainsText(NoUserNameMessage), "A log line holds the exception's message.");
    }

    // AAP 0.3.4.2 (Retry), 0.3.4.6 (statement order, save stamp) and 0.2.4, through the API as Program.cs composes it:
    // in a process without a user name, a PUT or DELETE the store answers without writing keeps its usual answer,
    // because the store asks for the stamp only once its locked checks decide that the operation writes. The stamp
    // source is never asked, the store's locked read ran and recorded no write, and every record of the form is as it
    // was, stamps included.
    [Theory]
    [InlineData(UnstampedRequest.UpdateOfAnUnknownRecord, StatusCodes.Status404NotFound)]
    [InlineData(UnstampedRequest.UpdateOfADeletedRecord, StatusCodes.Status409Conflict)]
    [InlineData(UnstampedRequest.UpdateFailingTheRequiredGate, StatusCodes.Status400BadRequest)]
    [InlineData(UnstampedRequest.UpdateDeniedAMissingPageRow, StatusCodes.Status403Forbidden)]
    [InlineData(UnstampedRequest.DeleteOfAnUnknownRecord, StatusCodes.Status404NotFound)]
    [InlineData(UnstampedRequest.DeleteOfADeletedRecord, StatusCodes.Status204NoContent)]
    public async Task RequestThatWritesNothingKeepsItsAnswerInAProcessWithoutAUserName(
        UnstampedRequest request,
        int status)
    {
        var store = FakeDataStore.WithStandardForms();
        var (viewId, id, method, values) = ArrangeUnstamped(store, request);
        var before = store.GetRecords(viewId);
        var user = new NamelessUser();

        await using var factory = new ApiFactory(ApiFactoryOptions.ForFake(store));
        await using var host = WithUserNameSource(factory, user);
        using var client = host.CreateClient();
        store.ClearCalls();

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/forms/{viewId}/records/{Uri.EscapeDataString(id)}");
        using var response = method == Update
            ? await ApiTestClient.PutJsonAsync(client, path, new { values })
            : await client.DeleteAsync(path);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, user.Calls);
        Assert.Equal([new FakeStoreCall(method, IsWrite: false)], store.Calls.Where(call => call.Method == method));
        Assert.Empty(store.WriteCalls);
        AssertRecordsUnchanged(before, store.GetRecords(viewId));
    }

    // A store holding one active TwoPage record whose LastName is OriginalLastName, and the services over it.
    private static (FakeDataStore Store, RecordService Records, string Id) CreateServices()
    {
        var store = FakeDataStore.WithStandardForms();
        var id = store.SeedRecord(
            FakeForms.TwoPageViewId,
            new Dictionary<string, FieldValue> { ["LastName"] = FieldValue.FromText(OriginalLastName) });
        var forms = new FormService(store, new CheckCodeEngine(), NullLogger<FormService>.Instance);
        var records = new RecordService(
            store,
            forms,
            new SaveStampProvider(TimeProvider.System),
            NullLogger<RecordService>.Instance);
        return (store, records, id);
    }

    // The form, record id, write method and PUT values of each request the store answers without writing. Every case
    // seeds the record it names, or, for an unknown id, a record beside it, so an unchanged store is a real check.
    private static (int ViewId, string Id, string Method, Dictionary<string, object?> Values) ArrangeUnstamped(
        FakeDataStore store,
        UnstampedRequest request)
    {
        var changed = new Dictionary<string, object?> { ["LastName"] = ChangedLastName };
        var original = new Dictionary<string, FieldValue> { ["LastName"] = FieldValue.FromText(OriginalLastName) };
        var unknownId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        switch (request)
        {
            case UnstampedRequest.UpdateOfAnUnknownRecord:
                store.SeedRecord(FakeForms.TwoPageViewId, original);
                return (FakeForms.TwoPageViewId, unknownId, Update, changed);

            case UnstampedRequest.UpdateOfADeletedRecord:
                return (
                    FakeForms.TwoPageViewId,
                    store.SeedRecord(FakeForms.TwoPageViewId, original, RecordQueryRules.DeletedRecStatus),
                    Update,
                    changed);

            case UnstampedRequest.UpdateFailingTheRequiredGate:
                // The stored record leaves the required field empty and the PUT does not submit it.
                return (
                    FakeForms.RequiredViewId,
                    store.SeedRecord(FakeForms.RequiredViewId),
                    Update,
                    new Dictionary<string, object?> { ["Comment"] = ChangedLastName });

            case UnstampedRequest.UpdateDeniedAMissingPageRow:
                // The PUT fills the required field, so it passes the gate, and the service's own check passes, because
                // UPDATE is granted everywhere; only the store's in-transaction check refuses the missing page's
                // INSERT.
                var form = FakeForms.WithRequiredField.Metadata;
                var page = Assert.Single(form.Pages);
                var lacking = store.SeedRecord(FakeForms.RequiredViewId, missingPageIds: [page.PageId]);
                store.SetPermissions(
                    FakeForms.RequiredViewId,
                    new FormPermissions(
                    [
                        new TablePermission(form.BaseTableName, CanInsert: true, CanUpdate: true),
                        new TablePermission(page.TableName, CanInsert: false, CanUpdate: true),
                    ]));
                return (
                    FakeForms.RequiredViewId,
                    lacking,
                    Update,
                    new Dictionary<string, object?>
                    {
                        [FakeForms.RequiredFieldName] = "Filled",
                        ["Comment"] = ChangedLastName,
                    });

            case UnstampedRequest.DeleteOfAnUnknownRecord:
                store.SeedRecord(FakeForms.TwoPageViewId, original);
                return (FakeForms.TwoPageViewId, unknownId, SoftDelete, changed);

            case UnstampedRequest.DeleteOfADeletedRecord:
                return (
                    FakeForms.TwoPageViewId,
                    store.SeedRecord(FakeForms.TwoPageViewId, original, RecordQueryRules.DeletedRecStatus),
                    SoftDelete,
                    changed);

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request, "No case arranges this request.");
        }
    }

    // The API as Program.cs composes it over the factory's store, with a save stamp provider whose user name comes from
    // user.
    private static WebApplicationFactory<Program> WithUserNameSource(ApiFactory factory, NamelessUser user) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<SaveStampProvider>();
                services.AddSingleton(provider => new SaveStampProvider(
                    provider.GetRequiredService<TimeProvider>(),
                    user.Read));
            }));

    // Each record holds exactly what it held before, in the same order: id, key, status, every value, page rows and
    // both stamps.
    private static void AssertRecordsUnchanged(
        IReadOnlyList<FakeRecordState> before,
        IReadOnlyList<FakeRecordState> after)
    {
        Assert.Equal(before.Select(record => record.GlobalRecordId), after.Select(record => record.GlobalRecordId));
        foreach (var (was, now) in before.Zip(after))
        {
            Assert.Equal(
                (was.UniqueKey, was.RecStatus, was.FirstSave, was.LastSave),
                (now.UniqueKey, now.RecStatus, now.FirstSave, now.LastSave));
            Assert.Equal(was.Values.Keys, now.Values.Keys);
            foreach (var (name, value) in was.Values)
            {
                Assert.Equal(value, now.Values[name]);
            }

            Assert.Equal(was.PresentPageIds.Order(), now.PresentPageIds.Order());
        }
    }

    private static Exception Failure(string kind, WriteOutcome outcome) => kind switch
    {
        Unavailable => FakeDataStore.Unavailable(outcome),
        WriteFailure => FakeDataStore.WriteFailure(outcome),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // The request as routing leaves it for the write endpoint: its metadata, request services and a readable body.
    private static DefaultHttpContext WriteContext(string method, IServiceProvider services, Stream body)
    {
        var (metadata, name) = method switch
        {
            Insert => (WriteEndpointMetadata.Insert, "CreateRecord"),
            Update => (WriteEndpointMetadata.Update, "UpdateRecord"),
            SoftDelete => (WriteEndpointMetadata.SoftDelete, "DeleteRecord"),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(metadata), name));
        return context;
    }

    // Each write submits LastName = ChangedLastName; the delete submits nothing.
    private static Task<IResult> WriteAsync(RecordService records, HttpContext context, string method, string id)
    {
        using var values = JsonDocument.Parse($$"""{"LastName":"{{ChangedLastName}}"}""");
        var request = new RecordWriteRequest(values.RootElement.Clone());
        return method switch
        {
            Insert => records.CreateAsync(context, FakeForms.TwoPageViewId, request, CancellationToken.None),
            Update => records.UpdateAsync(context, FakeForms.TwoPageViewId, id, request, CancellationToken.None),
            SoftDelete => records.DeleteAsync(context, FakeForms.TwoPageViewId, id, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
    }

    // The HTTP request of each write method on the TwoPage form: POST and PUT submit LastName = lastName; the DELETE
    // sends no body.
    private static Task<HttpResponseMessage> SendWriteAsync(
        HttpClient client,
        string method,
        string id,
        string lastName)
    {
        var records = string.Create(CultureInfo.InvariantCulture, $"/api/forms/{FakeForms.TwoPageViewId}/records");
        var record = records + "/" + Uri.EscapeDataString(id);
        var body = new { values = new Dictionary<string, object?> { ["LastName"] = lastName } };
        return method switch
        {
            Insert => ApiTestClient.PostJsonAsync(client, records, body),
            Update => ApiTestClient.PutJsonAsync(client, record, body),
            SoftDelete => client.DeleteAsync(record),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
    }

    // The fixed operation name the handler line gives each write method's endpoint.
    private static string OperationName(string method) => method switch
    {
        Insert => "record.insert",
        Update => "record.update",
        SoftDelete => "record.softDelete",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static object? LogValue(CapturedLogEntry entry, string key) =>
        Assert.Single(entry.State, pair => pair.Key == key).Value;

    // The writeOutcome wire values of the error contract.
    private static string WireValue(WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.NotWritten => "notWritten",
        WriteOutcome.Unknown => "unknown",
        WriteOutcome.Written => "written",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    // A user-name source for a process whose OS user has no name: every read answers the empty name, and is counted,
    // so a case can tell whether a stamp was asked for. Requests may run on any thread, so the count is atomic.
    private sealed class NamelessUser
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string? Read()
        {
            Interlocked.Increment(ref _calls);
            return string.Empty;
        }
    }
}
