private _force = call AAB_fnc_collectFriendlyForces;
private _groups = _force getOrDefault ["groups", []];
private _units = _force getOrDefault ["units", []];
private _vehicles = _force getOrDefault ["vehicles", []];
private _assets = _force getOrDefault ["assets", []];

private _toSignatureMap =
{
    params ["_items"];
    private _map = createHashMap;
    { _map set [_x get "id", toJSON _x]; } forEach _items;
    _map
};

private _groupSignatures = [_groups] call _toSignatureMap;
private _unitSignatures = [_units] call _toSignatureMap;
private _vehicleSignatures = [_vehicles] call _toSignatureMap;
private _assetSignatures = [_assets] call _toSignatureMap;

private _lastFull = missionNamespace getVariable ["AAB_lastFullReconciliationAt", -100];
private _fullDue = (diag_tickTime - _lastFull) >= 15;
missionNamespace setVariable ["AAB_lastForceEvaluationAt", diag_tickTime];
missionNamespace setVariable ["AAB_forceDirty", false];

if (_fullDue) exitWith
{
    private _reconciliationSequence = (missionNamespace getVariable ["AAB_reconciliationSequence", 0]) + 1;
    private _reconciliationId = format ["reconcile-%1", _reconciliationSequence];
    private _pageSize = 32;
    private _pageCount = (ceil ((count _units) / _pageSize)) max 1;

    for "_pageIndex" from 0 to (_pageCount - 1) do
    {
        private _pageUnits = _units select [_pageIndex * _pageSize, _pageSize];
        private _payload = createHashMapFromArray
        [
            ["reconciliationId", _reconciliationId],
            ["pageIndex", _pageIndex],
            ["pageCount", _pageCount],
            ["groups", if (_pageIndex isEqualTo 0) then { _groups } else { [] }],
            ["units", _pageUnits],
            ["vehicles", if (_pageIndex isEqualTo 0) then { _vehicles } else { [] }],
            ["assets", if (_pageIndex isEqualTo 0) then { _assets } else { [] }]
        ];
        [
            "arma-ai-bridge/arma3/friendly-force-snapshot-v1",
            _payload
        ] call AAB_fnc_publishWorldEvent;
    };

    missionNamespace setVariable ["AAB_reconciliationSequence", _reconciliationSequence];
    missionNamespace setVariable ["AAB_lastReconciliationId", _reconciliationId];
    missionNamespace setVariable ["AAB_lastFullReconciliationAt", diag_tickTime];
    missionNamespace setVariable ["AAB_previousGroupSignatures", _groupSignatures];
    missionNamespace setVariable ["AAB_previousUnitSignatures", _unitSignatures];
    missionNamespace setVariable ["AAB_previousVehicleSignatures", _vehicleSignatures];
    missionNamespace setVariable ["AAB_previousAssetSignatures", _assetSignatures];
    true
};

private _collectChanges =
{
    params ["_items", "_current", "_previous"];
    private _upserts = [];
    {
        private _id = _x get "id";
        private _old = _previous getOrDefault [_id, ""];
        private _new = _current getOrDefault [_id, ""];
        if !(_old isEqualTo _new) then { _upserts pushBack _x; };
    } forEach _items;

    private _removed = [];
    {
        if (isNil { _current get _x }) then { _removed pushBack _x; };
    } forEach (keys _previous);
    [_upserts, _removed]
};

private _previousGroups = missionNamespace getVariable ["AAB_previousGroupSignatures", createHashMap];
private _previousUnits = missionNamespace getVariable ["AAB_previousUnitSignatures", createHashMap];
private _previousVehicles = missionNamespace getVariable ["AAB_previousVehicleSignatures", createHashMap];
private _previousAssets = missionNamespace getVariable ["AAB_previousAssetSignatures", createHashMap];

private _groupChanges = [_groups, _groupSignatures, _previousGroups] call _collectChanges;
private _unitChanges = [_units, _unitSignatures, _previousUnits] call _collectChanges;
private _vehicleChanges = [_vehicles, _vehicleSignatures, _previousVehicles] call _collectChanges;
private _assetChanges = [_assets, _assetSignatures, _previousAssets] call _collectChanges;

private _changeCount =
    count (_groupChanges select 0) + count (_groupChanges select 1) +
    count (_unitChanges select 0) + count (_unitChanges select 1) +
    count (_vehicleChanges select 0) + count (_vehicleChanges select 1) +
    count (_assetChanges select 0) + count (_assetChanges select 1);

if (_changeCount > 0) then
{
    private _payload = createHashMapFromArray
    [
        ["baseReconciliationId", missionNamespace getVariable ["AAB_lastReconciliationId", ""]],
        ["upsertGroups", _groupChanges select 0],
        ["upsertUnits", _unitChanges select 0],
        ["upsertVehicles", _vehicleChanges select 0],
        ["upsertAssets", _assetChanges select 0],
        ["removedGroupIds", _groupChanges select 1],
        ["removedUnitIds", _unitChanges select 1],
        ["removedVehicleIds", _vehicleChanges select 1],
        ["removedAssetIds", _assetChanges select 1]
    ];
    [
        "arma-ai-bridge/arma3/friendly-force-delta-v1",
        _payload
    ] call AAB_fnc_publishWorldEvent;
};

missionNamespace setVariable ["AAB_previousGroupSignatures", _groupSignatures];
missionNamespace setVariable ["AAB_previousUnitSignatures", _unitSignatures];
missionNamespace setVariable ["AAB_previousVehicleSignatures", _vehicleSignatures];
missionNamespace setVariable ["AAB_previousAssetSignatures", _assetSignatures];

_changeCount > 0
