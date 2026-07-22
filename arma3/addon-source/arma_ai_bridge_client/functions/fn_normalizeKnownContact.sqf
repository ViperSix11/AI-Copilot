/*
 * Closed release-0.8 contact eligibility policy. This function must run before
 * AAB_fnc_getStableEntityId. It returns [] for every ineligible object.
 */
params ["_observer", "_target", "_knowledge", "_viewerSide"];

if (isNull _observer || { isNull _target } || { isSimpleObject _target }) exitWith { [] };
if !(_knowledge isEqualType [] && { count _knowledge >= 7 }) exitWith { [] };

_knowledge params
[
    "_knownByGroup", "_knownByUnit", "_lastSeen", "_lastThreat",
    "_perceivedSide", "_error", "_estimated", ["_ignored", false]
];

if !(_knownByGroup || _knownByUnit) exitWith { [] };
if (_ignored) exitWith { [] };
if !(_estimated isEqualType [] && { count _estimated isEqualTo 3 }) exitWith { [] };
if ({ !(_x isEqualType 0) || { !finite _x } || { abs _x > 10000000 } } count _estimated > 0) exitWith { [] };
if !(_error isEqualType 0 && { finite _error } && { _error >= 0 } && { _error <= 100000 }) exitWith { [] };

private _lastSeenAge = if (_lastSeen < 0) then { -1 } else { (time - _lastSeen) max 0 };
private _lastThreatAge = if (_lastThreat < 0) then { -1 } else { (time - _lastThreat) max 0 };
if (_lastSeenAge < 0 && { _lastThreatAge < 0 }) exitWith { [] };
if (_lastSeenAge > 120 || { _lastThreatAge > 120 }) exitWith { [] };

private _sideText = str _perceivedSide;
if !(_sideText in ["WEST", "EAST", "GUER", "CIV", "ENEMY"]) exitWith { [] };

private _relationship = "neutral";
if (_sideText isEqualTo "CIV") then
{
    _relationship = "civilian";
}
else
{
    if (_sideText isEqualTo "ENEMY" || { [_viewerSide, _perceivedSide] call BIS_fnc_sideIsEnemy }) then
    {
        _relationship = "hostile";
    }
    else
    {
        if (_perceivedSide isEqualTo _viewerSide || { [_viewerSide, _perceivedSide] call BIS_fnc_sideIsFriendly }) then
        {
            _relationship = "friendly";
        };
    };
};

// Own-side actors are represented authoritatively by friendlyForces, not
// duplicated as contacts.
if (_relationship isEqualTo "friendly") exitWith { [] };

private _contactType = "";
private _isPerson = _target isKindOf "CAManBase";
private _isStaticWeapon = _target isKindOf "StaticWeapon";
private _isAir = _target isKindOf "Air";
private _isLand = _target isKindOf "LandVehicle";
private _isNaval = _target isKindOf "Ship";
private _classConfig = configFile >> "CfgVehicles" >> typeOf _target;
private _isUnmanned = getNumber (_classConfig >> "isUav") > 0;
private _livingCrew = { alive _x } count (crew _target);

if (_isPerson) then
{
    if (!alive _target || { lifeState _target isEqualTo "DEAD" }) exitWith {};
    _contactType = "person";
}
else
{
    if !(_isStaticWeapon || _isAir || _isLand || _isNaval) exitWith {};
    if (!alive _target) exitWith {};
    if (_isUnmanned) then
    {
        private _controls = UAVControl _target;
        private _hasOperator = _controls findIf { _x isEqualType objNull && { !isNull _x } } >= 0;
        if (!isAutonomous _target && { _livingCrew isEqualTo 0 } && { !_hasOperator }) exitWith {};
        _contactType = if (_isAir) then { "unmanned-air" } else { "unmanned-ground" };
    }
    else
    {
        if (_livingCrew isEqualTo 0) exitWith {};
        _contactType = if (_isStaticWeapon) then { "static-weapon" } else
        { if (_isAir) then { "air" } else { if (_isLand) then { "ground-vehicle" } else { if (_isNaval) then { "naval" } else { "" } } } };
    };
};

if (_contactType isEqualTo "") exitWith { [] };

createHashMapFromArray
[
    ["class", typeOf _target],
    ["displayName", getText (_classConfig >> "displayName")],
    ["contactType", _contactType],
    ["perceivedSide", _sideText],
    ["relationship", _relationship],
    ["estimatedPosition", _estimated],
    ["positionErrorMeters", _error],
    ["lastSeenAgeSeconds", _lastSeenAge],
    ["lastThreatAgeSeconds", _lastThreatAge]
]
