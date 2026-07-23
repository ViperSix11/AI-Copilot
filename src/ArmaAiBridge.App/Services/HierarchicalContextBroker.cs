using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class HierarchicalContextBroker : IDisposable
{
    private static readonly HashSet<string> QueryProperties = new(StringComparer.Ordinal)
    {
        "group", "category", "detailLevel", "scope", "entityAliases",
        "referenceAliases", "timeRangeSeconds", "maximumDistanceMeters",
        "limit", "requestedFields", "searchText"
    };

    private readonly IStateRepository _state;
    private readonly IMissionMemoryRepository? _memory;
    private readonly ContextConversationStore _conversation;
    private readonly ITacticalPositionReporter _positions;
    private readonly SqliteMapIntelligenceRepository _mapIntelligence;
    private readonly ContextTraceStore _trace;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public HierarchicalContextBroker(
        IStateRepository state,
        ContextConversationStore conversation,
        ContextTraceStore trace,
        IMissionMemoryRepository? memory = null,
        SqliteMapIntelligenceRepository? mapIntelligence = null,
        TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _memory = memory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _positions = new TacticalPositionReportingService(state);
        _mapIntelligence = mapIntelligence ?? new SqliteMapIntelligenceRepository();
    }

    public string MapDatabasePath => _mapIntelligence.DatabasePath;

    public string Execute(string name, JsonElement arguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return name switch
        {
            "inspect_context_catalogue" => Inspect(arguments),
            "query_context" => Query(arguments),
            "query_long_term_map_intelligence" => QueryLongTerm(arguments),
            _ => throw new InvalidOperationException("Unsupported context tool.")
        };
    }

    public void Reset()
    {
        _conversation.Clear();
        _trace.Reset();
    }

    private static string Inspect(JsonElement root)
    {
        RequireObject(root);
        RequireOnly(root, "group");
        string group = HierarchicalContextCatalogue.NormalizeGroup(RequiredString(root, "group", 80));
        IReadOnlyList<ContextCatalogueCategory> categories = HierarchicalContextCatalogue.Inspect(group);
        return JsonSerializer.Serialize(new
        {
            schema = "arma-ai-bridge/context-catalogue-v1",
            group,
            categories = categories.Select(item => new
            {
                name = item.Category,
                description = item.Description,
                currentlyAvailable = item.Available
            })
        });
    }

    private string Query(JsonElement root)
    {
        RequireObject(root);
        RequireOnly(root, QueryProperties.ToArray());
        string group = HierarchicalContextCatalogue.NormalizeGroup(RequiredString(root, "group", 80));
        string category = HierarchicalContextCatalogue.NormalizeCategory(RequiredString(root, "category", 80));
        if (!HierarchicalContextCatalogue.Contains(group, category))
            throw new InvalidOperationException("The category does not belong to the selected context group.");
        string detail = RequiredEnum(root, "detailLevel", "summary", "detail");
        string scope = RequiredEnum(root, "scope", "current", "near_player", "entity", "area", "mission", "history");
        string[] entityAliases = StringArray(root, "entityAliases", 16, 100);
        string[] referenceAliases = StringArray(root, "referenceAliases", 16, 100);
        int timeRange = NullableInteger(root, "timeRangeSeconds", 0, 1800) ?? 300;
        double maximumDistance = NullableNumber(root, "maximumDistanceMeters", 25, 10000) ?? 2000;
        int limit = RequiredInteger(root, "limit", 1, 64);
        string[] requestedFields = StringArray(root, "requestedFields", 16, 60);
        string searchText = OptionalString(root, "searchText", 300);

        if (group == "long_term_map_intelligence")
            return Unavailable(group, category,
                "Use the dedicated bounded long-term map-intelligence query.");
        if (!HierarchicalContextCatalogue.IsCurrentlyAvailable(group, category))
            return Unavailable(group, category,
                "No authorized structured source currently supplies this category.");

        object result = group switch
        {
            "entities" => Entities(category, detail, scope, entityAliases, maximumDistance, limit),
            "operations" => Operations(category, detail, limit),
            "intelligence" => Intelligence(category, detail, entityAliases, timeRange, limit),
            "geography" => Geography(category, detail, entityAliases, referenceAliases, limit),
            "resources" => Resources(category, detail, maximumDistance, limit, searchText),
            "environment" => EnvironmentContext(category),
            "communications" => Communications(category, detail, limit),
            "events_and_history" => History(category, detail, entityAliases, timeRange, limit),
            "lore_and_rules" => Lore(category, detail, limit),
            "miscellaneous" => Miscellaneous(category, detail, searchText, limit),
            _ => new { available = false }
        };
        return Envelope(group, category, detail, scope, requestedFields, result);
    }

    private string QueryLongTerm(JsonElement root)
    {
        RequireObject(root);
        RequireOnly(root, "category", "scope", "limit");
        string category = RequiredString(root, "category", 80);
        string scope = RequiredString(root, "scope", 160);
        int limit = RequiredInteger(root, "limit", 1, 20);
        string normalizedCategory = HierarchicalContextCatalogue.NormalizeCategory(category);
        if (!HierarchicalContextCatalogue.Contains("long_term_map_intelligence", normalizedCategory))
            throw new InvalidOperationException("Unsupported long-term map-intelligence category.");
        string world = _state.GetDiagnostics().WorldName;
        if (string.IsNullOrWhiteSpace(world))
            throw new InvalidOperationException("The current world is unavailable.");
        return _mapIntelligence.Query(world, normalizedCategory, scope, limit);
    }

    private object Entities(
        string category,
        string detail,
        string scope,
        IReadOnlyCollection<string> entityAliases,
        double maximumDistance,
        int limit)
    {
        StatePlayer? player = _state.GetPlayer();
        if (category == "player")
            return player is null
                ? new { available = false }
                : new
                {
                    available = true,
                    summary = "Current player identity and operational status.",
                    player = new
                    {
                        entityAlias = "player:self",
                        player.Side,
                        groupCallsign = SafeCallsign(player.GroupCallsign),
                        canonicalPositionWithheld = true,
                        privateInventoryWithheld = true
                    }
                };

        if (category is "enemy_contacts" or "unknown_contacts")
        {
            string relationship = category == "enemy_contacts" ? "hostile" : "unknown";
            return ContactProjection(
                _memory?.GetContactTracks(256).Where(item => item.Relationship == relationship) ??
                Array.Empty<MissionContactTrack>(),
                detail,
                entityAliases,
                limit);
        }

        if (category == "groups_and_formations")
        {
            StateFriendlyGroup[] groups = _state.GetFriendlyGroups(128, true)
                .Where(item => entityAliases.Count == 0 || entityAliases.Contains(item.Alias))
                .Take(limit).ToArray();
            return new
            {
                available = true,
                summary = $"{groups.Length} friendly groups matched.",
                records = detail == "summary" ? Array.Empty<object>() : groups.Select(GroupRecord).ToArray()
            };
        }

        StateFriendlyUnit[] units = _state.GetFriendlyUnits(512, true)
            .Where(item => category switch
            {
                "casualties" => !item.Alive || item.Damage > 0,
                "incapacitated_units" => item.Alive &&
                    item.LifeState.Contains("INCAPACITATED", StringComparison.OrdinalIgnoreCase),
                "dead_units" => !item.Alive,
                _ => true
            })
            .Where(item => entityAliases.Count == 0 || entityAliases.Contains(item.Alias))
            .Where(item => scope != "near_player" || player is null ||
                           Distance(item.Position, player.PositionAtl) <= maximumDistance)
            .Take(limit).ToArray();
        return new
        {
            available = true,
            summary = $"{units.Length} friendly units matched.",
            records = detail == "summary" ? Array.Empty<object>() : units.Select(UnitRecord).ToArray()
        };
    }

    private object Operations(string category, string detail, int limit)
    {
        StateTask[] tasks = _state.GetTasks(limit, true).ToArray();
        if (category == "player_stated_intent")
        {
            MissionMemoryEntry[] intent = (_memory?.SearchMemory("mission objective intent plan", limit, 4000) ??
                                           Array.Empty<MissionMemoryEntry>()).ToArray();
            return new
            {
                available = true,
                summary = intent.Length == 0 ? "No retained player-stated intent." : $"{intent.Length} retained intent reports.",
                records = detail == "summary" ? Array.Empty<object>() :
                    intent.Select(MemoryRecord).ToArray()
            };
        }
        int active = tasks.Count(item => item.Active);
        return new
        {
            available = true,
            summary = $"{active} active objectives; {tasks.Length - active} inactive or completed objectives.",
            records = detail == "summary" ? Array.Empty<object>() : tasks.Select(TaskRecord).ToArray()
        };
    }

    private object Intelligence(
        string category,
        string detail,
        IReadOnlyCollection<string> entityAliases,
        int timeRangeSeconds,
        int limit)
    {
        IEnumerable<MissionContactTrack> tracks = _memory?.GetContactTracks(256) ??
                                                   Array.Empty<MissionContactTrack>();
        DateTimeOffset threshold = _timeProvider.GetUtcNow().AddSeconds(-timeRangeSeconds);
        tracks = tracks.Where(item => item.LastObservedAtUtc >= threshold || category == "contact_history");
        tracks = category switch
        {
            "new_contacts" => tracks.Where(item => item.ObservationCount <= 1),
            "lost_contacts" => tracks.Where(item => item.Status == "last-known"),
            "reacquired_contacts" => tracks.Where(item => item.Status == "current" && item.ObservationCount > 1),
            _ => tracks
        };
        return ContactProjection(tracks, detail, entityAliases, limit);
    }

    private object Geography(
        string category,
        string detail,
        IReadOnlyList<string> entityAliases,
        IReadOnlyList<string> referenceAliases,
        int limit)
    {
        if (category == "spatial_relationships")
            return SpatialRelationships(entityAliases, referenceAliases, limit);

        if (category is "map_markers" or "bullseyes" or "landmarks" or
            "points_of_interest" or "areas_and_zones")
        {
            StateMarker[] markers = _state.GetMarkers(512, true)
                .Where(marker => category switch
                {
                    "bullseyes" => marker.ReferenceRole == "bullseye",
                    "map_markers" => true,
                    _ => marker.ReferenceRole == "location"
                })
                .Where(marker => !string.IsNullOrWhiteSpace(marker.ReferenceLabel) ||
                                 !string.IsNullOrWhiteSpace(marker.Text))
                .Take(limit).ToArray();
            return new
            {
                available = true,
                summary = $"{markers.Length} mission references matched.",
                records = detail == "summary" ? Array.Empty<object>() : markers.Select(MarkerRecord).ToArray()
            };
        }

        if (category is "named_locations" or "nearby_references")
        {
            MapGazetteerLocation[] locations = _state.GetNamedLocations(limit: limit).ToArray();
            return new
            {
                available = true,
                summary = $"{locations.Length} official named locations matched.",
                records = detail == "summary" ? Array.Empty<object>() : locations.Select(LocationRecord).ToArray()
            };
        }

        if (category == "objective_areas")
            return Operations("objectives", detail, limit);

        return new
        {
            available = true,
            summary = "Grid and position formatting is available through deterministic local calculations.",
            canonicalPlayerPositionWithheld = true
        };
    }

    private object Resources(
        string category,
        string detail,
        double maximumDistance,
        int limit,
        string searchText)
    {
        string query = searchText.Length > 0 ? searchText : category switch
        {
            "ammunition_sources" => "ammunition ammo magazine resupply crate",
            "medical_support" => "medical medic aid treatment",
            "transport" => "transport vehicle helicopter",
            _ => category.Replace('_', ' ')
        };
        MissionMemoryEntry[] reports = (_memory?.SearchMemory(query, Math.Min(limit, 12), 4000) ??
                                        Array.Empty<MissionMemoryEntry>()).ToArray();
        StateFriendlyUnit[] capableUnits = _state.GetFriendlyUnits(512, false)
            .Where(item => category switch
            {
                "medical_support" => item.DisplayRole.Contains("medic", StringComparison.OrdinalIgnoreCase),
                "ammunition_sources" or "compatible_ammunition" =>
                    (item.MagazineClasses?.Count ?? 0) > 0 ||
                    (item.VehicleMagazineCargo?.Count ?? 0) > 0,
                "weapons" or "recoverable_equipment" =>
                    (item.WeaponClasses?.Count ?? 0) > 0 ||
                    (item.VehicleWeaponCargo?.Count ?? 0) > 0,
                "transport" or "supply_vehicles" =>
                    item.VehicleAlias.Length > 0 &&
                    (item.VehicleTransportCapacity > 0 ||
                     (item.VehicleItemCargo?.Count ?? 0) > 0),
                _ => item.Alive && item.Mobile
            })
            .Where(item => item.Alive)
            .Take(limit).ToArray();
        MissionContactTrack[] possibleEnemySources =
            category == "recoverable_equipment"
                ? (_memory?.GetContactTracks(256)
                    .Where(item => item.Relationship == "hostile" && item.Status == "dead")
                    .Take(Math.Min(limit, 8))
                    .ToArray() ?? Array.Empty<MissionContactTrack>())
                : Array.Empty<MissionContactTrack>();
        return new
        {
            available = true,
            summary = $"{reports.Length} retained resource reports and {capableUnits.Length} matching friendly sources.",
            maximumDistanceMeters = maximumDistance,
            reports = detail == "summary" ? Array.Empty<object>() : reports.Select(MemoryRecord).ToArray(),
            units = detail == "summary"
                ? Array.Empty<object>()
                : capableUnits.Select(item => ResourceUnitRecord(item, category)).ToArray(),
            possibleEnemySources = detail == "summary"
                ? Array.Empty<object>()
                : possibleEnemySources.Select(item => new
                {
                    entityAlias = item.TrackId,
                    classification = ContactNoun(item.ContactType),
                    equipmentStatus = "unknown",
                    note = "Possible recoverable source; equipment has not been observed or inspected."
                }).ToArray(),
            playerPrivateInventoryWithheld = true,
            enemyEquipmentRequiresObservationOrInspection = true
        };
    }

    private object Miscellaneous(string category, string detail, string searchText, int limit)
    {
        string query = searchText.Length > 0
            ? searchText
            : category.Replace('_', ' ');
        MissionMemoryEntry[] records = (_memory?.SearchMemory(
            query,
            Math.Min(limit, 12),
            4000) ?? Array.Empty<MissionMemoryEntry>())
            .Where(item =>
                item.Tags.Contains($"group:miscellaneous", StringComparer.OrdinalIgnoreCase) ||
                item.Provenance == "raw-player-message")
            .Take(limit)
            .ToArray();
        return new
        {
            available = true,
            summary = records.Length == 0
                ? "No relevant miscellaneous context is retained."
                : $"{records.Length} relevant miscellaneous records matched.",
            records = detail == "summary"
                ? Array.Empty<object>()
                : records.Select(MemoryRecord).ToArray(),
            retrievalIsRelevanceBounded = true
        };
    }

    private object EnvironmentContext(string category)
    {
        StateEnvironment? environment = _state.GetEnvironment();
        StateTimeAstronomy? time = _state.GetTimeAstronomy();
        return category switch
        {
            "weather" => environment is null
                ? new { available = false }
                : new
                {
                    available = true,
                    overcast = Math.Round(environment.Overcast, 2),
                    condition = EnvironmentInterpretationService.Classify(environment.Overcast)
                },
            "visibility" => environment is null
                ? new { available = false }
                : new
                {
                    available = true,
                    fog = Math.Round(environment.Fog, 2),
                    overcast = Math.Round(environment.Overcast, 2)
                },
            _ => time is null
                ? new { available = false }
                : new
                {
                    available = true,
                    missionDate = time.MissionDate,
                    localMissionTime = Math.Round(time.Daytime, 2),
                    lighting = time.SunOrMoon < .2 ? "dark" : "daylight"
                }
        };
    }

    private object Communications(string category, string detail, int limit)
    {
        ContextConversationTurn[] turns = _conversation.GetRecent(limit).ToArray();
        return new
        {
            available = true,
            summary = turns.Length == 0 ? "No recent radio turns retained." : $"{turns.Length} recent radio turns retained.",
            records = detail == "summary" ? Array.Empty<object>() : turns.Select(item => new
            {
                item.Role,
                item.Text,
                item.CreatedAtUtc
            }).ToArray()
        };
    }

    private object History(
        string category,
        string detail,
        IReadOnlyCollection<string> entityAliases,
        int timeRangeSeconds,
        int limit)
    {
        if (category == "recent_events")
        {
            MissionMemoryEntry[] events = (_memory?.SearchMemory(
                    string.Empty,
                    Math.Min(limit, 12),
                    5000) ?? Array.Empty<MissionMemoryEntry>())
                .Where(item =>
                    item.Tags.Contains("event-candidate", StringComparer.OrdinalIgnoreCase) ||
                    item.Tags.Contains("event-assessment", StringComparer.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
            return new
            {
                available = true,
                summary = events.Length == 0
                    ? "No recent event records are retained."
                    : $"{events.Length} recent event records matched.",
                records = detail == "summary"
                    ? Array.Empty<object>()
                    : events.Select(MemoryRecord).ToArray()
            };
        }
        if (category is "contact_history" or "position_history")
            return Intelligence("contact_history", detail, entityAliases, timeRangeSeconds, limit);
        if (category is "communication_history" or "player_action_history")
            return Communications(category, detail, limit);
        return new { available = false, reason = "No authorized structured history source currently supplies this category." };
    }

    private object Lore(string category, string detail, int limit)
    {
        string scope = category switch
        {
            "mission_lore" => "Mission",
            "map_lore" => "Map",
            "player_lore" => "Player",
            "target_lore" => "Target",
            "common_lore" => "Common",
            _ => string.Empty
        };
        LoreSection[] sections = (_memory?.GetLoreSections() ?? Array.Empty<LoreSection>())
            .Where(item => item.Enabled)
            .Where(item => scope.Length == 0 || string.Equals(item.Scope, scope, StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToArray();
        return new
        {
            available = true,
            summary = sections.Length == 0 ? "No matching lore is available." : $"{sections.Length} lore sections matched.",
            records = detail == "summary" ? Array.Empty<object>() : sections.Select(item => new
            {
                item.Scope,
                item.Content,
                item.UpdatedAtUtc,
                untrustedContext = true
            }).ToArray()
        };
    }

    private object ContactProjection(
        IEnumerable<MissionContactTrack> source,
        string detail,
        IReadOnlyCollection<string> entityAliases,
        int limit)
    {
        MissionContactTrack[] tracks = source
            .Where(item => entityAliases.Count == 0 || entityAliases.Contains(item.TrackId))
            .OrderBy(item => item.Status == "current" ? 0 : 1)
            .ThenByDescending(item => item.LastObservedAtUtc)
            .Take(limit).ToArray();
        object[] groups = GroupContacts(tracks).ToArray();
        return new
        {
            available = true,
            summary = new
            {
                current = tracks.Count(item => item.Status == "current"),
                lastKnown = tracks.Count(item => item.Status == "last-known"),
                confirmedDead = tracks.Count(item => item.Status == "dead"),
                presentationGroups = groups.Length,
                observationsMayOverlap = true
            },
            groups,
            records = detail == "summary" ? Array.Empty<object>() : tracks.Select(ContactRecord).ToArray()
        };
    }

    private IEnumerable<object> GroupContacts(IReadOnlyList<MissionContactTrack> tracks)
    {
        List<List<MissionContactTrack>> groups = new();
        foreach (MissionContactTrack track in tracks.Where(item => item.Status != "dead"))
        {
            List<MissionContactTrack>? group = groups.FirstOrDefault(candidate =>
                candidate.All(item => ContactPresentationPolicy.CanCluster(item, track)));
            if (group is null) groups.Add(group = new List<MissionContactTrack>());
            group.Add(track);
        }
        return groups.Select((group, index) =>
        {
            WorldPosition center = new(
                group.Average(item => item.EstimatedPosition.X),
                group.Average(item => item.EstimatedPosition.Y),
                group.Average(item => item.EstimatedPosition.Z));
            return (object)new
            {
                entityAlias = $"contact-group:{index + 1}",
                memberCount = group.Count,
                relationship = group[0].Relationship,
                classification = group.Select(item => ContactNoun(item.ContactType)).Distinct().ToArray(),
                status = group.Any(item => item.Status == "current") ? "current" : "last-known",
                position = _positions.Describe(center).Text,
                uncertaintyMeters = Math.Ceiling(group.Max(item => item.UncertaintyRadiusMeters) / 10) * 10,
                memberAliases = group.Select(item => item.TrackId).ToArray()
            };
        });
    }

    private object SpatialRelationships(
        IReadOnlyList<string> entityAliases,
        IReadOnlyList<string> referenceAliases,
        int limit)
    {
        if (entityAliases.Count == 0 || referenceAliases.Count == 0)
            throw new InvalidOperationException("Spatial relationships require at least one entity alias and one reference alias.");
        List<object> results = new();
        foreach (string entityAlias in entityAliases.Take(limit))
        {
            WorldPosition entity = ResolvePosition(entityAlias);
            foreach (string referenceAlias in referenceAliases.Take(limit))
            {
                WorldPosition reference = ResolvePosition(referenceAlias);
                double distance = Distance(reference, entity);
                results.Add(new
                {
                    entityAlias,
                    referenceAlias,
                    distance = TacticalPositionReportingService.FormatDistance(distance),
                    direction = TacticalPositionReportingService.Direction(reference, entity),
                    description = distance < 25
                        ? $"at {ReferenceLabel(referenceAlias)}"
                        : $"{TacticalPositionReportingService.FormatDistance(distance)} {TacticalPositionReportingService.Direction(reference, entity)} of {ReferenceLabel(referenceAlias)}"
                });
            }
        }
        return new { available = true, calculatedLocally = true, records = results };
    }

    private WorldPosition ResolvePosition(string alias)
    {
        if (alias == "player:self")
            return _state.GetPlayer()?.PositionAtl ?? throw new InvalidOperationException("Player position is unavailable locally.");
        MissionContactTrack? contact = _memory?.GetContactTracks(256, true)
            .FirstOrDefault(item => item.TrackId == alias);
        if (contact is not null) return contact.EstimatedPosition;
        StateMarker? marker = _state.GetMarkers(512, true).FirstOrDefault(item => item.Alias == alias);
        if (marker is not null) return marker.Position;
        StateFriendlyGroup? group = _state.GetFriendlyGroups(128, true).FirstOrDefault(item => item.Alias == alias);
        if (group is not null) return group.LeaderPosition;
        if (alias.StartsWith("named:", StringComparison.Ordinal))
        {
            string name = alias[6..].Replace('_', ' ');
            MapGazetteerLocation? location = _state.GetNamedLocations(name, 10)
                .FirstOrDefault(item => NormalizeAlias(item.Name) == alias);
            if (location is not null) return new WorldPosition(location.X, location.Y, 0);
        }
        throw new InvalidOperationException("The requested spatial alias is unknown in the active mission.");
    }

    private string ReferenceLabel(string alias)
    {
        if (alias == "player:self") return "your reported operating position";
        StateMarker? marker = _state.GetMarkers(512, true).FirstOrDefault(item => item.Alias == alias);
        if (marker is not null)
            return TacticalPositionReportingService.SafeLabel(
                marker.ReferenceLabel.Length > 0 ? marker.ReferenceLabel : marker.Text);
        StateFriendlyGroup? group = _state.GetFriendlyGroups(128, true).FirstOrDefault(item => item.Alias == alias);
        if (group is not null) return SafeCallsign(group.Callsign);
        if (alias.StartsWith("named:", StringComparison.Ordinal)) return alias[6..].Replace('_', ' ');
        return "the selected reference";
    }

    private object ContactRecord(MissionContactTrack item) => new
    {
        entityAlias = item.TrackId,
        classification = ContactNoun(item.ContactType),
        item.Relationship,
        item.Status,
        position = _positions.Describe(item.EstimatedPosition).Text,
        uncertaintyMeters = Math.Round(item.UncertaintyRadiusMeters),
        lastObservedSecondsAgo = Math.Max(0, Math.Round((_timeProvider.GetUtcNow() - item.LastObservedAtUtc).TotalSeconds)),
        item.ObservationCount,
        item.Corroborated,
        reporters = item.ReporterCallsigns.Select(SafeCallsign).Where(value => value.Length > 0).ToArray()
    };

    private object GroupRecord(StateFriendlyGroup item) => new
    {
        entityAlias = item.Alias,
        callsign = SafeCallsign(item.Callsign),
        memberCount = item.MemberAliases.Count,
        item.Formation,
        item.Behaviour,
        item.CombatMode,
        position = _positions.Describe(item.LeaderPosition).Text,
        stale = item.Metadata.IsStale
    };

    private object UnitRecord(StateFriendlyUnit item) => new
    {
        entityAlias = item.Alias,
        groupAlias = item.GroupAlias,
        role = SafeText(item.DisplayRole, 80),
        item.Alive,
        item.LifeState,
        item.Mobile,
        damageState = item.Damage switch { <= 0 => "uninjured", < .5 => "wounded", _ => "severely-wounded" },
        position = _positions.Describe(item.Position).Text,
        stale = item.Metadata.IsStale
    };

    private object ResourceUnitRecord(StateFriendlyUnit item, string category) => new
    {
        entityAlias = item.Alias,
        groupAlias = item.GroupAlias,
        role = SafeText(item.DisplayRole, 80),
        vehicleAlias = item.VehicleAlias.Length == 0 ? null : item.VehicleAlias,
        relevantEquipment = category switch
        {
            "ammunition_sources" or "compatible_ammunition" =>
                (item.MagazineClasses ?? Array.Empty<string>())
                    .Concat(item.VehicleMagazineCargo ?? Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal).Take(24).ToArray(),
            "weapons" or "recoverable_equipment" =>
                (item.WeaponClasses ?? Array.Empty<string>())
                    .Concat(item.VehicleWeaponCargo ?? Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal).Take(16).ToArray(),
            "transport" or "supply_vehicles" =>
                (item.VehicleItemCargo ?? Array.Empty<string>()).Take(24).ToArray(),
            _ => (item.ItemClasses ?? Array.Empty<string>()).Take(16).ToArray()
        },
        transportCapacity = item.VehicleTransportCapacity,
        position = _positions.Describe(item.Position).Text,
        stale = item.Metadata.IsStale
    };

    private object TaskRecord(StateTask item) => new
    {
        entityAlias = item.Alias,
        title = SafeText(item.Title, 160),
        description = SafeText(item.Description, 240),
        item.Type,
        item.Status,
        item.Active,
        position = item.Destination is null ? null : _positions.Describe(item.Destination).Text,
        stale = item.Metadata.IsStale
    };

    private object MarkerRecord(StateMarker item) => new
    {
        entityAlias = item.Alias,
        label = SafeText(item.ReferenceLabel.Length > 0 ? item.ReferenceLabel : item.Text, 160),
        role = item.ReferenceRole,
        item.Shape,
        position = _positions.Describe(item.Position).Text,
        stale = item.Metadata.IsStale
    };

    private static object LocationRecord(MapGazetteerLocation item) => new
    {
        entityAlias = NormalizeAlias(item.Name),
        officialName = SafeText(item.Name, 160),
        item.Type,
        grid = TacticalPositionReportingService.Grid(new WorldPosition(item.X, item.Y, 0)),
        sizeMeters = new[] { Math.Round(item.RadiusA), Math.Round(item.RadiusB) }
    };

    private static object MemoryRecord(MissionMemoryEntry item) => new
    {
        reportAlias = $"report:{item.Id}",
        content = item.Text,
        item.Provenance,
        item.UpdatedAtUtc,
        item.Tags
    };

    private static string Envelope(
        string group,
        string category,
        string detail,
        string scope,
        IReadOnlyList<string> requestedFields,
        object result)
        => JsonSerializer.Serialize(new
        {
            schema = "arma-ai-bridge/context-result-v1",
            group,
            category,
            detailLevel = detail,
            scope,
            requestedFields,
            result,
            truncated = false
        });

    private static string Unavailable(string group, string category, string reason)
        => JsonSerializer.Serialize(new
        {
            schema = "arma-ai-bridge/context-result-v1",
            group,
            category,
            available = false,
            reason,
            truncated = false
        });

    private static void RequireObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Tool arguments must be an object.");
    }

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        HashSet<string> names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!names.Contains(property.Name))
                throw new InvalidOperationException($"Unsupported tool argument: {property.Name}.");
    }

    private static string RequiredString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{name} is required.");
        string result = (value.GetString() ?? string.Empty).Trim();
        if (result.Length is 0 || result.Length > maximum || result.Any(char.IsControl))
            throw new InvalidOperationException($"{name} is invalid.");
        return result;
    }

    private static string OptionalString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{name} must be text or null.");
        string result = (value.GetString() ?? string.Empty).Trim();
        if (result.Length > maximum || result.Any(char.IsControl))
            throw new InvalidOperationException($"{name} is invalid.");
        return result;
    }

    private static string RequiredEnum(JsonElement root, string name, params string[] allowed)
    {
        string value = RequiredString(root, name, 80).ToLowerInvariant().Replace('-', '_');
        return allowed.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new InvalidOperationException($"{name} is unsupported.");
    }

    private static int RequiredInteger(JsonElement root, string name, int minimum, int maximum)
        => NullableInteger(root, name, minimum, maximum) ??
           throw new InvalidOperationException($"{name} is required.");

    private static int? NullableInteger(JsonElement root, string name, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) &&
               result >= minimum && result <= maximum
            ? result
            : throw new InvalidOperationException($"{name} is out of range.");
    }

    private static double? NullableNumber(JsonElement root, string name, double minimum, double maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) &&
               double.IsFinite(result) && result >= minimum && result <= maximum
            ? result
            : throw new InvalidOperationException($"{name} is out of range.");
    }

    private static string[] StringArray(JsonElement root, string name, int maximumItems, int maximumLength)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{name} must be an array.");
        JsonElement[] values = value.EnumerateArray().Take(maximumItems + 1).ToArray();
        if (values.Length > maximumItems || values.Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidOperationException($"{name} is invalid.");
        string[] result = values.Select(item => (item.GetString() ?? string.Empty).Trim()).ToArray();
        if (result.Any(item => item.Length is 0 || item.Length > maximumLength || item.Any(char.IsControl)))
            throw new InvalidOperationException($"{name} is invalid.");
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string SafeText(string? value, int maximum)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Any(char.IsControl)) return string.Empty;
        return text.Length <= maximum ? text : text[..maximum];
    }

    private static string SafeCallsign(string? value) => SafeText(value, 80);
    private static string NormalizeAlias(string value)
        => "named:" + string.Concat(value.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '_')).Trim('_');

    private static string ContactNoun(string type) => type switch
    {
        "person" => "infantry",
        "ground-vehicle" => "vehicle",
        "air" => "aircraft",
        "naval" => "vessel",
        "static-weapon" => "static weapon",
        "unmanned-ground" => "unmanned vehicle",
        "unmanned-air" => "unmanned aircraft",
        _ => "contact"
    };

    private static double Distance(WorldPosition left, WorldPosition right)
        => Math.Sqrt(Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mapIntelligence.Dispose();
    }
}
