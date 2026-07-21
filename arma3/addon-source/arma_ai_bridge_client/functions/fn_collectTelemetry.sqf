private _unit = player;
private _now = diag_tickTime;

if ((_now - (missionNamespace getVariable ["AAB_contactCacheAt", -100])) >= 1) then
{
    missionNamespace setVariable ["AAB_contactCache", call AAB_fnc_collectContacts];
    missionNamespace setVariable ["AAB_sensorContactCache", call AAB_fnc_collectSensorContacts];
    missionNamespace setVariable ["AAB_contactCacheAt", _now];
};

private _view = eyeDirection _unit;
private _viewHeading = ((_view select 0) atan2 (_view select 1));
if (_viewHeading < 0) then { _viewHeading = _viewHeading + 360; };

private _weapon = currentWeapon _unit;
private _magazine = currentMagazine _unit;
private _muzzle = currentMuzzle _unit;
private _loadedRounds = if (_muzzle isEqualTo "") then { 0 } else { _unit ammo _muzzle };
private _matchingMagazineRounds = 0;
private _matchingMagazineCount = 0;

{
    _x params ["_class", "_rounds"];
    if (_class isEqualTo _magazine) then
    {
        _matchingMagazineCount = _matchingMagazineCount + 1;
        _matchingMagazineRounds = _matchingMagazineRounds + _rounds;
    };
} forEach (magazinesAmmoFull _unit);

private _playerData = createHashMapFromArray
[
    ["uid", getPlayerUID _unit],
    ["name", name _unit],
    ["side", str (side _unit)],
    ["group", groupId (group _unit)],
    ["positionATL", getPosATL _unit],
    ["positionASL", getPosASL _unit],
    ["bodyHeading", getDir _unit],
    ["viewHeading", _viewHeading],
    ["eyeDirection", _view],
    ["speedKph", speed _unit],
    ["damage", damage _unit],
    ["lifeState", lifeState _unit],
    ["stance", stance _unit],
    ["weapon", _weapon],
    ["magazine", _magazine],
    ["muzzle", _muzzle],
    ["loadedRounds", _loadedRounds],
    ["matchingMagazineCount", _matchingMagazineCount],
    ["matchingMagazineRounds", _matchingMagazineRounds]
];

private _vehicleObject = vehicle _unit;
private _vehicleData = objNull;
if !(_vehicleObject isEqualTo _unit) then
{
    _vehicleData = createHashMapFromArray
    [
        ["class", typeOf _vehicleObject],
        ["displayName", getText (configFile >> "CfgVehicles" >> (typeOf _vehicleObject) >> "displayName")],
        ["positionATL", getPosATL _vehicleObject],
        ["heading", getDir _vehicleObject],
        ["speedKph", speed _vehicleObject],
        ["fuel", fuel _vehicleObject],
        ["damage", damage _vehicleObject],
        ["role", assignedVehicleRole _unit]
    ];
};

private _mapData = createHashMapFromArray
[
    ["name", worldName],
    ["sizeMeters", worldSize],
    ["grid", mapGridPosition _unit],
    ["daytime", daytime]
];

createHashMapFromArray
[
    ["schema", "arma-ai-bridge/arma3/telemetry-v1"],
    ["timestamp", time],
    ["frame", diag_frameNo],
    ["map", _mapData],
    ["player", _playerData],
    ["vehicle", _vehicleData],
    ["contacts", missionNamespace getVariable ["AAB_contactCache", []]],
    ["sensorContacts", missionNamespace getVariable ["AAB_sensorContactCache", []]]
]
