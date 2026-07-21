private _unit = player;
private _targets = [];
_targets append (_unit targets [true, 2000, [], 120]);
_targets append ((group _unit) targets [true, 2000, [], 120]);

private _uniqueTargets = [];
{ _uniqueTargets pushBackUnique _x; } forEach _targets;

private _result = [];
{
    if ((count _result) >= 32) exitWith {};

    private _knowledge = _unit targetKnowledge _x;
    _knowledge params
    [
        "_knownByGroup",
        "_knownByUnit",
        "_lastSeen",
        "_lastThreat",
        "_perceivedSide",
        "_errorMargin",
        "_estimatedPosition",
        ["_ignored", false]
    ];

    private _lastSeenAge = if (_lastSeen < 0) then { -1 } else { (time - _lastSeen) max 0 };
    private _lastThreatAge = if (_lastThreat < 0) then { -1 } else { (time - _lastThreat) max 0 };
    private _id = netId _x;
    if (_id isEqualTo "") then { _id = str _x; };

    _result pushBack (createHashMapFromArray
    [
        ["id", _id],
        ["class", typeOf _x],
        ["displayName", getText (configFile >> "CfgVehicles" >> (typeOf _x) >> "displayName")],
        ["knownByPlayer", _knownByUnit],
        ["knownByGroup", _knownByGroup],
        ["lastSeenAgeSeconds", _lastSeenAge],
        ["lastThreatAgeSeconds", _lastThreatAge],
        ["perceivedSide", str _perceivedSide],
        ["positionErrorMeters", _errorMargin],
        ["estimatedPosition", _estimatedPosition],
        ["ignored", _ignored]
    ]);
} forEach _uniqueTargets;

_result
