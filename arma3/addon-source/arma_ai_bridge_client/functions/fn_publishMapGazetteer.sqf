params ["_requestId"];

private _sendFailure =
{
    params ["_requestId", "_errorCode"];
    private _batchSequence = (missionNamespace getVariable ["AAB_gazetteerBatchSequence", 0]) + 1;
    missionNamespace setVariable ["AAB_gazetteerBatchSequence", _batchSequence];
    private _payload = createHashMapFromArray
    [
        ["requestId", _requestId],
        ["batchId", format ["gazetteer-%1", _batchSequence]],
        ["pageIndex", 0],
        ["pageCount", 1],
        ["world", createHashMapFromArray [["name", worldName], ["sizeMeters", worldSize]]],
        ["totalLocations", 0],
        ["status", "failed"],
        ["errorCode", _errorCode],
        ["locations", []]
    ];
    ["arma-ai-bridge/arma3/map-gazetteer-v1", _payload] call AAB_fnc_publishWorldEvent;
};

private _sendPages =
{
    params ["_requestId", "_locations"];
    private _batchSequence = (missionNamespace getVariable ["AAB_gazetteerBatchSequence", 0]) + 1;
    missionNamespace setVariable ["AAB_gazetteerBatchSequence", _batchSequence];
    private _batchId = format ["gazetteer-%1", _batchSequence];
    private _total = count _locations;
    private _pageCount = 1 max (ceil (_total / 128));
    for "_pageIndex" from 0 to (_pageCount - 1) do
    {
        private _start = _pageIndex * 128;
        private _page = _locations select [_start, 128];
        private _payload = createHashMapFromArray
        [
            ["requestId", _requestId],
            ["batchId", _batchId],
            ["pageIndex", _pageIndex],
            ["pageCount", _pageCount],
            ["world", createHashMapFromArray [["name", worldName], ["sizeMeters", worldSize]]],
            ["totalLocations", _total],
            ["status", "complete"],
            ["errorCode", objNull],
            ["locations", _page]
        ];
        ["arma-ai-bridge/arma3/map-gazetteer-v1", _payload] call AAB_fnc_publishWorldEvent;
        uiSleep 0;
    };
};

if !(_requestId isEqualType "" && { count _requestId > 0 } && { count _requestId <= 128 }) exitWith {};

private _status = missionNamespace getVariable ["AAB_gazetteerCacheStatus", "unavailable"];
if (_status isEqualTo "ready") exitWith
{
    [_requestId, missionNamespace getVariable ["AAB_gazetteerCache", []]] call _sendPages;
};
if (_status isEqualTo "failed") exitWith
{
    [_requestId, missionNamespace getVariable ["AAB_gazetteerCacheError", "gazetteer_collection_failed"]] call _sendFailure;
};
if (_status isEqualTo "collecting") exitWith
{
    [_requestId, "gazetteer_collection_in_progress"] call _sendFailure;
};

missionNamespace setVariable ["AAB_gazetteerCacheStatus", "collecting"];
missionNamespace setVariable
[
    "AAB_gazetteerCollectionCount",
    (missionNamespace getVariable ["AAB_gazetteerCollectionCount", 0]) + 1
];

private _root = configFile >> "CfgWorlds" >> worldName >> "Names";
private _entries = "true" configClasses _root;
if ((count _entries) > 8192) exitWith
{
    missionNamespace setVariable ["AAB_gazetteerCacheStatus", "failed"];
    missionNamespace setVariable ["AAB_gazetteerCacheError", "gazetteer_limit_exceeded"];
    [_requestId, "gazetteer_limit_exceeded"] call _sendFailure;
};

private _locations = [];
private _invalid = false;
private _allowedTypes =
[
    "Name", "NameCityCapital", "NameCity", "NameVillage", "CityCenter", "NameLocal",
    "Airport", "Strategic", "StrongpointArea", "Mount", "Hill", "ViewPoint", "NameMarine",
    "BorderCrossing", "HistoricalSite", "CulturalProperty", "CivilDefense"
];
{
    private _key = configName _x;
    private _name = getText (_x >> "name");
    private _type = getText (_x >> "type");
    private _position = getArray (_x >> "position");
    private _radiusA = getNumber (_x >> "radiusA");
    private _radiusB = getNumber (_x >> "radiusB");
    private _angle = getNumber (_x >> "angle");
    if !(_type in _allowedTypes) then { continue; };
    if
    (
        !(_key isEqualType "") || { count _key < 1 } || { count _key > 128 } ||
        { !(_name isEqualType "") } || { count _name < 1 } || { count _name > 160 } ||
        { !(_type isEqualType "") } || { count _type < 1 } || { count _type > 64 } ||
        { !(_position isEqualType []) } || { count _position != 2 } ||
        { !finite (_position select 0) } || { !finite (_position select 1) } ||
        { (_position select 0) < -50000 } || { (_position select 0) > 500000 } ||
        { (_position select 1) < -50000 } || { (_position select 1) > 500000 } ||
        { !finite _radiusA } || { _radiusA < 0 } || { _radiusA > 100000 } ||
        { !finite _radiusB } || { _radiusB < 0 } || { _radiusB > 100000 } ||
        { !finite _angle }
    ) exitWith { _invalid = true; };

    _angle = ((_angle % 360) + 360) % 360;
    _locations pushBack (createHashMapFromArray
    [
        ["key", _key],
        ["name", _name],
        ["type", _type],
        ["position", _position],
        ["radiusA", _radiusA],
        ["radiusB", _radiusB],
        ["angle", _angle]
    ]);
    if (((_forEachIndex + 1) % 128) isEqualTo 0) then { uiSleep 0; };
} forEach _entries;

if (_invalid) exitWith
{
    missionNamespace setVariable ["AAB_gazetteerCacheStatus", "failed"];
    missionNamespace setVariable ["AAB_gazetteerCacheError", "gazetteer_config_invalid"];
    [_requestId, "gazetteer_config_invalid"] call _sendFailure;
};

missionNamespace setVariable ["AAB_gazetteerCache", _locations];
missionNamespace setVariable ["AAB_gazetteerCacheStatus", "ready"];
missionNamespace setVariable ["AAB_gazetteerCacheError", ""];
[_requestId, _locations] call _sendPages;
