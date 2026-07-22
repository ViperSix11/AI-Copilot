using System.Text.Json;
using System.Text.Json.Nodes;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class OperationalMemoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("map-gazetteer-v1")]
    [InlineData("operational-observation-batch-v1")]
    public void ProtocolFixtures_MatchClosedVersionedSchemas(string contract)
    {
        using JsonDocument schema = JsonDocument.Parse(Fixture($"schemas/{contract}.schema.json"));
        using JsonDocument fixture = JsonDocument.Parse(Fixture($"{contract}.json"));
        JsonElement schemaRoot = schema.RootElement;
        JsonElement fixtureRoot = fixture.RootElement;
        Assert.False(schemaRoot.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(schemaRoot.GetProperty("properties").GetProperty("schema").GetProperty("const").GetString(),
            fixtureRoot.GetProperty("schema").GetString());
        foreach (JsonElement required in schemaRoot.GetProperty("required").EnumerateArray())
            Assert.True(fixtureRoot.TryGetProperty(required.GetString()!, out _));
    }

    [Fact]
    public void ProtocolRejectsPrivateOrUnexpectedFields()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            JsonNode privateObservation = JsonNode.Parse(Fixture("operational-observation-batch-v1.json"))!;
            privateObservation["observations"]![0]!["profileName"] = "PRIVATE NAME";
            TelemetryIngestResult rejectedObservation = harness.Ingest.Ingest(privateObservation.ToJsonString());
            Assert.Equal(TelemetryIngestStatus.Rejected, rejectedObservation.Status);
            Assert.Equal("operational_observation_unexpected_property", rejectedObservation.DiagnosticCode);
            Assert.Empty(harness.Memory.GetCurrentView().Entities);

            JsonNode privateGazetteer = JsonNode.Parse(Fixture("map-gazetteer-v1.json"))!;
            privateGazetteer["messageId"] = "message-gazetteer-private";
            privateGazetteer["sequence"] = 4;
            privateGazetteer["locations"]![0]!["uid"] = "PRIVATE-UID";
            TelemetryIngestResult rejectedGazetteer = harness.Ingest.Ingest(privateGazetteer.ToJsonString());
            Assert.Equal(TelemetryIngestStatus.Rejected, rejectedGazetteer.Status);
            Assert.Equal("gazetteer_location_unexpected_property", rejectedGazetteer.DiagnosticCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Gazetteer_IsDeterministicPersistentAndContainsNoStaticMapIndex()
    {
        string root = TemporaryDirectory();
        try
        {
            string firstFingerprint;
            using (OperationalHarness harness = CreateHarness(root, ingestGazetteer: false))
            {
                Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(Fixture("map-gazetteer-v1.json")).Status);
                OperationalMemoryView first = harness.Memory.GetCurrentView();
                Assert.Equal(GazetteerReadiness.Ready, first.GazetteerReadiness);
                Assert.Equal(2, first.GazetteerLocationCount);
                firstFingerprint = first.GazetteerFingerprintAlias;
                Assert.StartsWith("gazetteer-", firstFingerprint, StringComparison.Ordinal);

                JsonNode reordered = JsonNode.Parse(Fixture("map-gazetteer-v1.json"))!;
                reordered["sequence"] = 3;
                reordered["messageId"] = "message-gazetteer-reordered";
                JsonArray locations = reordered["locations"]!.AsArray();
                JsonNode firstLocation = locations[0]!.DeepClone();
                locations.RemoveAt(0);
                locations.Add(firstLocation);
                Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(reordered.ToJsonString()).Status);
                Assert.Equal(firstFingerprint, harness.Memory.GetCurrentView().GazetteerFingerprintAlias);
            }

            using (OperationalHarness reopened = CreateHarness(root, ingestGazetteer: false))
            {
                OperationalMemoryView cached = reopened.Memory.GetCurrentView();
                Assert.Equal(GazetteerReadiness.Ready, cached.GazetteerReadiness);
                Assert.Equal(firstFingerprint, cached.GazetteerFingerprintAlias);
                Assert.Equal(2, cached.NamedLocations.Count);
            }

            using (SqliteConnection database = new($"Data Source={Path.Combine(root, "gazetteer.sqlite3")};Pooling=False"))
            {
                database.Open();
                using SqliteCommand tables = database.CreateCommand();
                tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
                using SqliteDataReader reader = tables.ExecuteReader();
                List<string> names = new();
                while (reader.Read()) names.Add(reader.GetString(0));
                Assert.Equal(new[] { "gazetteer_locations", "gazetteer_maps", "schema_metadata" }, names);
                Assert.DoesNotContain(names, name => name.Contains("building", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(names, name => name.Contains("road", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(names, name => name.Contains("terrain", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GazetteerPages_AreAtomicAndMismatchedPagesFailClosed()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root, ingestGazetteer: false);
            JsonNode first = JsonNode.Parse(Fixture("map-gazetteer-v1.json"))!;
            first["pageCount"] = 2;
            JsonArray firstLocations = first["locations"]!.AsArray();
            JsonNode secondLocation = firstLocations[1]!.DeepClone();
            firstLocations.RemoveAt(1);
            Assert.Equal("gazetteer_page_buffered", harness.Ingest.Ingest(first.ToJsonString()).DiagnosticCode);
            Assert.Equal(GazetteerReadiness.Receiving, harness.Memory.GetCurrentView().GazetteerReadiness);
            Assert.Empty(harness.Memory.GetCurrentView().NamedLocations);

            JsonNode second = first.DeepClone();
            second["messageId"] = "message-gazetteer-2";
            second["sequence"] = 3;
            second["pageIndex"] = 1;
            second["locations"] = new JsonArray(secondLocation);
            Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(second.ToJsonString()).Status);
            Assert.Equal(2, harness.Memory.GetCurrentView().GazetteerLocationCount);

            JsonNode next = JsonNode.Parse(Fixture("map-gazetteer-v1.json"))!;
            next["messageId"] = "message-gazetteer-next-1";
            next["sequence"] = 4;
            next["gazetteerId"] = "gazetteer-000002";
            next["pageCount"] = 2;
            next["locations"]!.AsArray().RemoveAt(1);
            harness.Ingest.Ingest(next.ToJsonString());
            JsonNode mismatch = next.DeepClone();
            mismatch["messageId"] = "message-gazetteer-next-2";
            mismatch["sequence"] = 5;
            mismatch["pageIndex"] = 1;
            mismatch["world"]!["sizeMeters"] = 9000;
            TelemetryIngestResult rejected = harness.Ingest.Ingest(mismatch.ToJsonString());
            Assert.Equal("inconsistent_gazetteer_pages", rejected.DiagnosticCode);
            Assert.Equal(GazetteerReadiness.Failed, harness.Memory.GetCurrentView().GazetteerReadiness);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NewerOperationalSchema_FailsClosedWithoutRewritingVersion()
    {
        string root = TemporaryDirectory();
        try
        {
            using (OperationalHarness first = CreateHarness(root))
                Assert.Equal(OperationalMemoryReadiness.Ready, first.Memory.GetCurrentView().Readiness);
            string databasePath = Directory.GetFiles(root, "operational-*.sqlite3").Single();
            using (SqliteConnection database = new($"Data Source={databasePath};Pooling=False"))
            {
                database.Open();
                using SqliteCommand newer = database.CreateCommand();
                newer.CommandText = "PRAGMA user_version=99;";
                newer.ExecuteNonQuery();
            }

            using (OperationalHarness reopened = CreateHarness(root))
                Assert.Equal(OperationalMemoryReadiness.Failed, reopened.Memory.GetCurrentView().Readiness);
            using (SqliteConnection verify = new($"Data Source={databasePath};Pooling=False"))
            {
                verify.Open();
                using SqliteCommand version = verify.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                Assert.Equal(99L, (long)version.ExecuteScalar()!);
                verify.Close();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissionStart_DoesNotPreseedOperationalEntities()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            Assert.Empty(harness.Memory.GetCurrentView().Entities);
            Assert.Empty(harness.Memory.GetCurrentView().Observations);
            string output = harness.Memory.QueryOperationalMemory(Arguments(
                """{"entityKind":"any","maxDistanceMeters":50000,"freshness":"any","includeConflicts":true,"limit":20}"""));
            Assert.Contains("\"entities\":[]", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NewSession_UsesCleanActivePartitionAndRetainsPriorHistoryLocally()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            Assert.Equal(TelemetryIngestStatus.Applied,
                harness.Ingest.Ingest(Fixture("operational-observation-batch-v1.json")).Status);
            Assert.Single(harness.Memory.GetCurrentView().Entities);

            JsonNode nextHandshake = JsonNode.Parse(Handshake())!;
            nextHandshake["messageId"] = "message-handshake-m4-session-b";
            nextHandshake["sessionId"] = "m4-source-session-b";
            Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(nextHandshake.ToJsonString()).Status);
            Assert.Empty(harness.Memory.GetCurrentView().Entities);
            Assert.Empty(harness.Memory.GetCurrentView().Observations);

            string databasePath = Directory.GetFiles(root, "operational-*.sqlite3").Single();
            using SqliteConnection database = new($"Data Source={databasePath};Pooling=False");
            database.Open();
            using SqliteCommand sessions = database.CreateCommand();
            sessions.CommandText = "SELECT COUNT(*) FROM sessions;";
            Assert.Equal(2L, (long)sessions.ExecuteScalar()!);
            using SqliteCommand priorEntities = database.CreateCommand();
            priorEntities.CommandText = "SELECT COUNT(*) FROM entities;";
            Assert.Equal(1L, (long)priorEntities.ExecuteScalar()!);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EngineObservation_HashesRawIdentityAndBecomesStaleWithoutDeletion()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            Assert.Equal(TelemetryIngestStatus.Applied,
                harness.Ingest.Ingest(Fixture("operational-observation-batch-v1.json")).Status);
            OperationalEntityState entity = Assert.Single(harness.Memory.GetCurrentView().Entities);
            Assert.Equal("vehicle-001", entity.Alias);
            Assert.Equal(WorldFreshness.Live, entity.Freshness);
            Assert.Equal(0.9, entity.Confidence, 3);

            string output = harness.Memory.QueryOperationalMemory(Arguments(
                """{"entityKind":"vehicle","maxDistanceMeters":50000,"freshness":"any","includeConflicts":true,"limit":20}"""));
            Assert.Contains("vehicle-001", output, StringComparison.Ordinal);
            Assert.Contains("observation-000001", output, StringComparison.Ordinal);
            Assert.Contains("\"observations\"", output, StringComparison.Ordinal);
            Assert.DoesNotContain("net:2:87", output, StringComparison.Ordinal);
            Assert.DoesNotContain("net:2:14", output, StringComparison.Ordinal);

            harness.Time.Advance(TimeSpan.FromSeconds(31));
            OperationalEntityState stale = Assert.Single(harness.Memory.GetCurrentView().Entities);
            Assert.Equal(WorldFreshness.Stale, stale.Freshness);
            Assert.True(stale.IsLastKnown);
            Assert.Equal("intact", stale.State);
            Assert.Equal(0.495, stale.Confidence, 3);
            Assert.Single(harness.Memory.GetCurrentView().Observations);

            string databasePath = Directory.GetFiles(root, "operational-*.sqlite3").Single();
            using SqliteConnection database = new($"Data Source={databasePath};Pooling=False");
            database.Open();
            using SqliteCommand command = database.CreateCommand();
            command.CommandText = "SELECT identity_hash FROM entity_identities UNION ALL SELECT source_hash FROM source_identities UNION ALL SELECT source_hash FROM observations;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string value = reader.GetString(0);
                Assert.DoesNotContain("net:", value, StringComparison.Ordinal);
                Assert.Matches("^[0-9A-F]{64}$", value);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissionAuthorizedReport_RetainsMissionProvenanceWithoutPrivateSourceIdentity()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            JsonNode report = JsonNode.Parse(Fixture("operational-observation-batch-v1.json"))!;
            report["observations"]![0]!["sourceEntityId"] = "mission-authority";
            report["observations"]![0]!["targetEntityId"] = "mission-report:cache-alpha";
            report["observations"]![0]!["provenance"] = "mission-report";
            report["observations"]![0]!["confidenceBasis"] = "mission-authorized-report";
            Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(report.ToJsonString()).Status);
            OperationalMemoryView view = harness.Memory.GetCurrentView();
            Assert.Equal(OperationalProvenance.MissionReport, Assert.Single(view.Observations).Provenance);
            Assert.Equal(OperationalIdentityQuality.FusedReport, Assert.Single(view.Entities).IdentityQuality);
            Assert.Equal(0.7, Assert.Single(view.Entities).Confidence, 3);
            string output = harness.Memory.QueryOperationalMemory(Arguments(
                """{"entityKind":"any","maxDistanceMeters":50000,"freshness":"any","includeConflicts":true,"limit":20}"""));
            Assert.Contains("mission-report", output, StringComparison.Ordinal);
            Assert.DoesNotContain("mission-authority", output, StringComparison.Ordinal);
            Assert.DoesNotContain("cache-alpha", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExplicitPlayerReport_IsLowerConfidenceAndFriendlyConfirmationFuses()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            const string report = "I saw an empty offroad 200 metres ahead.";
            string result = harness.Memory.RecordPlayerObservation(Arguments("""
                {
                  "sourceQuote":"I saw an empty offroad 200 metres ahead.",
                  "timeReference":"present",
                  "entityKind":"vehicle",
                  "classification":"offroad",
                  "state":"unknown",
                  "rangeMeters":200,
                  "bearingDegrees":0,
                  "bearingReference":"view",
                  "rangePrecisionMeters":50,
                  "bearingPrecisionDegrees":15,
                  "ageSeconds":null,
                  "namedLocation":null
                }
                """), report);
            Assert.Contains("recorded", result, StringComparison.Ordinal);
            OperationalEntityState playerEntity = Assert.Single(harness.Memory.GetCurrentView().Entities);
            Assert.Equal(OperationalIdentityQuality.FusedReport, playerEntity.IdentityQuality);
            Assert.Equal(0.45, playerEntity.Confidence, 3);
            Assert.InRange(playerEntity.Position!.X, 3535, 3536);
            Assert.InRange(playerEntity.Position.Y, 5747, 5749);
            Assert.InRange(playerEntity.PositionErrorMeters!.Value, 51, 53);

            JsonNode confirmation = JsonNode.Parse(Fixture("operational-observation-batch-v1.json"))!;
            confirmation["observations"]![0]!["position"] = new JsonArray(3535.2, 5747.8, 12.3);
            Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(confirmation.ToJsonString()).Status);

            OperationalMemoryView fused = harness.Memory.GetCurrentView();
            OperationalEntityState entity = Assert.Single(fused.Entities);
            Assert.Equal(OperationalIdentityQuality.StableMission, entity.IdentityQuality);
            Assert.Equal(2, fused.Observations.Count);
            Assert.Equal(1, entity.CorroborationCount);
            Assert.True(entity.Confidence > 0.9);
            Assert.DoesNotContain("net:2", JsonSerializer.Serialize(fused), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LaterFriendlyConfirmation_FusesReportIntoAlreadyKnownEngineEntity()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            JsonNode firstVisual = JsonNode.Parse(Fixture("operational-observation-batch-v1.json"))!;
            firstVisual["observations"]![0]!["position"] = new JsonArray(3535.2, 5747.8, 12.3);
            Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(firstVisual.ToJsonString()).Status);
            harness.Time.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(WorldFreshness.Stale, Assert.Single(harness.Memory.GetCurrentView().Entities).Freshness);

            const string report = "I saw an empty offroad 200 metres ahead 60 seconds ago.";
            harness.Memory.RecordPlayerObservation(Arguments("""
                {
                  "sourceQuote":"I saw an empty offroad 200 metres ahead 60 seconds ago.",
                  "timeReference":"past","entityKind":"vehicle","classification":"offroad","state":"unknown",
                  "rangeMeters":200,"bearingDegrees":0,"bearingReference":"view",
                  "rangePrecisionMeters":50,"bearingPrecisionDegrees":15,"ageSeconds":60,"namedLocation":null
                }
                """), report);
            Assert.Equal(2, harness.Memory.GetCurrentView().Entities.Count);

            JsonNode confirmation = firstVisual.DeepClone();
            confirmation["messageId"] = "message-observation-confirmation";
            confirmation["timestamp"] = 13.0;
            confirmation["sequence"] = 4;
            confirmation["batchId"] = "observation-batch-confirmation";
            confirmation["observations"]![0]!["observationId"] = "source-observation-confirmation";
            confirmation["observations"]![0]!["observedAt"] = 13.0;
            Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(confirmation.ToJsonString()).Status);

            OperationalMemoryView fused = harness.Memory.GetCurrentView();
            OperationalEntityState entity = Assert.Single(fused.Entities);
            Assert.Equal(OperationalIdentityQuality.StableMission, entity.IdentityQuality);
            Assert.Equal(3, fused.Observations.Count);
            Assert.All(fused.Observations, observation => Assert.Equal(entity.Alias, observation.EntityAlias));
            Assert.True(entity.CorroborationCount > 0);
            Assert.True(entity.Confidence > 0.9);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void QuestionsCannotWriteAndExplicitRetractionPreservesHistory()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            string hypothetical = "Could there be a truck 400 metres east?";
            string rejected = harness.Memory.RecordPlayerObservation(Arguments("""
                {
                  "sourceQuote":"Could there be a truck 400 metres east?",
                  "timeReference":"present","entityKind":"vehicle","classification":"truck","state":"unknown",
                  "rangeMeters":400,"bearingDegrees":90,"bearingReference":"absolute",
                  "rangePrecisionMeters":50,"bearingPrecisionDegrees":15,"ageSeconds":null,"namedLocation":null
                }
                """), hypothetical);
            Assert.Contains("questions_cannot_create_observations", rejected, StringComparison.Ordinal);
            Assert.Empty(harness.Memory.GetCurrentView().Observations);

            const string report = "I saw a truck 100 metres ahead.";
            harness.Memory.RecordPlayerObservation(Arguments("""
                {
                  "sourceQuote":"I saw a truck 100 metres ahead.","timeReference":"present",
                  "entityKind":"vehicle","classification":"truck","state":"unknown",
                  "rangeMeters":100,"bearingDegrees":0,"bearingReference":"view",
                  "rangePrecisionMeters":25,"bearingPrecisionDegrees":10,"ageSeconds":null,"namedLocation":null
                }
                """), report);
            string alias = Assert.Single(harness.Memory.GetCurrentView().Observations).Alias;
            const string retraction = "I retract my truck report.";
            string retracted = harness.Memory.CorrectPlayerObservation(Arguments($$"""
                {
                  "sourceQuote":"I retract my truck report.","action":"retract","observationAlias":"{{alias}}",
                  "timeReference":"present","entityKind":"vehicle","classification":"truck","state":"unknown",
                  "rangeMeters":null,"bearingDegrees":null,"bearingReference":"view",
                  "rangePrecisionMeters":25,"bearingPrecisionDegrees":10,"ageSeconds":null,"namedLocation":null
                }
                """), retraction);
            Assert.Contains("retracted", retracted, StringComparison.Ordinal);
            OperationalMemoryView view = harness.Memory.GetCurrentView();
            Assert.Single(view.Observations);
            Assert.NotNull(view.Observations[0].RetractedAtUtc);
            Assert.True(Assert.Single(view.Entities).IsRetracted);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExplicitCorrection_AppendsToSameEntityAndSupersedesPriorObservation()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            const string report = "I saw a truck 100 metres ahead.";
            string recorded = harness.Memory.RecordPlayerObservation(Arguments("""
                {
                  "sourceQuote":"I saw a truck 100 metres ahead.","timeReference":"present",
                  "entityKind":"vehicle","classification":"truck","state":"unknown",
                  "rangeMeters":100,"bearingDegrees":0,"bearingReference":"view",
                  "rangePrecisionMeters":25,"bearingPrecisionDegrees":10,"ageSeconds":null,"namedLocation":null
                }
                """), report);
            using JsonDocument recordedResult = JsonDocument.Parse(recorded);
            string entityAlias = recordedResult.RootElement.GetProperty("entityAlias").GetString()!;
            string originalObservation = recordedResult.RootElement.GetProperty("observationAlias").GetString()!;

            const string correction = "Correction: I saw the truck 150 metres ahead.";
            string corrected = harness.Memory.CorrectPlayerObservation(Arguments($$"""
                {
                  "sourceQuote":"Correction: I saw the truck 150 metres ahead.","action":"correct",
                  "observationAlias":"{{originalObservation}}","timeReference":"present",
                  "entityKind":"vehicle","classification":"truck","state":"unknown",
                  "rangeMeters":150,"bearingDegrees":0,"bearingReference":"view",
                  "rangePrecisionMeters":25,"bearingPrecisionDegrees":10,"ageSeconds":null,"namedLocation":null
                }
                """), correction);

            using JsonDocument correctedResult = JsonDocument.Parse(corrected);
            Assert.Equal("corrected", correctedResult.RootElement.GetProperty("status").GetString());
            Assert.Equal(entityAlias, correctedResult.RootElement.GetProperty("entityAlias").GetString());
            OperationalMemoryView view = harness.Memory.GetCurrentView();
            Assert.Single(view.Entities);
            Assert.Equal(2, view.Observations.Count);
            Assert.NotNull(view.Observations.Single(item => item.Alias == originalObservation).RetractedAtUtc);
            Assert.Equal(originalObservation,
                view.Observations.Single(item => item.Alias != originalObservation).Supersedes);
            Assert.InRange(Assert.Single(view.Entities).Position!.Y, 5710, 5712);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NamedLocationConstraint_DoesNotOverrideContradictoryRangeBearing()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            const string report = "I saw a truck 100 metres ahead near Agia Marina.";
            string result = harness.Memory.RecordPlayerObservation(Arguments("""
                {
                  "sourceQuote":"I saw a truck 100 metres ahead near Agia Marina.","timeReference":"present",
                  "entityKind":"vehicle","classification":"truck","state":"unknown",
                  "rangeMeters":100,"bearingDegrees":0,"bearingReference":"view",
                  "rangePrecisionMeters":25,"bearingPrecisionDegrees":10,"ageSeconds":null,
                  "namedLocation":"Agia Marina"
                }
                """), report);
            using JsonDocument response = JsonDocument.Parse(result);
            Assert.True(response.RootElement.GetProperty("constraintConflict").GetBoolean());
            OperationalEntityState entity = Assert.Single(harness.Memory.GetCurrentView().Entities);
            Assert.Equal(1, entity.ConflictCount);
            Assert.NotEqual(2915, entity.Position!.X);
            OperationalObservationState observation = Assert.Single(harness.Memory.GetCurrentView().Observations);
            Assert.Equal("location-001", observation.ConstraintLocationAlias);
            Assert.Equal(2915, observation.ConstraintPosition!.X);
            Assert.Equal(220, observation.ConstraintRadiusMeters);
            Assert.Contains("Agia Marina", harness.Memory.FindNamedLocations(Arguments(
                """{"query":"Agia","maxDistanceMeters":50000,"limit":10}""")), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IndependentContradictionRemainsVisibleAndReducesConfidence()
    {
        string root = TemporaryDirectory();
        try
        {
            using OperationalHarness harness = CreateHarness(root);
            harness.Ingest.Ingest(Fixture("operational-observation-batch-v1.json"));
            JsonNode contradiction = JsonNode.Parse(Fixture("operational-observation-batch-v1.json"))!;
            contradiction["messageId"] = "message-observation-2";
            contradiction["sequence"] = 4;
            contradiction["batchId"] = "observation-batch-000002";
            contradiction["observations"]![0]!["observationId"] = "source-observation-000002";
            contradiction["observations"]![0]!["sourceEntityId"] = "net:2:15";
            contradiction["observations"]![0]!["position"] = new JsonArray(3800, 5200, 0);
            Assert.Equal(TelemetryIngestStatus.Applied, harness.Ingest.Ingest(contradiction.ToJsonString()).Status);

            OperationalEntityState entity = Assert.Single(harness.Memory.GetCurrentView().Entities);
            Assert.Equal(1, entity.ConflictCount);
            Assert.Equal(0, entity.CorroborationCount);
            Assert.Equal(0.675, entity.Confidence, 3);
            Assert.All(harness.Memory.GetCurrentView().Observations, item => Assert.Single(item.Contradicts));
            using SqliteConnection database = new($"Data Source={Directory.GetFiles(root, "operational-*.sqlite3").Single()};Pooling=False");
            database.Open();
            using SqliteCommand links = database.CreateCommand();
            links.CommandText = "SELECT COUNT(*) FROM observation_links WHERE relation='contradicts';";
            Assert.Equal(2L, (long)links.ExecuteScalar()!);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static OperationalHarness CreateHarness(string root, bool ingestGazetteer = true)
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore world = new(time);
        OperationalMemoryStore memory = new(world, time, root);
        TelemetryIngestService ingest = new(world, time, memory);
        Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(Handshake()).Status);
        Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(WorldModelTestData.Telemetry(
            timestamp: 10.5, frame: 1000, mapName: "Stratis", mapSize: 8192,
            missionId: "m4-test-mission", sessionId: "m4-source-session-a",
            playerId: "net:2:31", groupId: "net:2:15")).Status);
        if (ingestGazetteer)
            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(Fixture("map-gazetteer-v1.json")).Status);
        return new OperationalHarness(time, world, memory, ingest);
    }

    private static string Handshake() => JsonSerializer.Serialize(new
    {
        schema = "arma-ai-bridge/arma3/session-handshake-v1",
        messageId = "message-handshake-m4",
        missionId = "m4-test-mission",
        sessionId = "m4-source-session-a",
        timestamp = 1.0,
        sequence = 1,
        protocol = new { major = 1, minor = 0 },
        world = new { name = "Stratis", sizeMeters = 8192 },
        viewer = new { side = "WEST", visibility = "own-side" },
        features = new[]
        {
            new { name = "player-telemetry", version = 1 },
            new { name = "environment-query", version = 1 },
            new { name = "friendly-force-picture", version = 1 },
            new { name = "mission-capabilities", version = 1 },
            new { name = "map-gazetteer", version = 1 },
            new { name = "operational-observations", version = 1 }
        }
    });

    private static JsonElement Arguments(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Fixture(string relativePath) => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", relativePath)));
    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "arma-ai-bridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class OperationalHarness : IDisposable
    {
        public OperationalHarness(ManualTimeProvider time, WorldStateStore world, OperationalMemoryStore memory, TelemetryIngestService ingest)
        { Time = time; World = world; Memory = memory; Ingest = ingest; }
        public ManualTimeProvider Time { get; }
        public WorldStateStore World { get; }
        public OperationalMemoryStore Memory { get; }
        public TelemetryIngestService Ingest { get; }
        public void Dispose() { Ingest.Dispose(); Memory.Dispose(); }
    }
}
