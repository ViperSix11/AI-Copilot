using System.Globalization;
using System.Text;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed record TacticalEvidenceReport(
    string CandidateEvidence,
    string SelectedEvidence,
    string FusedInterpretation,
    string ModelContext,
    int CandidateCount,
    int SelectedCount);

/// <summary>
/// Projects the closed tactical snapshot into one local evidence pool, selects
/// purpose-specific facts, fuses provenance without destructive overwrites,
/// and renders the only tactical context supplied to OpenAI.
/// </summary>
public static class TacticalEvidencePipeline
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "about", "advised", "again", "also", "another", "around", "bear", "been", "being", "corner",
        "could", "current", "does", "from", "have", "here", "into", "just", "movement", "near", "papa",
        "please", "possible", "report", "reported", "seems", "south", "that", "their", "there", "these",
        "they", "this", "those", "what", "when", "where", "which", "with", "would", "your"
    };
    private static readonly string[] TacticalReportTerms =
    {
        "enemy", "hostile", "contact", "tank", "vehicle", "infantry", "soldier", "tower", "dish", "building",
        "bridge", "road", "checkpoint", "bunker", "fortification", "landmark", "object", "crate", "cache",
        "mine", "destroyed", "damaged", "blocked", "movement"
    };

    public static TacticalEvidenceReport Build(string snapshotJson, string? question)
    {
        using JsonDocument document = JsonDocument.Parse(snapshotJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "schema") != TacticalSnapshotBuilder.Schema)
            throw new InvalidOperationException("The tactical snapshot cannot be interpreted.");

        string input = NormalizeWhitespace(question ?? string.Empty, 2000);
        List<Evidence> candidates = Project(root, input);
        IReadOnlyList<Evidence> selected = Select(candidates, input);
        string candidateView = RenderEvidenceView("ALL BOUNDED EVIDENCE CANDIDATES", candidates, selected);
        string selectedView = RenderEvidenceView("DETERMINISTICALLY SELECTED EVIDENCE", selected, selected);
        string fused = RenderFused(selected);
        string modelContext = RenderModelContext(selected, input, True(root, "modelPayloadTruncated"));
        return new TacticalEvidenceReport(candidateView, selectedView, fused, modelContext, candidates.Count, selected.Count);
    }

    private static List<Evidence> Project(JsonElement root, string input)
    {
        List<Evidence> result = new();
        int sequence = 0;
        void Add(string category, string provenance, string statement, string confidence, string freshness,
            bool negative = false, bool always = false, bool currentReport = false, string? original = null)
        {
            string clean = NormalizeWhitespace(statement, 2000);
            if (clean.Length == 0) return;
            string observed = currentReport ? "current-turn" : freshness switch
            {
                "current" => "current-snapshot",
                "stale" => "last-observed; see statement age",
                "retained" => "stored-record",
                _ => freshness
            };
            result.Add(new Evidence($"evidence-{++sequence:000}", category, provenance, clean,
                observed, confidence, freshness, negative, always, currentReport, original ?? clean, Terms(clean)));
        }

        if (LooksLikeTacticalReport(input))
            Add("player-report", "player-reported", $"You report: {input}", "unconfirmed", "current",
                currentReport: true, original: input);

        if (Object(root, "player", out JsonElement player))
        {
            string side = Text(player, "side");
            string callsign = Text(player, "groupCallsign");
            if (side.Length > 0) Add("player", "canonical-state", $"Current side: {side}.", "high", "current", always: true);
            if (callsign.Length > 0) Add("player", "canonical-state", $"Callsign: {callsign}.", "high", "current", always: true);
        }
        Add("privacy-boundary", "local-policy",
            "The player's current position, grid and elevation are deliberately unavailable to the model and must not be inferred.",
            "authoritative", "current", always: true);

        if (Object(root, "environment", out JsonElement environment))
        {
            if (True(environment, "unavailable"))
                Add("environment", "canonical-state", "Current overcast information is unavailable.", "high", "current", negative: true);
            else
                Add("environment", "canonical-state",
                    $"Current overcast is {Number(environment, "overcast")}; locally classified as {ValueOrUnavailable(Text(environment, "condition"))}.",
                    "high", "current");
        }

        if (Object(root, "time", out JsonElement time))
        {
            if (True(time, "unavailable"))
                Add("mission-time", "canonical-state", "Current mission time is unavailable.", "high", "current", negative: true);
            else
            {
                string date = time.TryGetProperty("missionDate", out JsonElement dateValue) && dateValue.ValueKind == JsonValueKind.Array
                    ? string.Join('-', dateValue.EnumerateArray().Select(x => x.GetInt32().ToString(CultureInfo.InvariantCulture)))
                    : "unavailable";
                string daytime = NumberOrNull(time, "daytime", out double value) ? FormatDaytime(value) : "unavailable";
                Add("mission-time", "canonical-state",
                    $"Mission date is {date}; local mission time is approximately {daytime}; daylight condition is {ValueOrUnavailable(Text(time, "daylight"))}.",
                    "high", "current");
            }
        }

        ProjectFriendlies(root, Add);
        ProjectContacts(root, Add);
        ProjectObjectives(root, Add);
        ProjectMarkers(root, Add);
        ProjectSpatialCorroboration(root, input, Add);
        ProjectMemory(root, Add);
        ProjectLore(root, Add);
        Add("context-boundary", "local-policy",
            True(root, "modelPayloadTruncated")
                ? "The bounded tactical snapshot was truncated; selected records must not be described as a complete picture."
                : "The evidence pool is complete only within the accepted bounded own-side and mission picture.",
            "authoritative", "current", always: true);
        return result;
    }

    private static void ProjectFriendlies(JsonElement root, Action<string, string, string, string, string, bool, bool, bool, string?> add)
    {
        if (!Object(root, "friendlyForces", out JsonElement friendlies)) return;
        if (Object(friendlies, "summary", out JsonElement summary))
        {
            int groups = IntegerValue(summary, "groupCount"), units = IntegerValue(summary, "unitCount");
            add("friendly-force", "canonical-state",
                $"Current friendly strength: {groups} groups and {units} units; {IntegerValue(summary, "woundedCount")} wounded, " +
                $"{IntegerValue(summary, "incapacitatedCount")} incapacitated and {IntegerValue(summary, "deadCount")} dead.",
                "high", "current", groups == 0 && units == 0, false, false, null);
        }
        if (!Array(root: friendlies, "groups", out JsonElement groupsValue)) return;
        foreach (JsonElement group in groupsValue.EnumerateArray())
        {
            StringBuilder statement = new();
            statement.Append("Friendly group ").Append(ValueOrUnavailable(Text(group, "callsign"))).Append(" has ")
                .Append(IntegerValue(group, "memberCount")).Append(" members; element type ")
                .Append(ValueOrUnavailable(Text(group, "elementType"))).Append("; composition ")
                .Append(ValueOrUnavailable(Text(group, "compositionSummary")));
            bool stale = True(group, "stale");
            string position = Text(group, "positionDescription");
            if (position.Length > 0) statement.Append("; ").Append(position);
            if (stale) statement.Append("; last-known/stale");
            statement.Append('.');
            add("friendly-force", "canonical-state", statement.ToString(), stale ? "medium" : "high", stale ? "stale" : "current", false, false, false, null);
        }
    }

    private static void ProjectObjectives(JsonElement root, Action<string, string, string, string, string, bool, bool, bool, string?> add)
    {
        if (!Object(root, "objectives", out JsonElement objectives) ||
            !Array(objectives, "records", out JsonElement records)) return;
        foreach (JsonElement objective in records.EnumerateArray())
        {
            string title = Text(objective, "title").Trim();
            if (title.Length == 0) continue;
            StringBuilder statement = new($"Objective {title}");
            string position = Text(objective, "positionDescription");
            if (position.Length > 0) statement.Append("; ").Append(position);
            string status = Text(objective, "status");
            if (status.Length > 0) statement.Append("; status ").Append(status);
            statement.Append('.');
            bool stale = True(objective, "stale");
            add("objective", "canonical-state", statement.ToString(), stale ? "medium" : "high",
                stale ? "stale" : "current", false, false, false, title);
        }
    }

    private static void ProjectContacts(JsonElement root, Action<string, string, string, string, string, bool, bool, bool, string?> add)
    {
        if (!Object(root, "enemyContacts", out JsonElement contacts)) return;
        int current = 0, lastKnown = 0, dead = 0;
        if (Object(contacts, "summary", out JsonElement summary))
        {
            current = IntegerValue(summary, "currentEnemyContactCount");
            lastKnown = IntegerValue(summary, "lastKnownEnemyContactCount");
            dead = IntegerValue(summary, "confirmedDeadEnemyContactCount");
        }
        int total = current + lastKnown + dead;
        add("hostile-summary", "canonical-own-side-observation",
            total == 0
                ? "No hostile activity is currently known."
                : $"Current hostile picture: {current} current, {lastKnown} last-known and {dead} confirmed dead.",
            "high", "current", total == 0, false, false, null);

        if (total > 0)
        {
            string composition = CurrentHostileComposition(contacts);
            int currentClusters = CurrentHostileClusterCount(contacts);
            string clusterText = currentClusters == 0
                ? "No geographic grouping is supported."
                : $"The current observations form {currentClusters} geographic presentation " +
                  $"{(currentClusters == 1 ? "cluster" : "clusters")}.";
            add("hostile-strength", "derived-local",
                $"Supported hostile strength estimate: {current} current observed {(current == 1 ? "contact" : "contacts")} " +
                $"and {lastKnown} last-known. " +
                $"Current identified composition: {composition}. {clusterText} " +
                "This is an observed-contact estimate and may not equal total personnel when observations overlap or vehicle crews are unknown.",
                "medium", "current", false, false, false, null);
        }

        int currentUnknown = Object(contacts, "summary", out JsonElement unknownSummary)
            ? IntegerValue(unknownSummary, "currentUnknownContactCount") : 0;
        int lastKnownUnknown = Object(contacts, "summary", out unknownSummary)
            ? IntegerValue(unknownSummary, "lastKnownUnknownContactCount") : 0;
        int unknownTotal = currentUnknown + lastKnownUnknown;
        add("unknown-summary", "canonical-own-side-observation",
            unknownTotal == 0
                ? "No unidentified contacts are currently known."
                : $"Current unidentified-contact picture: {currentUnknown} current and {lastKnownUnknown} last-known.",
            "high", "current", unknownTotal == 0, false, false, null);

        HashSet<string> groupedTrackReferences = new(StringComparer.Ordinal);
        if (Array(contacts, "groups", out JsonElement groups))
        {
            foreach (JsonElement group in groups.EnumerateArray())
            {
                if (Array(group, "memberTrackReferences", out JsonElement members))
                    foreach (JsonElement member in members.EnumerateArray())
                        if (member.ValueKind == JsonValueKind.String)
                            groupedTrackReferences.Add(member.GetString() ?? string.Empty);
                StringBuilder statement = new();
                statement.Append(ValueOrUnavailable(Text(group, "description"))).Append(" with ")
                    .Append(IntegerValue(group, "memberCount")).Append(" members; status ")
                    .Append(ValueOrUnavailable(Text(group, "status")));
                string position = Text(group, "positionDescription");
                if (position.Length > 0) statement.Append("; ").Append(position);
                statement.Append("; uncertainty about ").Append(Number(group, "positionUncertaintyMeters"))
                    .Append(" metres; last seen ").Append(Number(group, "lastSeenSecondsAgo")).Append(" seconds ago.");
                bool stale = Text(group, "status") != "current";
                string category = Text(group, "description").StartsWith("unknown", StringComparison.OrdinalIgnoreCase)
                    ? "unknown-contact" : "hostile-contact";
                add(category, "canonical-own-side-observation", statement.ToString(), stale ? "medium" : "high",
                    stale ? "stale" : "current", false, false, false, null);
            }
        }
        if (!Array(contacts, "records", out JsonElement records)) return;
        foreach (JsonElement contact in records.EnumerateArray())
        {
            if (!True(contact, "focused") &&
                groupedTrackReferences.Contains(Text(contact, "contactTrackReference"))) continue;
            StringBuilder statement = new();
            statement.Append(ValueOrUnavailable(Text(contact, "description"))).Append("; status ")
                .Append(ValueOrUnavailable(Text(contact, "status")));
            string position = Text(contact, "positionDescription");
            if (position.Length > 0) statement.Append("; ").Append(position);
            statement.Append("; uncertainty about ").Append(Number(contact, "positionUncertaintyMeters"))
                .Append(" metres; last seen ").Append(Number(contact, "lastSeenSecondsAgo"))
                .Append(" seconds ago");
            if (Array(contact, "reporterCallsigns", out JsonElement reporters))
            {
                string[] callsigns = reporters.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()?.Trim() ?? string.Empty).Where(item => item.Length > 0).Take(8).ToArray();
                statement.Append(callsigns.Length == 0
                    ? "; reported by a friendly observer"
                    : $"; reported by {string.Join(" and ", callsigns)}");
            }
            statement.Append('.');
            bool stale = True(contact, "stale") || Text(contact, "status") != "current";
            string category = True(contact, "focused") ? "hostile-focus" :
                Text(contact, "description").StartsWith("unknown", StringComparison.OrdinalIgnoreCase) ? "unknown-contact" : "hostile-contact";
            add(category, "canonical-own-side-observation", statement.ToString(), stale ? "medium" : "high",
                stale ? "stale" : "current", false, false, false, null);
        }
    }

    private static void ProjectMarkers(JsonElement root, Action<string, string, string, string, string, bool, bool, bool, string?> add)
    {
        if (!Object(root, "markers", out JsonElement markers) || !Array(markers, "records", out JsonElement records)) return;
        foreach (JsonElement marker in records.EnumerateArray())
        {
            string name = Text(marker, "text").Trim();
            if (name.Length == 0 || !name.Any(char.IsLetterOrDigit)) continue;
            string shape = Text(marker, "shape").ToUpperInvariant() switch
            {
                "RECTANGLE" => "rectangular area",
                "ELLIPSE" => "elliptical area",
                _ => "point location"
            };
            bool stale = True(marker, "stale");
            string position = Text(marker, "positionDescription");
            string statement = position.Length == 0
                ? $"{name} is a known {shape}."
                : $"{name} is a known {shape}; {position}.";
            add("location", "mission-annotation", statement, stale ? "medium" : "high",
                stale ? "stale" : "current", false, false, false, name);
        }
    }

    private static void ProjectSpatialCorroboration(JsonElement root, string input,
        Action<string, string, string, string, string, bool, bool, bool, string?> add)
    {
        if (input.Length == 0 || !Object(root, "markers", out JsonElement markers) ||
            !Array(markers, "records", out JsonElement markerRecords)) return;

        List<SemanticLocationDefinition> locations = new();
        foreach (JsonElement marker in markerRecords.EnumerateArray())
        {
            string name = Text(marker, "text").Trim();
            if (name.Length == 0 || !name.Any(char.IsLetterOrDigit) ||
                !Object(marker, "approximatePosition", out JsonElement center)) continue;
            double x = Numeric(center, "x"), y = Numeric(center, "y");
            double[] size = Array(marker, "size", out JsonElement sizeValue)
                ? sizeValue.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Number).Take(2).Select(value => value.GetDouble()).ToArray()
                : System.Array.Empty<double>();
            string shape = Text(marker, "shape").ToUpperInvariant() switch { "RECTANGLE" => "rectangle", "ELLIPSE" => "ellipse", _ => "point" };
            locations.Add(new SemanticLocationDefinition(name, new WorldPosition(x, y, 0), shape,
                size.ElementAtOrDefault(0), size.ElementAtOrDefault(1), Numeric(marker, "direction"), True(marker, "stale")));
        }
        SemanticLocationDefinition? location = SemanticLocationPolicy.Resolve(locations, input);
        if (location is null || !Object(root, "enemyContacts", out JsonElement contacts) ||
            !Array(contacts, "records", out JsonElement records)) return;

        bool recent = false, earlier = false;
        foreach (JsonElement contact in records.EnumerateArray())
        {
            if (string.Equals(Text(contact, "status"), "dead", StringComparison.OrdinalIgnoreCase) ||
                !Object(contact, "estimatedPosition", out JsonElement position)) continue;
            WorldPosition estimated = new(Numeric(position, "x"), Numeric(position, "y"), Numeric(position, "z"));
            if (!SemanticLocationPolicy.Contains(location, estimated, Numeric(contact, "positionUncertaintyMeters"))) continue;
            earlier = true;
            if (string.Equals(Text(contact, "status"), "current", StringComparison.OrdinalIgnoreCase) &&
                Numeric(contact, "lastSeenSecondsAgo") <= SemanticLocationPolicy.RecentObservationWindow.TotalSeconds)
                recent = true;
        }

        if (recent)
            add("corroboration", "derived-local", $"Recent observations indicate hostile movement at the {location.Name}.",
                "high", "current", false, false, false, location.Name);
        else if (earlier)
            add("corroboration", "derived-local", $"Earlier observations indicated hostile activity at the {location.Name}.",
                "medium", "stale", false, false, false, location.Name);
        else if (ContainsAny(input, "movement", "enemy", "hostile", "contact", "activity", "tank", "infantry", "soldier"))
            add("corroboration", "derived-local", $"Nothing recent indicates hostile movement at the {location.Name}.",
                "medium", "current", true, false, false, location.Name);
    }

    private static void ProjectMemory(JsonElement root, Action<string, string, string, string, string, bool, bool, bool, string?> add)
    {
        if (!Object(root, "retrievedMemory", out JsonElement memory)) return;
        if (Object(memory, "dialogueFocus", out JsonElement focus))
        {
            string resolved = Text(focus, "resolvedQuestion");
            if (resolved.Length > 0) add("dialogue-focus", "derived-local", $"Resolved current request: {resolved}", "high", "current", false, false, false, resolved);
            string friendly = Text(focus, "friendlyGroupCallsign");
            if (friendly.Length > 0) add("dialogue-focus", "derived-local", $"Active friendly referent is {friendly}.", "high", "current", false, false, false, friendly);
        }
        if (!Array(memory, "records", out JsonElement records)) return;
        foreach (JsonElement entry in records.EnumerateArray())
        {
            string content = Text(entry, "text");
            if (PlayerUtteranceClassifier.IsQuestion(content)) continue;
            string provenance = Text(entry, "provenance");
            string confidence = provenance == "user-reported" ? "unconfirmed" : provenance == "game-observed" ? "high" : "medium";
            add("session-memory", provenance.Length == 0 ? "local-database" : provenance, content, confidence, "retained", false, false, false, content);
        }
    }

    private static void ProjectLore(JsonElement root, Action<string, string, string, string, string, bool, bool, bool, string?> add)
    {
        if (!Object(root, "lore", out JsonElement lore) || !Array(lore, "sections", out JsonElement sections)) return;
        foreach (JsonElement section in sections.EnumerateArray())
        {
            string scope = ValueOrUnavailable(Text(section, "scope"));
            string content = Text(section, "content");
            add("lore", "user-authored-lore", $"{scope} lore: {content}", "context-only", "retained", false, true, false, content);
        }
    }

    private static IReadOnlyList<Evidence> Select(IReadOnlyList<Evidence> candidates, string input)
    {
        HashSet<string> inputTerms = Terms(input);
        string resolvedIntent = candidates
            .Where(item => item.Category == "dialogue-focus" &&
                           item.Statement.StartsWith("Resolved current request:", StringComparison.Ordinal))
            .Select(item => item.OriginalText)
            .FirstOrDefault() ?? string.Empty;
        string intent = resolvedIntent.Length == 0 ? input : $"{input} {resolvedIntent}";
        bool blank = input.Length == 0;
        bool report = LooksLikeTacticalReport(input);
        bool broad = ContainsAny(intent, "situation", "sitrep", "status report", "overview", "whole picture");
        bool confirmation = ContainsAny(intent, "sensor", "confirmed", "confirmation", "recorded", "records", "state mirror", "feed", "known contact");
        bool question = PlayerUtteranceClassifier.IsQuestion(input);
        bool hasLocationFusion = candidates.Any(evidence => evidence.Category == "corroboration");

        List<Evidence> selected = new();
        foreach (Evidence evidence in candidates)
        {
            if (evidence.AlwaysInclude || evidence.CurrentPlayerReport) { selected.Add(evidence); continue; }
            if (hasLocationFusion && evidence.Category is "hostile-contact" or "hostile-focus" or "unknown-contact" or "location") continue;
            int overlap = evidence.Terms.Count(inputTerms.Contains);
            bool category = CategoryIntent(evidence.Category, intent) || broad;
            if (evidence.NegativeAbsence)
            {
                if (!report && category && (question || confirmation || broad)) selected.Add(evidence);
                continue;
            }
            if (blank) { selected.Add(evidence); continue; }
            if (report)
            {
                if (evidence.Category == "session-memory" && (overlap >= 2 || SameReport(evidence.OriginalText, input))) selected.Add(evidence);
                else if ((evidence.Category is "hostile-contact" or "hostile-focus" or "unknown-contact") && MeaningfulOverlap(evidence.Terms, inputTerms) >= 2) selected.Add(evidence);
                else if (evidence.Category == "location" && overlap > 0) selected.Add(evidence);
                else if (evidence.Category == "corroboration") selected.Add(evidence);
                continue;
            }
            if (evidence.Category is "session-memory" or "dialogue-focus")
            {
                if (overlap > 0 || category ||
                    evidence.Statement.StartsWith("Resolved current request:", StringComparison.Ordinal))
                    selected.Add(evidence);
                continue;
            }
            if (evidence.Category == "location" && overlap > 0) { selected.Add(evidence); continue; }
            if (evidence.Category == "corroboration") { selected.Add(evidence); continue; }
            if (evidence.Category == "lore") { selected.Add(evidence); continue; }
            if (category) selected.Add(evidence);
        }
        return selected.OrderByDescending(x => x.CurrentPlayerReport)
            .ThenBy(x => CategoryOrder(x.Category)).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    private static string RenderEvidenceView(string title, IReadOnlyList<Evidence> evidence, IReadOnlyList<Evidence> selected)
    {
        HashSet<string> selectedIds = selected.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        StringBuilder output = new StringBuilder(title).AppendLine().AppendLine();
        if (evidence.Count == 0) return output.Append("No evidence.").ToString();
        foreach (Evidence item in evidence)
        {
            output.Append(selectedIds.Contains(item.Id) ? "[SELECTED] " : "[EXCLUDED] ")
                .Append(item.Id).Append(" | category=").Append(item.Category)
                .Append(" | provenance=").Append(item.Provenance)
                .Append(" | observed=").Append(item.Observed)
                .Append(" | confidence=").Append(item.Confidence)
                .Append(" | freshness=").Append(item.Freshness)
                .Append(" | semantics=").Append(item.NegativeAbsence ? "absence-not-contradiction" : "positive")
                .AppendLine().Append("  ").AppendLine(item.Statement);
        }
        return output.ToString().TrimEnd();
    }

    private static string RenderFused(IReadOnlyList<Evidence> selected)
    {
        StringBuilder output = new StringBuilder("FUSED OPERATIONAL INTERPRETATION").AppendLine().AppendLine();
        Evidence? current = selected.FirstOrDefault(x => x.CurrentPlayerReport);
        if (current is not null)
        {
            output.AppendLine("CURRENT PLAYER REPORT — POSITIVE PLAYER-PROVIDED EVIDENCE")
                .Append("- ").AppendLine(current.OriginalText)
                .AppendLine("- Status: player-reported and unconfirmed unless independently corroborated.");
        }

        Evidence[] facts = selected.Where(x => !x.CurrentPlayerReport && x.Category is not "player" and not "privacy-boundary" and not "context-boundary")
            .Where(x => current is null || x.Category != "session-memory" || !SameReport(x.OriginalText, current.OriginalText)).ToArray();
        if (facts.Length > 0)
        {
            output.AppendLine().AppendLine("RELEVANT EVIDENCE");
            foreach (Evidence item in facts)
                output.Append("- [").Append(item.Provenance).Append("; confidence ").Append(item.Confidence)
                    .Append("; ").Append(item.Freshness).Append("] ").AppendLine(item.Statement);
        }
        return output.ToString().TrimEnd();
    }

    private static string RenderModelContext(IReadOnlyList<Evidence> selected, string input, bool truncated)
    {
        StringBuilder output = new StringBuilder("CURRENT OPERATIONAL CONTEXT").AppendLine().AppendLine();
        Evidence? current = selected.FirstOrDefault(item => item.CurrentPlayerReport);
        foreach (Evidence item in selected.Where(item => item.Category == "player" && item.Statement.StartsWith("Callsign:", StringComparison.Ordinal)))
            output.AppendLine(item.Statement);
        if (ContainsAny(input, "where am i", "my position", "my location", "my grid", "current position", "current location"))
            output.AppendLine("Exact current position, grid and elevation are unavailable.");

        foreach (Evidence item in selected.Where(item => item.Category is not "player" and not "privacy-boundary" and not "context-boundary")
                     .Where(item => current is null || item.Category != "session-memory" || !SameReport(item.OriginalText, current.OriginalText)))
        {
            string line = item.Category switch
            {
                "session-memory" when item.Provenance == "user-reported" => $"An earlier report says: {item.OriginalText}",
                "session-memory" when item.Provenance == "game-observed" => $"Earlier observations indicate: {item.OriginalText}",
                "session-memory" => $"Earlier information: {item.OriginalText}",
                "lore" => $"Mission background: {item.OriginalText}",
                _ => item.Statement
            };
            output.AppendLine(line);
        }
        if (truncated) output.AppendLine("Some relevant current information may be omitted.");
        return output.ToString().TrimEnd();
    }

    private static bool CategoryIntent(string category, string input) => category switch
    {
        "environment" => ContainsAny(input, "weather", "overcast", "cloud", "storm", "calm"),
        "mission-time" => ContainsAny(input, "time", "date", "daylight", "night", "dawn", "dusk", "morning", "evening"),
        "friendly-force" => ContainsAny(input, "friendly", "friendlies", "unit", "group", "team", "callsign", "wounded", "casualt", "force"),
        "objective" => ContainsAny(input, "objective", "mission", "task", "goal", "destination", "target"),
        "hostile-summary" => ContainsAny(input, "enemy", "hostile", "hostiles", "threat", "opfor"),
        "hostile-strength" => ContainsAny(input, "combatant", "strength", "how many enemies", "how many hostiles", "enemy count", "hostile count", "contact count", "approximation", "approximate", "estimate"),
        "unknown-summary" => ContainsAny(input, "unknown", "unidentified"),
        "hostile-focus" => ContainsAny(input, "where", "position", "grid", "last seen", "last known", "who reported", "who saw", "who observed", "this enemy", "this hostile", "this contact", "enemy contact"),
        "hostile-contact" => ContainsAny(input, "where", "position", "which", "describe", "details", "grid", "who reported", "who saw", "who observed", "last seen", "last known"),
        "unknown-contact" => ContainsAny(input, "unknown", "unidentified", "where", "grid", "who reported", "who saw"),
        "location" => ContainsAny(input, "location", "place", "area", "airport", "airfield", "base", "camp", "harbor", "harbour", "port"),
        "corroboration" => true,
        "session-memory" or "dialogue-focus" => ContainsAny(input, "remember", "memory", "record", "earlier", "previous", "before"),
        _ => false
    };

    private static bool LooksLikeTacticalReport(string input)
    {
        if (input.Length < 10 || PlayerUtteranceClassifier.IsQuestion(input) || PlayerUtteranceClassifier.IsPlanningIntent(input)) return false;
        return TacticalReportTerms.Any(term => ContainsWord(input, term));
    }

    private static int MeaningfulOverlap(IReadOnlySet<string> evidence, IReadOnlySet<string> input)
        => evidence.Count(term => input.Contains(term) && !StopWords.Contains(term));
    private static bool SameReport(string left, string right)
        => string.Equals(NormalizeForComparison(left), NormalizeForComparison(right), StringComparison.Ordinal);
    private static string NormalizeForComparison(string value)
        => string.Join(' ', Terms(value).OrderBy(x => x, StringComparer.Ordinal));
    private static int CategoryOrder(string category) => category switch
    { "player-report" => 0, "player" => 1, "dialogue-focus" => 2, "corroboration" => 3, "session-memory" => 4, "hostile-strength" => 5, "hostile-summary" => 6, "unknown-summary" => 7, "hostile-focus" => 8, "hostile-contact" => 9, "unknown-contact" => 10, "friendly-force" => 11, "objective" => 12, "location" => 13, "environment" => 14, "mission-time" => 15, "lore" => 16, "privacy-boundary" => 98, _ => 99 };

    private static string CurrentHostileComposition(JsonElement contacts)
    {
        if (!Array(contacts, "records", out JsonElement records)) return "unavailable";
        string[] composition = records.EnumerateArray()
            .Where(item => Text(item, "status") == "current")
            .Where(item => !Text(item, "description").StartsWith("unknown", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => Text(item, "contactType"), StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Count()} {StrengthNoun(item.Key, item.Count())}")
            .ToArray();
        return composition.Length == 0 ? "unavailable" : string.Join(", ", composition);
    }

    private static int CurrentHostileClusterCount(JsonElement contacts)
    {
        if (!Array(contacts, "groups", out JsonElement groups)) return 0;
        return groups.EnumerateArray().Count(item =>
            Text(item, "status") == "current" &&
            !Text(item, "description").StartsWith("unknown", StringComparison.OrdinalIgnoreCase));
    }

    private static string StrengthNoun(string type, int count) => type switch
    {
        "person" => count == 1 ? "infantry contact" : "infantry contacts",
        "ground-vehicle" => count == 1 ? "vehicle" : "vehicles",
        "air" => count == 1 ? "aircraft" : "aircraft",
        _ => count == 1 ? "contact" : "contacts"
    };

    private static HashSet<string> Terms(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => new string(x.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
        .Where(x => x.Length > 2 || x.All(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static bool StartsWithAny(string value, params string[] terms)
        => terms.Any(term => value.StartsWith(term, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsWord(string value, string term)
        => System.Text.RegularExpressions.Regex.IsMatch(value, $@"(?<![A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(term)}(?![A-Za-z0-9])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string NormalizeWhitespace(string value, int maximum)
    {
        string result = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length <= maximum ? result : result[..maximum];
    }
    private static bool Object(JsonElement parent, string name, out JsonElement value)
        => parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    private static bool Array(JsonElement root, string name, out JsonElement value)
        => root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;
    private static string Text(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static bool True(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    private static int IntegerValue(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    private static double Numeric(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : 0;
    private static string Number(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? Format(value.GetDouble()) : "unavailable";
    private static bool NumberOrNull(JsonElement parent, string name, out double value)
    { value = 0; return parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value); }
    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string ValueOrUnavailable(string value) => value.Length == 0 ? "unavailable" : value;
    private static string FormatDaytime(double value)
    {
        int totalMinutes = (int)Math.Round(value * 60) % 1440;
        return $"{totalMinutes / 60:00}:{totalMinutes % 60:00}";
    }

    private sealed record Evidence(string Id, string Category, string Provenance, string Statement,
        string Observed, string Confidence, string Freshness, bool NegativeAbsence, bool AlwaysInclude, bool CurrentPlayerReport,
        string OriginalText, IReadOnlySet<string> Terms);
}
