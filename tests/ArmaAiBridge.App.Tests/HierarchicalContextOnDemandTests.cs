using System.Net;
using System.Text;
using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class HierarchicalContextOnDemandTests
{
    private const string SeedState =
        "{\"schema\":\"arma-ai-bridge/context-seed-state-v1\",\"player\":{\"side\":\"WEST\",\"groupCallsign\":\"Alpha 1-1\"}}";

    [Theory]
    [InlineData("context-seed-state-v1.schema.json")]
    [InlineData("normalized-event-v2.schema.json")]
    public void NewWireSchemas_CloseTheRootAndEveryNestedObject(string file)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        using JsonDocument schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "schemas", file)));
        AssertClosed(schema.RootElement, "$");
    }

    [Fact]
    public void Catalogue_ContainsTheElevenStableGroupsAndInspectableCategories()
    {
        Assert.Equal(11, HierarchicalContextCatalogue.Groups.Length);
        Assert.Equal(new[]
        {
            "entities", "operations", "intelligence", "geography", "resources",
            "environment", "communications", "events_and_history",
            "lore_and_rules", "long_term_map_intelligence", "miscellaneous"
        }, HierarchicalContextCatalogue.Groups);
        Assert.Contains(
            HierarchicalContextCatalogue.Inspect("intelligence"),
            item => item.Category == "current_contacts");
        Assert.Contains(
            HierarchicalContextCatalogue.Inspect("geography"),
            item => item.Category == "spatial_relationships");
        Assert.Contains(
            HierarchicalContextCatalogue.Inspect("miscellaneous"),
            item => item.Category == "casual_conversation");
    }

    [Fact]
    public void Broker_StrictlyRejectsUnknownFieldsAndCrossGroupCategories()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arma-context-{Guid.NewGuid():N}.db");
        try
        {
            using SqliteStateRepository state = new(path);
            using SqliteMapIntelligenceRepository map = new(":memory:");
            ContextTraceStore trace = new();
            using HierarchicalContextBroker broker = new(
                state,
                new ContextConversationStore(),
                trace,
                mapIntelligence: map);

            using JsonDocument inspect = JsonDocument.Parse("{\"group\":\"intelligence\"}");
            string result = broker.Execute("inspect_context_catalogue", inspect.RootElement);
            Assert.Contains("\"current_contacts\"", result, StringComparison.Ordinal);

            using JsonDocument unknown = JsonDocument.Parse(
                "{\"group\":\"intelligence\",\"category\":\"current_contacts\",\"detailLevel\":\"summary\",\"scope\":\"current\",\"entityAliases\":[],\"referenceAliases\":[],\"timeRangeSeconds\":300,\"maximumDistanceMeters\":2000,\"limit\":10,\"requestedFields\":[],\"command\":\"SELECT *\"}");
            Assert.Throws<InvalidOperationException>(() =>
                broker.Execute("query_context", unknown.RootElement));

            using JsonDocument mismatch = JsonDocument.Parse(
                "{\"group\":\"resources\",\"category\":\"current_contacts\",\"detailLevel\":\"summary\",\"scope\":\"current\",\"entityAliases\":[],\"referenceAliases\":[],\"timeRangeSeconds\":300,\"maximumDistanceMeters\":2000,\"limit\":10,\"requestedFields\":[]}");
            Assert.Throws<InvalidOperationException>(() =>
                broker.Execute("query_context", mismatch.RootElement));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LongTermMapIntelligence_HasNoConfirmationRoundAndRemainsBounded()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string assistant = File.ReadAllText(Path.Combine(
            root, "src", "ArmaAiBridge.App", "Services", "OpenAiAssistantService.cs"));
        string broker = File.ReadAllText(Path.Combine(
            root, "src", "ArmaAiBridge.App", "Services", "HierarchicalContextBroker.cs"));

        Assert.DoesNotContain(
            "request_long_term_intelligence_permission",
            assistant,
            StringComparison.Ordinal);
        Assert.DoesNotContain("confirmation-required", broker, StringComparison.Ordinal);
        Assert.Contains("RequiredInteger(root, \"limit\", 1, 20)", broker, StringComparison.Ordinal);
        Assert.Contains(
            "Contains(\"long_term_map_intelligence\", normalizedCategory)",
            broker,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayerTurn_StartsWithOnlyTheMessageAndCatalogueThenRetrievesTwoChosenCategories()
    {
        List<(string Group, string Category)> calls = new();
        ContextScriptHandler handler = new(requestNumber => requestNumber switch
        {
            1 => FunctionCall("call-one", "query_context", QueryArguments("intelligence", "current_contacts")),
            2 => FunctionCall("call-two", "query_context", QueryArguments("geography", "spatial_relationships")),
            _ => Final("Alpha One-One, current contacts are near Bullseye.")
        });
        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://api.openai.test/v1/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        using OpenAiAssistantService service = new(client);

        AssistantResponse response = await service.AskAsync(
            "test-key",
            "gpt-5.6-luna",
            "Where are the current contacts?",
            SeedState,
            ResponseProfilePolicy.Defaults(),
            (name, arguments, _) =>
            {
                calls.Add((
                    arguments.GetProperty("group").GetString()!,
                    arguments.GetProperty("category").GetString()!));
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    schema = "arma-ai-bridge/context-result-v1",
                    category = arguments.GetProperty("category").GetString(),
                    records = Array.Empty<object>()
                }));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(new[]
        {
            ("intelligence", "current_contacts"),
            ("geography", "spatial_relationships")
        }, calls);
        Assert.Equal(3, handler.Requests.Count);
        string initial = handler.Requests[0].GetProperty("input")[0].GetProperty("content").GetString()!;
        Assert.Contains("Where are the current contacts?", initial, StringComparison.Ordinal);
        Assert.Contains("Available information areas:", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("\"availableGroups\"", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("\"friendlyForces\"", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("\"enemyContacts\"", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("\"overcast\"", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("\"weather\"", initial, StringComparison.Ordinal);
        string firstToolOutput = handler.Requests[1].GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out JsonElement type) &&
                            type.GetString() == "function_call_output")
            .GetProperty("output").GetString()!;
        Assert.Contains("Current contacts.", firstToolOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("{", firstToolOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("context-result-v1", firstToolOutput, StringComparison.Ordinal);
        Assert.Equal(2, response.ToolCalls);
    }

    [Fact]
    public async Task NormalizedEvent_IsMinimalAndMayRemainSilent()
    {
        ContextScriptHandler handler = new(_ => Final("NO_RADIO_RESPONSE"));
        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://api.openai.test/v1/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        using OpenAiAssistantService service = new(client);
        const string normalizedEvent =
            "{\"schema\":\"arma-ai-bridge/normalized-event-v2\",\"eventAlias\":\"event-safe-one\",\"eventType\":\"contact-development\",\"transition\":\"possible-new\",\"entityAliases\":[\"contact-safe-one\"],\"changedDomains\":[\"contacts\"],\"snapshotSequence\":2,\"windowSnapshotCount\":1,\"observedAtUtc\":\"2026-07-23T12:00:00Z\"}";

        AssistantResponse response = await service.AskEventAsync(
            "test-key",
            "gpt-5.6-luna",
            normalizedEvent,
            SeedState,
            ResponseProfilePolicy.Defaults(),
            (_, _, _) => Task.FromResult("unused"),
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, response.Text);
        string input = handler.Requests.Single()
            .GetProperty("input")[0].GetProperty("content").GetString()!;
        Assert.Contains("possible new contact", input, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"type\"", input, StringComparison.Ordinal);
        Assert.DoesNotContain("event-safe-one", input, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-07-23", input, StringComparison.Ordinal);
        Assert.DoesNotContain("\"grid\"", input, StringComparison.Ordinal);
        Assert.DoesNotContain("\"suggestedCategories\"", input, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextFacts_ProjectContactResultsWithoutDatabaseShapeOrAliases()
    {
        const string structured = """
        {
          "schema":"arma-ai-bridge/context-result-v1",
          "group":"entities",
          "category":"enemy_contacts",
          "result":{
            "available":true,
            "summary":{
              "current":2,
              "lastKnown":6,
              "confirmedDead":0,
              "presentationGroups":2,
              "observationsMayOverlap":true
            },
            "groups":[{
              "entityAlias":"contact-group:1",
              "memberCount":6,
              "relationship":"hostile",
              "classification":["infantry"],
              "status":"current",
              "position":"1,800 metres northwest of Bullseye",
              "uncertaintyMeters":10,
              "memberAliases":["contact-secret-one","contact-secret-two"]
            }],
            "records":[]
          },
          "truncated":false
        }
        """;

        string facts = ContextFactFormatter.Format("query_context", structured);

        Assert.Contains("Known contact picture: two current, six last known, and zero confirmed dead.", facts, StringComparison.Ordinal);
        Assert.Contains("six hostile infantry contacts are current at one thousand eight hundred metres northwest of Bullseye.", facts, StringComparison.Ordinal);
        Assert.Contains("Some observations may describe the same contacts.", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("{", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("schema", facts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alias", facts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contact-secret", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("presentationGroups", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextFacts_ProjectMemoryAsReadableReportsWithoutRowsOrTimestamps()
    {
        const string structured = """
        {
          "ok":true,
          "records":[{
            "id":47,
            "text":"A damaged truck was reported at the airfield.",
            "provenance":"user-reported",
            "updatedAtUtc":"2026-07-23T13:41:28Z",
            "tags":["category:vehicles","database:internal"]
          }]
        }
        """;

        string facts = ContextFactFormatter.Format("search_memory", structured);

        Assert.Contains("You reported: A damaged truck was reported at the airfield.", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("47", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("tags", facts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("database", facts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContextFacts_NeverForwardStoredStructuredEventPayloads()
    {
        const string structured = """
        {
          "schema":"arma-ai-bridge/context-result-v1",
          "group":"events_and_history",
          "category":"recent_events",
          "result":{
            "available":true,
            "records":[{
              "reportAlias":"report:81",
              "content":"{\"schema\":\"arma-ai-bridge/normalized-event-v2\",\"eventAlias\":\"event-secret\",\"snapshotSequence\":21}",
              "provenance":"normalized-event-candidate",
              "updatedAtUtc":"2026-07-23T13:41:28Z",
              "tags":["event-candidate"]
            }]
          }
        }
        """;

        string facts = ContextFactFormatter.Format("query_context", structured);

        Assert.Contains("A prior mission-state update was retained.", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("event-secret", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshotSequence", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("{", facts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventAssessment_UsesInternalCurrentReferenceWithoutShowingItToTheModel()
    {
        ContextScriptHandler handler = new(requestNumber => requestNumber switch
        {
            1 => FunctionCall(
                "call-assessment",
                "record_event_assessment",
                """
                {"priority":"important","outcome":"sitrep","summary":"A contact update is useful.","confidence":"high"}
                """),
            _ => Final("Contact update acknowledged.")
        });
        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://api.openai.test/v1/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        using OpenAiAssistantService service = new(client);
        JsonElement received = default;
        const string normalizedEvent =
            "{\"schema\":\"arma-ai-bridge/normalized-event-v2\",\"eventAlias\":\"event-safe-one\",\"eventType\":\"state-change-bundle\",\"transition\":\"window-complete\",\"entityAliases\":[],\"changedDomains\":[\"contacts\"],\"snapshotSequence\":21,\"windowSnapshotCount\":6,\"observedAtUtc\":\"2026-07-23T12:00:00Z\"}";

        await service.AskEventAsync(
            "test-key",
            "gpt-5.6-luna",
            normalizedEvent,
            SeedState,
            ResponseProfilePolicy.Defaults(),
            (_, arguments, _) =>
            {
                received = arguments.Clone();
                return Task.FromResult("{\"ok\":true}");
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("event-safe-one", received.GetProperty("eventAlias").GetString());
        string initial = handler.Requests[0].GetProperty("input")[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain("event-safe-one", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshotSequence", initial, StringComparison.Ordinal);
        string toolOutput = handler.Requests[1].GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out JsonElement type) &&
                            type.GetString() == "function_call_output")
            .GetProperty("output").GetString()!;
        Assert.Equal("The current state update was assessed and retained locally.", toolOutput);
    }

    private static string QueryArguments(string group, string category)
        => JsonSerializer.Serialize(new
        {
            group,
            category,
            detailLevel = "summary",
            scope = "current",
            entityAliases = Array.Empty<string>(),
            referenceAliases = Array.Empty<string>(),
            timeRangeSeconds = 300,
            maximumDistanceMeters = 2000,
            limit = 10,
            requestedFields = Array.Empty<string>()
        });

    private static void AssertClosed(JsonElement schema, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out JsonElement type) &&
            type.ValueKind == JsonValueKind.String &&
            type.GetString() == "object")
        {
            Assert.True(
                schema.TryGetProperty("additionalProperties", out JsonElement additional) &&
                additional.ValueKind == JsonValueKind.False,
                $"{path} must reject unknown properties.");
        }
        if (!schema.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty property in properties.EnumerateObject())
            AssertClosed(property.Value, $"{path}.{property.Name}");
    }

    private static HttpResponseMessage FunctionCall(string callId, string name, string arguments)
        => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            status = "completed",
            model = "gpt-5.6-luna",
            output = new object[]
            {
                new { type = "reasoning", id = "rs-" + callId, encrypted_content = "opaque" },
                new { type = "function_call", call_id = callId, name, arguments }
            },
            usage = new { input_tokens = 100, output_tokens = 20 }
        }));

    private static HttpResponseMessage Final(string text)
        => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            status = "completed",
            model = "gpt-5.6-luna",
            output = new[]
            {
                new
                {
                    type = "message",
                    role = "assistant",
                    content = new[] { new { type = "output_text", text } }
                }
            },
            usage = new { input_tokens = 100, output_tokens = 20 }
        }));

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class ContextScriptHandler(
        Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            Requests.Add(document.RootElement.Clone());
            return responseFactory(Requests.Count);
        }
    }
}
