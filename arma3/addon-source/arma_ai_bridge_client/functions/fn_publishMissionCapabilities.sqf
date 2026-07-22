private _capabilities = call AAB_fnc_collectMissionCapabilities;
private _signature = toJSON _capabilities;
private _previousSignature = missionNamespace getVariable ["AAB_lastCapabilitySignature", ""];
private _lastPublished = missionNamespace getVariable ["AAB_lastCapabilitiesAt", -100];
private _changed = !(_signature isEqualTo _previousSignature);
private _reconcileDue = (diag_tickTime - _lastPublished) >= 30;

if !(_changed || _reconcileDue) exitWith { false };

private _registryVersion = missionNamespace getVariable ["AAB_capabilityRegistryVersion", 0];
if (_changed) then { _registryVersion = _registryVersion + 1; };
if (_registryVersion < 1) then { _registryVersion = 1; };

missionNamespace setVariable ["AAB_capabilityRegistryVersion", _registryVersion];
missionNamespace setVariable ["AAB_lastCapabilitySignature", _signature];
missionNamespace setVariable ["AAB_lastCapabilitiesAt", diag_tickTime];

private _payload = createHashMapFromArray
[
    ["registryVersion", _registryVersion],
    ["capabilities", _capabilities]
];
[
    "arma-ai-bridge/arma3/mission-capabilities-v1",
    _payload
] call AAB_fnc_publishWorldEvent;

true
