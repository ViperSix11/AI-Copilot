private _viewerSide = playerSide;
private _viewerSideText = str _viewerSide;
private _visibility = toLower (missionNamespace getVariable ["AAB_friendlyForceVisibility", "own-side"]);
if !(_visibility in ["own-side", "own-group"]) then { _visibility = "own-side"; };

private _friendlyUnits = if (_visibility isEqualTo "own-group") then
{
    units (group player)
}
else
{
    units _viewerSide
};
_friendlyUnits = _friendlyUnits select
{
    !isNull _x && { (side (group _x)) isEqualTo _viewerSide }
};

private _friendlyGroups = [];
private _friendlyVehicles = [];
{
    private _group = group _x;
    if (!isNull _group) then { _friendlyGroups pushBackUnique _group; };
    private _vehicle = vehicle _x;
    if !(_vehicle isEqualTo _x) then { _friendlyVehicles pushBackUnique _vehicle; };
} forEach _friendlyUnits;

private _registeredAssets = missionNamespace getVariable ["AAB_supportAssets", []];
if !(_registeredAssets isEqualType []) then { _registeredAssets = []; };
private _visibleAssetDefinitions = [];
{
    if (_x isEqualType createHashMap) then
    {
        private _allowedSides = _x getOrDefault ["allowedSides", []];
        if !(_allowedSides isEqualType []) then { _allowedSides = []; };
        _allowedSides = _allowedSides
            select { _x isEqualType "" }
            apply { toUpper _x };
        private _visible = ((count _allowedSides) isEqualTo 0) || { _viewerSideText in _allowedSides };
        private _vehicle = _x getOrDefault ["vehicle", objNull];

        if (_visible && { _vehicle isEqualType objNull } && { !isNull _vehicle }) then
        {
            private _foreignCrew = (crew _vehicle) findIf
            {
                alive _x && { !((side (group _x)) isEqualTo _viewerSide) }
            };
            if (_foreignCrew < 0) then
            {
                _friendlyVehicles pushBackUnique _vehicle;
                _visibleAssetDefinitions pushBack _x;
            };
        };
    };
} forEach _registeredAssets;

private _unitRecords = [];
{
    private _unitId = [_x, "unit"] call AAB_fnc_getStableEntityId;
    private _groupId = [group _x, "group"] call AAB_fnc_getStableEntityId;
    private _vehicle = vehicle _x;
    private _vehicleId = objNull;
    private _vehicleRole = "";
    if !(_vehicle isEqualTo _x) then
    {
        _vehicleId = [_vehicle, "vehicle"] call AAB_fnc_getStableEntityId;
        private _assignedRole = assignedVehicleRole _x;
        if ((count _assignedRole) > 0 && { (_assignedRole select 0) isEqualType "" }) then
        {
            _vehicleRole = toLower (_assignedRole select 0);
        };
    };

    private _lifeState = lifeState _x;
    private _medicalReadiness = "base-arma-healthy";
    if !(alive _x) then
    {
        _medicalReadiness = "base-arma-dead";
    }
    else
    {
        if (_lifeState in ["INCAPACITATED", "UNCONSCIOUS"]) then
        {
            _medicalReadiness = "base-arma-incapacitated";
        }
        else
        {
            if ((damage _x) > 0) then { _medicalReadiness = "base-arma-wounded"; };
        };
    };

    private _callsign = _x getVariable ["AAB_callsign", ""];
    if !(_callsign isEqualType "") then { _callsign = ""; };
    _unitRecords pushBack (createHashMapFromArray
    [
        ["id", _unitId],
        ["groupId", _groupId],
        ["callsign", _callsign],
        ["side", _viewerSideText],
        ["class", typeOf _x],
        ["role", getText (configFile >> "CfgVehicles" >> (typeOf _x) >> "displayName")],
        ["positionATL", getPosATL _x],
        ["alive", alive _x],
        ["lifeState", _lifeState],
        ["mobile", alive _x && { canMove _x }],
        ["damage", damage _x],
        ["vehicleId", _vehicleId],
        ["vehicleRole", _vehicleRole],
        ["medicalReadiness", _medicalReadiness]
    ]);
} forEach _friendlyUnits;

