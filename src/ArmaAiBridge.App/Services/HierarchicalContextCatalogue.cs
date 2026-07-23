using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public static class HierarchicalContextCatalogue
{
    public static readonly string[] Groups =
    [
        "entities",
        "operations",
        "intelligence",
        "geography",
        "resources",
        "environment",
        "communications",
        "events_and_history",
        "lore_and_rules",
        "long_term_map_intelligence",
        "miscellaneous"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> Categories =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["entities"] =
            [
                "player", "friendly_units", "enemy_contacts", "unknown_contacts",
                "civilian_entities", "groups_and_formations", "vehicles", "aircraft",
                "buildings", "static_objects", "important_persons", "casualties",
                "incapacitated_units", "dead_units", "supply_objects",
                "mission_relevant_objects"
            ],
            ["operations"] =
            [
                "current_mission", "mission_phases", "objectives", "unit_assignments",
                "unit_tasks", "unit_intent", "player_stated_intent", "commander_intent",
                "orders", "order_history", "task_dependencies", "supporting_tasks",
                "supported_tasks", "success_conditions", "failure_conditions",
                "operational_constraints", "mission_progress", "mission_consequences",
                "replacement_responsibilities", "alternative_plans"
            ],
            ["intelligence"] =
            [
                "current_contacts", "contact_history", "contact_classification",
                "contact_lifecycle", "new_contacts", "lost_contacts",
                "reacquired_contacts", "nearby_contacts", "contact_grouping",
                "observations", "observation_history", "information_sources",
                "confidence", "threat_assessments", "hypotheses",
                "unresolved_questions", "knowledge_by_side", "knowledge_by_unit",
                "information_freshness", "conflicting_reports"
            ],
            ["geography"] =
            [
                "exact_positions", "map_markers", "bullseyes", "landmarks",
                "points_of_interest", "named_locations", "areas_and_zones", "routes",
                "route_corridors", "roads", "bridges", "buildings", "terrain",
                "elevation", "map_grids", "spatial_relationships",
                "nearby_references", "search_areas", "objective_areas", "choke_points",
                "observation_positions", "accessible_approaches"
            ],
            ["resources"] =
            [
                "ammunition_sources", "compatible_ammunition", "weapons",
                "medical_support", "medical_supplies", "transport", "fuel",
                "repair_capability", "engineering_capability",
                "reconnaissance_capability", "artillery_support", "air_support",
                "reinforcements", "supply_points", "supply_vehicles",
                "recoverable_equipment", "unit_capabilities", "lost_capabilities",
                "replacement_capabilities", "available_support_assets"
            ],
            ["environment"] =
            [
                "weather", "visibility", "time_of_day", "lighting", "wind", "rain",
                "fog", "smoke", "fire", "flooding", "temporary_hazards",
                "terrain_accessibility", "current_environmental_obstacles",
                "road_conditions", "landing_zone_suitability"
            ],
            ["communications"] =
            [
                "recent_radio_messages", "message_history_by_topic",
                "orders_transmitted", "acknowledgements", "message_delivery_status",
                "communication_availability", "radio_range", "relay_availability",
                "communication_quality", "unresponsive_units",
                "communication_failures", "player_confirmations",
                "player_rejections", "requests_for_repetition",
                "last_known_communication", "information_already_reported"
            ],
            ["events_and_history"] =
            [
                "recent_events", "entity_history", "contact_history",
                "position_history", "mission_history", "objective_history",
                "order_history", "casualty_history", "communication_history",
                "resource_history", "player_action_history", "ai_decision_history",
                "changes_since_time", "events_in_area"
            ],
            ["lore_and_rules"] =
            [
                "mission_lore", "map_lore", "player_lore", "target_lore",
                "common_lore", "rules_of_engagement", "faction_relationships",
                "narrative_constraints", "communication_style",
                "mission_restrictions", "player_defined_restrictions",
                "known_background_information"
            ],
            ["long_term_map_intelligence"] =
            [
                "static_buildings", "roads_and_routes", "bridges_and_crossings",
                "choke_points", "observation_positions", "terrain_characteristics",
                "permanent_hazards", "entrances_and_exits", "important_landmarks",
                "historical_navigation_data", "archived_area_assessments",
                "structural_information", "known_vehicle_restrictions",
                "persistent_tactical_relationships"
            ],
            ["miscellaneous"] =
            [
                "off_topic_conversation", "general_questions",
                "casual_conversation", "personal_remarks", "jokes",
                "non_mission_technical_topics", "previous_unrelated_topics",
                "uncategorized_information"
            ]
        };

    private static readonly HashSet<string> CurrentlyAvailable = new(StringComparer.Ordinal)
    {
        "entities/player", "entities/friendly_units", "entities/enemy_contacts",
        "entities/unknown_contacts", "entities/groups_and_formations",
        "entities/casualties", "entities/incapacitated_units", "entities/dead_units",
        "operations/current_mission", "operations/objectives", "operations/unit_assignments",
        "operations/unit_tasks", "operations/mission_progress",
        "operations/player_stated_intent",
        "intelligence/current_contacts", "intelligence/contact_history",
        "intelligence/contact_classification", "intelligence/contact_lifecycle",
        "intelligence/new_contacts", "intelligence/lost_contacts",
        "intelligence/reacquired_contacts", "intelligence/nearby_contacts",
        "intelligence/contact_grouping", "intelligence/observations",
        "intelligence/observation_history", "intelligence/information_sources",
        "intelligence/confidence", "intelligence/information_freshness",
        "geography/map_markers", "geography/bullseyes", "geography/landmarks",
        "geography/points_of_interest", "geography/named_locations",
        "geography/areas_and_zones", "geography/map_grids",
        "geography/spatial_relationships", "geography/nearby_references",
        "geography/objective_areas",
        "resources/ammunition_sources", "resources/compatible_ammunition",
        "resources/weapons", "resources/medical_support",
        "resources/transport", "resources/supply_vehicles",
        "resources/recoverable_equipment", "resources/unit_capabilities",
        "resources/available_support_assets",
        "environment/weather", "environment/visibility",
        "environment/time_of_day", "environment/lighting",
        "communications/recent_radio_messages",
        "communications/message_history_by_topic",
        "communications/player_confirmations", "communications/player_rejections",
        "communications/requests_for_repetition",
        "communications/information_already_reported",
        "events_and_history/recent_events", "events_and_history/contact_history",
        "events_and_history/position_history",
        "events_and_history/communication_history",
        "events_and_history/player_action_history",
        "lore_and_rules/mission_lore", "lore_and_rules/map_lore",
        "lore_and_rules/player_lore", "lore_and_rules/target_lore",
        "lore_and_rules/common_lore", "lore_and_rules/communication_style",
        "lore_and_rules/known_background_information",
        "miscellaneous/off_topic_conversation", "miscellaneous/general_questions",
        "miscellaneous/casual_conversation", "miscellaneous/personal_remarks",
        "miscellaneous/jokes", "miscellaneous/non_mission_technical_topics",
        "miscellaneous/previous_unrelated_topics",
        "miscellaneous/uncategorized_information"
    };

    public static IReadOnlyList<ContextCatalogueCategory> Inspect(string group)
    {
        string normalized = NormalizeGroup(group);
        return Categories[normalized]
            .Select(category => new ContextCatalogueCategory(
                normalized,
                category,
                Description(category),
                CurrentlyAvailable.Contains($"{normalized}/{category}")))
            .ToArray();
    }

    public static bool Contains(string group, string category)
    {
        string normalized = NormalizeGroup(group);
        return Categories[normalized].Contains(NormalizeCategory(category), StringComparer.Ordinal);
    }

    public static bool IsCurrentlyAvailable(string group, string category)
        => CurrentlyAvailable.Contains($"{NormalizeGroup(group)}/{NormalizeCategory(category)}");

    public static string NormalizeGroup(string value)
    {
        string normalized = Normalize(value);
        return Groups.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : throw new InvalidOperationException("Unsupported context group.");
    }

    public static string NormalizeCategory(string value) => Normalize(value);

    private static string Normalize(string value)
        => (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private static string Description(string category)
        => category.Replace('_', ' ');
}
