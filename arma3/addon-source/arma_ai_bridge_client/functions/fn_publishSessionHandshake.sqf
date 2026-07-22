private _visibility = toLower (missionNamespace getVariable ["AAB_friendlyForceVisibility", "own-side"]);
if !(_visibility in ["own-side", "own-group"]) then { _visibility = "own-side"; };

private _features = [
    createHashMapFromArray [["name", "player-telemetry"], ["version", 1]],
    createHashMapFromArray [["name", "environment-query"], ["version", 1]],
    createHashMapFromArray [["name", "friendly-force-picture"], ["version", 1]],
    createHashMapFromArray [["name", "mission-capabilities"], ["version", 1]],
    createHashMapFromArray [["name", "map-gazetteer"], ["version", 1]],
    createHashMapFromArray [["name", "operational-observations"], ["version", 1]]
];

private _payload = createHashMapFromArray
[
    ["protocol", createHashMapFromArray [["major", 1], ["minor", 0]]],
    ["world", createHashMapFromArray [["name", worldName], ["sizeMeters", worldSize]]],
    ["viewer", createHashMapFromArray [["side", str playerSide], ["visibility", _visibility]]],
    ["features", _features]
];

missionNamespace setVariable ["AAB_lastHandshakeAt", diag_tickTime];
[
    "arma-ai-bridge/arma3/session-handshake-v1",
    _payload
] call AAB_fnc_publishWorldEvent