private _groupRecords = [];
{
    private _members = (units _x) select { _x in _friendlyUnits };
    if ((count _members) > 0) then
    {
        private _leader = leader _x;
        if !(_leader in _members) then { _leader = _members select 0; };
        private _memberIds = _members apply { [_x, "unit"] call AAB_fnc_getStableEntityId };
        _groupRecords pushBack (createHashMapFromArray
        [
            ["id", [_x, "group"] call AAB_fnc_getStableEntityId],
            ["callsign", groupId _x],
            ["side", _viewerSideText],
            ["leaderId", [_leader, "unit"] call AAB_fnc_getStableEntityId],
            ["unitIds", _memberIds],
            ["positionATL", getPosATL _leader],
            ["behaviour", behaviour _leader]
        ]);
    };
} forEach _friendlyGroups;

private _vehicleRecords = [];
{
    private _crewIds = [];
    {
        if (_x in _friendlyUnits) then
        {
            _crewIds pushBack ([_x, "unit"] call AAB_fnc_getStableEntityId);
        };
    } forEach (crew _x);

    private _allCargoSeats = count (fullCrew [_x, "cargo", true]);
    _vehicleRecords pushBack (createHashMapFromArray
    [
        ["id", [_x, "vehicle"] call AAB_fnc_getStableEntityId],
        ["side", _viewerSideText],
        ["class", typeOf _x],
        ["displayName", getText (configFile >> "CfgVehicles" >> (typeOf _x) >> "displayName")],
        ["positionATL", getPosATL _x],
        ["alive", alive _x],
        ["mobile", alive _x && { canMove _x }],
        ["damage", damage _x],
        ["fuel", fuel _x],
        ["speedKph", speed _x],
        ["crewUnitIds", _crewIds],
        ["cargoCapacity", _allCargoSeats],
        ["emptyCargoSeats", _x emptyPositions "cargo"]
    ]);
} forEach _friendlyVehicles;

private _assetRecords = [];
private _allowedKinds =
[
    "rotary_transport",
    "ground_transport",
    "medevac",
    "resupply",
    "reconnaissance",
    "vehicle_recovery",
    "other"
];
private _allowedStatuses = ["available", "busy", "degraded", "unavailable", "unknown"];
{
    if ((count _assetRecords) >= 128) exitWith {};
    private _id = _x getOrDefault ["id", ""];
    private _kind = toLower (_x getOrDefault ["kind", "other"]);
    private _callsign = _x getOrDefault ["callsign", ""];
    private _provider = _x getOrDefault ["provider", "mission-script"];
    private _vehicle = _x getOrDefault ["vehicle", objNull];
    private _status = toLower (_x getOrDefault ["status", "unknown"]);
    private _available = _x getOrDefault ["available", false];
    private _capacity = _x getOrDefault ["capacity", 0];

    if !(_kind in _allowedKinds) then { _kind = "other"; };
    if !(_status in _allowedStatuses) then { _status = "unknown"; };
    if !(_callsign isEqualType "") then { _callsign = ""; };
    if !(_provider isEqualType "") then { _provider = "mission-script"; };
    if !(_available isEqualType true) then { _available = false; };
    if !(_capacity isEqualType 0) then { _capacity = 0; };

    if (_id isEqualType "" && { _id isEqualTo "" } && { !isNull _vehicle }) then
    {
        _id = "auto:" + ([_vehicle, "vehicle"] call AAB_fnc_getStableEntityId);
    };

    if (_id isEqualType "" && { !(_id isEqualTo "") }) then
    {
        private _wireId = if ((_id select [0, 6]) isEqualTo "asset:") then { _id } else { "asset:" + _id };
        _assetRecords pushBack (createHashMapFromArray
        [
            ["id", _wireId],
            ["kind", _kind],
            ["callsign", _callsign],
            ["provider", _provider],
            ["vehicleId", if (isNull _vehicle) then { objNull } else { [_vehicle, "vehicle"] call AAB_fnc_getStableEntityId }],
            ["status", _status],
            ["available", _available],
            ["capacity", round ((_capacity max 0) min 256)]
        ]);
    };
} forEach _visibleAssetDefinitions;

createHashMapFromArray
[
    ["groups", _groupRecords],
    ["units", _unitRecords],
    ["vehicles", _vehicleRecords],
    ["assets", _assetRecords]
]
