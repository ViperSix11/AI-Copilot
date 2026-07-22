private _now = diag_tickTime;
private _caches = missionNamespace getVariable ["AAB_stateSectionCaches", createHashMap];
private _lastSamples = missionNamespace getVariable ["AAB_stateLastSamples", createHashMap];

private _finish =
{
    params ["_name", "_value"];
    _value set ["sampledAt", time];
    _value set ["readiness", "ready"];
    _caches set [_name, _value];
    _lastSamples set [_name, diag_tickTime];
};

private _fail =
{
    params ["_name"];
    private _previous = _caches getOrDefault [_name, createHashMap];
    if (isNil { _previous get "sampledAt" }) then { _previous set ["sampledAt", time]; };
    _previous set ["readiness", "failed"];
    _caches set [_name, _previous];
    _lastSamples set [_name, diag_tickTime];
};

private _due =
{
    params ["_name", "_seconds"];
    (diag_tickTime - (_lastSamples getOrDefault [_name, -100])) >= _seconds
};

private _runSection =
{
    params ["_name", "_seconds", "_collector"];
    if ([_name, _seconds] call _due) then
    {
        private _failed = isNil { call _collector };
        if (_failed) then
        {
            [_name] call _fail;
            private _failureLogs = missionNamespace getVariable ["AAB_stateFailureLogs", createHashMap];
            private _lastLog = _failureLogs getOrDefault [_name, -100];
            if ((diag_tickTime - _lastLog) >= 30) then
            {
                diag_log format ["[ArmA AI Bridge] State section collection failed: %1", _name];
                _failureLogs set [_name, diag_tickTime];
                missionNamespace setVariable ["AAB_stateFailureLogs", _failureLogs];
            };
        };
        uiSleep 0;
    };
};

[
    "player",
    1,
    {
        private _value = createHashMapFromArray
        [
            ["sourceId", [player, "unit"] call AAB_fnc_getStableEntityId],
            ["side", str (side player)],
            ["groupSourceId", [group player, "group"] call AAB_fnc_getStableEntityId],
            ["groupCallsign", groupId (group player)],
            ["positionATL", getPosATL player],
            ["positionASL", getPosASL player],
            ["grid", mapGridPosition player]
        ];
        ["player", _value] call _finish;
        false
    }
] call _runSection;

[
    "environment",
    8,
{
    private _temperature = ambientTemperature;
    private _value = createHashMapFromArray
    [
        ["overcast", overcast],
        ["forecastOvercast", overcastForecast],
        ["rain", rain],
        ["fog", fog],
        ["fogParameters", fogParams],
        ["forecastFog", fogForecast],
        ["wind", wind],
        ["windDirection", windDir],
        ["windStrength", windStr],
        ["gusts", gusts],
        ["waves", waves],
        ["lightning", lightnings],
        ["humidity", humidity],
        ["temperatureCelsius", if ((count _temperature) > 0) then { _temperature select 0 } else { objNull }],
        ["nextWeatherChange", nextWeatherChange]
    ];
    ["environment", _value] call _finish;
    false
}
] call _runSection;

[
    "timeAstronomy",
    8,
{
    private _missionDate = date;
    private _value = createHashMapFromArray
    [
        ["missionDate", _missionDate],
        ["daytime", daytime],
        ["elapsedMissionTime", time],
        ["timeMultiplier", timeMultiplier],
        ["moonPhase", moonPhase _missionDate],
        ["sunOrMoon", sunOrMoon]
    ];
    ["timeAstronomy", _value] call _finish;
    false
}
] call _runSection;

[
    "loadout",
    4,
{
    private _magazines = [];
    private _totals = createHashMap;
    private _grenades = 0;
    private _throwables = 0;
    private _mines = 0;
    private _explosives = 0;
    {
        if ((count _magazines) >= 64) exitWith {};
        _x params ["_class", "_rounds", ["_loaded", false], ["_magazineType", -1], ["_container", ""]];
        private _displayName = getText (configFile >> "CfgMagazines" >> _class >> "displayName");
        _magazines pushBack (createHashMapFromArray
        [
            ["class", _class], ["displayName", _displayName], ["rounds", _rounds max 0],
            ["loaded", _loaded], ["container", _container]
        ]);
        private _total = _totals getOrDefault [_class, [0, 0, _displayName]];
        _total set [0, (_total select 0) + 1];
        _total set [1, (_total select 1) + (_rounds max 0)];
        _totals set [_class, _total];

        private _itemType = [_class] call BIS_fnc_itemType;
        private _subtype = toLower (_itemType param [1, ""]);
        if (_subtype find "grenade" >= 0) then { _grenades = _grenades + 1; };
        if (_subtype find "smoke" >= 0 || { _subtype find "flare" >= 0 }) then { _throwables = _throwables + 1; };
        if (_subtype find "mine" >= 0) then { _mines = _mines + 1; };
        if (_subtype find "explosive" >= 0 || { _subtype find "charge" >= 0 }) then { _explosives = _explosives + 1; };
    } forEach (magazinesAmmoFull player);

    private _magazineTotals = [];
    {
        if ((count _magazineTotals) >= 64) exitWith {};
        private _total = _totals get _x;
        _magazineTotals pushBack (createHashMapFromArray
        [
            ["class", _x], ["displayName", _total select 2],
            ["magazineCount", _total select 0], ["rounds", _total select 1]
        ]);
    } forEach (keys _totals);

    private _selected = currentWeapon player;
    private _muzzle = currentMuzzle player;
    private _fireMode = currentWeaponMode player;
    if !(_fireMode isEqualType "") then { _fireMode = ""; };
    private _attachments = [];
    { if !(_x isEqualTo "") then { _attachments pushBackUnique _x; }; } forEach
        ((primaryWeaponItems player) + (secondaryWeaponItems player) + (handgunItems player) + (binocularItems player));
    if ((count _attachments) > 32) then { _attachments resize 32; };

    private _currentMagazine = currentMagazine player;
    private _magazineConfig = configFile >> "CfgMagazines" >> _currentMagazine;
    private _ammunitionClass = getText (_magazineConfig >> "ammo");
    private _ammunitionConfig = configFile >> "CfgAmmo" >> _ammunitionClass;
    private _ammunitionDisplayName = getText (_ammunitionConfig >> "displayName");
    if (_ammunitionDisplayName isEqualTo "") then { _ammunitionDisplayName = getText (_ammunitionConfig >> "descriptionShort"); };
    private _simulation = toLower (getText (_ammunitionConfig >> "simulation"));
    private _magazineInitSpeed = getNumber (_magazineConfig >> "initSpeed");
    private _weaponMuzzleConfig = configFile >> "CfgWeapons" >> _selected >> _muzzle;
    if !(isClass _weaponMuzzleConfig) then { _weaponMuzzleConfig = configFile >> "CfgWeapons" >> _selected; };
    private _weaponInitSpeed = if (isNumber (_weaponMuzzleConfig >> "initSpeed")) then
        { getNumber (_weaponMuzzleConfig >> "initSpeed") } else { 0 };
    private _effectiveInitSpeed = if (_weaponInitSpeed > 0) then
        { _weaponInitSpeed }
        else
        { if (_weaponInitSpeed < 0) then { _magazineInitSpeed * abs _weaponInitSpeed } else { _magazineInitSpeed } };
    private _airFriction = getNumber (_ammunitionConfig >> "airFriction");
    private _typicalSpeed = getNumber (_ammunitionConfig >> "typicalSpeed");
    private _gravityCoefficient = if (isNumber (_ammunitionConfig >> "coefGravity")) then
        { getNumber (_ammunitionConfig >> "coefGravity") } else { 1 };
    private _muzzleDevice = "";
    private _weaponItems = if (_selected isEqualTo primaryWeapon player) then { primaryWeaponItems player } else
        { if (_selected isEqualTo handgunWeapon player) then { handgunItems player } else
            { if (_selected isEqualTo secondaryWeapon player) then { secondaryWeaponItems player } else { [] } } };
    if ((count _weaponItems) > 0) then { _muzzleDevice = _weaponItems param [0, ""]; };
    if !(_muzzleDevice isEqualTo "") then
    {
        private _itemInfo = configFile >> "CfgWeapons" >> _muzzleDevice >> "ItemInfo";
        if (isNumber (_itemInfo >> "MagazineCoef" >> "initSpeed")) then
            { _effectiveInitSpeed = _effectiveInitSpeed * getNumber (_itemInfo >> "MagazineCoef" >> "initSpeed"); };
        if (isNumber (_itemInfo >> "AmmoCoef" >> "airFriction")) then
            { _airFriction = _airFriction * getNumber (_itemInfo >> "AmmoCoef" >> "airFriction"); };
        if (isNumber (_itemInfo >> "AmmoCoef" >> "typicalSpeed")) then
            { _typicalSpeed = _typicalSpeed * getNumber (_itemInfo >> "AmmoCoef" >> "typicalSpeed"); };
    };
    private _zeroing = if (_selected isEqualTo "") then { [0, -1] } else { player currentZeroing [_selected, _muzzle] };
    private _zeroingDistance = _zeroing param [0, 0];
    private _zeroingIndex = _zeroing param [1, -1];
    private _advancedBallistics = isClass (configFile >> "CfgPatches" >> "ace_advanced_ballistics");
    private _submunition = getText (_ammunitionConfig >> "submunitionAmmo");
    private _customAmmoHandlers = isClass (_ammunitionConfig >> "EventHandlers");
    private _ballisticReason = "";
    if (_selected isEqualTo "" || { _currentMagazine isEqualTo "" }) then { _ballisticReason = "no_current_weapon"; };
    if (_ballisticReason isEqualTo "" && { _advancedBallistics }) then { _ballisticReason = "advanced_ballistics_mod_detected"; };
    if (_ballisticReason isEqualTo "" && { !(_simulation in ["shotbullet", "shotshell"]) || { !(_submunition isEqualTo "") } || { getNumber (_ammunitionConfig >> "artilleryLock") > 0 } || { _customAmmoHandlers } }) then
        { _ballisticReason = "unsupported_projectile"; };
    if (_ballisticReason isEqualTo "" && { _effectiveInitSpeed <= 0 || { _airFriction > 0 } || { _gravityCoefficient <= 0 } || { _typicalSpeed <= 0 } }) then
        { _ballisticReason = "missing_ballistic_config"; };
    private _ballisticProfile = createHashMapFromArray
    [
        ["available", _ballisticReason isEqualTo ""], ["reason", _ballisticReason],
        ["model", "arma-vanilla-config"],
        ["supportedProjectileType", if (_simulation isEqualTo "shotbullet") then { "bullet" } else { if (_simulation isEqualTo "shotshell") then { "shell" } else { "" } }],
        ["weaponClass", _selected], ["weaponDisplayName", getText (configFile >> "CfgWeapons" >> _selected >> "displayName")],
        ["muzzleClass", _muzzle], ["fireMode", _fireMode],
        ["magazineClass", _currentMagazine], ["magazineDisplayName", getText (_magazineConfig >> "displayName")],
        ["ammunitionClass", _ammunitionClass], ["ammunitionDisplayName", _ammunitionDisplayName],
        ["simulation", _simulation], ["loadedRounds", if (_muzzle isEqualTo "") then { 0 } else { player ammo _muzzle }],
        ["currentZeroingMeters", _zeroingDistance], ["currentZeroingIndex", _zeroingIndex],
        ["initialSpeedMetersPerSecond", _effectiveInitSpeed], ["airFriction", _airFriction],
        ["gravityCoefficient", _gravityCoefficient], ["typicalSpeedMetersPerSecond", _typicalSpeed],
        ["shooterPositionASL", eyePos player], ["advancedBallisticsDetected", _advancedBallistics]
    ];

    private _containers = [];
    {
        _x params ["_name", "_container"];
        if (!isNull _container) then
        {
            private _items = itemCargo _container;
            private _weapons = weaponCargo _container;
            if ((count _items) > 64) then { _items resize 64; };
            if ((count _weapons) > 32) then { _weapons resize 32; };
            _containers pushBack (createHashMapFromArray [["container", _name], ["items", _items], ["weaponClasses", _weapons]]);
        };
    } forEach [["uniform", uniformContainer player], ["vest", vestContainer player], ["backpack", backpackContainer player]];

    private _value = createHashMapFromArray
    [
        ["primaryWeapon", primaryWeapon player], ["launcher", secondaryWeapon player],
        ["handgun", handgunWeapon player], ["selectedWeapon", _selected],
        ["selectedWeaponDisplayName", getText (configFile >> "CfgWeapons" >> _selected >> "displayName")],
        ["muzzle", _muzzle], ["fireMode", _fireMode],
        ["currentMagazine", _currentMagazine],
        ["loadedRounds", if (_muzzle isEqualTo "") then { 0 } else { player ammo _muzzle }],
        ["opticsAndAttachments", _attachments], ["binocular", binocular player],
        ["magazines", _magazines], ["magazineTotals", _magazineTotals],
        ["grenadeCount", _grenades], ["throwableCount", _throwables],
        ["mineCount", _mines], ["explosiveCount", _explosives],
        ["assignedItems", (assignedItems player) select [0, 64]],
        ["uniformClass", uniform player], ["vestClass", vest player], ["backpackClass", backpack player],
        ["containerContents", _containers], ["ballisticProfile", _ballisticProfile]
    ];
    _value set ["loadoutHash", str (hashValue (toJSON _value))];
    ["loadout", _value] call _finish;
    false
}
] call _runSection;

[
    "friendlyForces",
    2,
{
    private _viewerSide = playerSide;
    private _visibility = toLower (missionNamespace getVariable ["AAB_friendlyForceVisibility", "own-side"]);
    private _friendlyUnits = if (_visibility isEqualTo "own-group") then { units (group player) } else { units _viewerSide };
    _friendlyUnits = _friendlyUnits select { !isNull _x && { side (group _x) isEqualTo _viewerSide } };
    if ((count _friendlyUnits) > 512) then { _friendlyUnits resize 512; };
    private _groups = [];
    { _groups pushBackUnique (group _x); } forEach _friendlyUnits;
    if ((count _groups) > 128) then { _groups resize 128; };

    private _groupRecords = [];
    {
        private _group = _x;
        private _members = (units _group) select { _x in _friendlyUnits };
        if ((count _members) > 0) then
        {
            private _leader = leader _group;
            private _waypointIndex = currentWaypoint _group;
            private _waypoint = createHashMapFromArray
            [
                ["index", _waypointIndex], ["position", waypointPosition [_group, _waypointIndex]],
                ["type", waypointType [_group, _waypointIndex]]
            ];
            private _destination = expectedDestination _leader;
            private _targets = [];
            {
                private _target = assignedTarget _x;
                if (!isNull _target) then { _targets pushBackUnique ([_target, "contact"] call AAB_fnc_getStableEntityId); };
            } forEach _members;
            _groupRecords pushBack (createHashMapFromArray
            [
                ["sourceId", [_group, "group"] call AAB_fnc_getStableEntityId], ["callsign", groupId _group],
                ["leaderSourceId", [_leader, "unit"] call AAB_fnc_getStableEntityId],
                ["memberSourceIds", _members apply { [_x, "unit"] call AAB_fnc_getStableEntityId }],
                ["leaderPosition", getPosATL _leader], ["behaviour", behaviour _leader],
                ["combatMode", combatMode _group], ["formation", formation _group], ["waypoint", _waypoint],
                ["expectedDestination", _destination param [0, [0, 0, 0]]],
                ["assignedTargetSourceIds", _targets]
            ]);
        };
    } forEach _groups;

    private _unitRecords = [];
    {
        private _vehicle = vehicle _x;
        private _vehicleId = if (_vehicle isEqualTo _x) then { "" } else { [_vehicle, "vehicle"] call AAB_fnc_getStableEntityId };
        private _vehicleRole = "";
        if !(_vehicle isEqualTo _x) then
        {
            private _role = assignedVehicleRole _x;
            if ((count _role) > 0) then { _vehicleRole = toLower (_role select 0); };
        };
        private _target = assignedTarget _x;
        _unitRecords pushBack (createHashMapFromArray
        [
            ["sourceId", [_x, "unit"] call AAB_fnc_getStableEntityId],
            ["groupSourceId", [group _x, "group"] call AAB_fnc_getStableEntityId],
            ["class", typeOf _x], ["displayRole", getText (configFile >> "CfgVehicles" >> (typeOf _x) >> "displayName")],
            ["position", getPosATL _x], ["alive", alive _x], ["lifeState", lifeState _x],
            ["mobile", alive _x && { canMove _x }], ["damage", damage _x], ["currentCommand", currentCommand _x],
            ["assignedTargetSourceId", if (isNull _target) then { "" } else { [_target, "contact"] call AAB_fnc_getStableEntityId }],
            ["vehicleSourceId", _vehicleId], ["vehicleRole", _vehicleRole]
        ]);
    } forEach _friendlyUnits;
    ["friendlyForces", createHashMapFromArray [["groups", _groupRecords], ["units", _unitRecords]]] call _finish;
    false
}
] call _runSection;

[
    "knownContacts",
    2,
{
    private _viewerSide = playerSide;
    private _visibility = toLower (missionNamespace getVariable ["AAB_friendlyForceVisibility", "own-side"]);
    private _friendlyUnits = if (_visibility isEqualTo "own-group") then { units (group player) } else { units _viewerSide };
    _friendlyUnits = _friendlyUnits select { !isNull _x && { side (group _x) isEqualTo _viewerSide } };
    if ((count _friendlyUnits) > 512) then { _friendlyUnits resize 512; };
    private _groups = [];
    { _groups pushBackUnique (group _x); } forEach _friendlyUnits;
    if ((count _groups) > 128) then { _groups resize 128; };
    private _contactMap = createHashMap;
    {
        private _group = _x;
        private _members = units _group;
        private _localIndex = _members findIf { local _x };
        if (_localIndex >= 0) then
        {
            private _observer = _members select _localIndex;
            private _groupId = [_group, "group"] call AAB_fnc_getStableEntityId;
            {
                private _target = _x;
                private _knowledge = _observer targetKnowledge _target;
                _knowledge params ["_knownByGroup", "_knownByUnit", "_lastSeen", "_lastThreat", "_perceivedSide", "_error", "_estimated", ["_ignored", false]];
                private _targetId = [_target, "contact"] call AAB_fnc_getStableEntityId;
                private _existing = _contactMap getOrDefault [_targetId, createHashMap];
                private _sources = _existing getOrDefault ["observerGroupSourceIds", []];
                _sources pushBackUnique _groupId;
                private _lastSeenAge = if (_lastSeen < 0) then { -1 } else { (time - _lastSeen) max 0 };
                private _replace = (count _existing) isEqualTo 0 || { _error < (_existing getOrDefault ["positionErrorMeters", 100000]) } ||
                    { _error isEqualTo (_existing getOrDefault ["positionErrorMeters", 100000]) && { _lastSeenAge < (_existing getOrDefault ["lastSeenAgeSeconds", 100000]) } };
                if (_replace) then
                {
                    private _broadType = if (_target isKindOf "CAManBase") then { "person" } else
                    { if (_target isKindOf "Air") then { "air" } else { if (_target isKindOf "LandVehicle") then { "ground-vehicle" } else { if (_target isKindOf "Ship") then { "naval" } else { "other" } } } };
                    _existing = createHashMapFromArray
                    [
                        ["sourceId", _targetId], ["class", typeOf _target], ["broadType", _broadType],
                        ["perceivedSide", str _perceivedSide], ["estimatedPosition", _estimated],
                        ["positionErrorMeters", _error max 0], ["lastSeenAgeSeconds", _lastSeenAge],
                        ["lastThreatAgeSeconds", if (_lastThreat < 0) then { -1 } else { (time - _lastThreat) max 0 }],
                        ["observerGroupSourceIds", _sources]
                    ];
                }
                else
                {
                    _existing set ["observerGroupSourceIds", _sources];
                };
                _contactMap set [_targetId, _existing];
            } forEach (_observer targets [false, 0, [], 120]);
        };
    } forEach _groups;

    // Preserve the accepted player-vehicle sensor path by folding sensor-known
    // targets into the same bounded contact map. Position still comes only
    // from targetKnowledge; getSensorTargets never authorizes target getPos.
    private _sensorObserver = vehicle player;
    if !(_sensorObserver isEqualTo player) then
    {
        private _groupId = [group player, "group"] call AAB_fnc_getStableEntityId;
        {
            _x params ["_target"];
            private _knowledge = _sensorObserver targetKnowledge _target;
            _knowledge params ["_knownByGroup", "_knownByUnit", "_lastSeen", "_lastThreat", "_perceivedSide", "_error", "_estimated", ["_ignored", false]];
            private _targetId = [_target, "contact"] call AAB_fnc_getStableEntityId;
            private _existing = _contactMap getOrDefault [_targetId, createHashMap];
            private _sources = _existing getOrDefault ["observerGroupSourceIds", []];
            _sources pushBackUnique _groupId;
            private _lastSeenAge = if (_lastSeen < 0) then { -1 } else { (time - _lastSeen) max 0 };
            private _replace = (count _existing) isEqualTo 0 || { _error < (_existing getOrDefault ["positionErrorMeters", 100000]) } ||
                { _error isEqualTo (_existing getOrDefault ["positionErrorMeters", 100000]) && { _lastSeenAge < (_existing getOrDefault ["lastSeenAgeSeconds", 100000]) } };
            if (_replace) then
            {
                private _broadType = if (_target isKindOf "CAManBase") then { "person" } else
                { if (_target isKindOf "Air") then { "air" } else { if (_target isKindOf "LandVehicle") then { "ground-vehicle" } else { if (_target isKindOf "Ship") then { "naval" } else { "other" } } } };
                _existing = createHashMapFromArray
                [
                    ["sourceId", _targetId], ["class", typeOf _target], ["broadType", _broadType],
                    ["perceivedSide", str _perceivedSide], ["estimatedPosition", _estimated],
                    ["positionErrorMeters", _error max 0], ["lastSeenAgeSeconds", _lastSeenAge],
                    ["lastThreatAgeSeconds", if (_lastThreat < 0) then { -1 } else { (time - _lastThreat) max 0 }],
                    ["observerGroupSourceIds", _sources]
                ];
            }
            else
            {
                _existing set ["observerGroupSourceIds", _sources];
            };
            _contactMap set [_targetId, _existing];
        } forEach (getSensorTargets _sensorObserver);
    };
    private _contacts = [];
    { if ((count _contacts) < 256) then { _contacts pushBack (_contactMap get _x); }; } forEach (keys _contactMap);
    ["knownContacts", createHashMapFromArray [["contacts", _contacts]]] call _finish;
    false
}
] call _runSection;

[
    "tasks",
    4,
{
    private _taskRecords = [];
    private _activeTask = currentTask player;
    {
        if ((count _taskRecords) >= 128) exitWith {};
        private _task = _x;
        private _description = taskDescription _task;
        private _sourceId = taskName _task;
        if (_sourceId isEqualTo "") then { _sourceId = str _task; };
        private _parent = taskParent _task;
        private _parentId = if (_parent isEqualTo taskNull) then { "" } else { taskName _parent };
        _taskRecords pushBack (createHashMapFromArray
        [
            ["sourceId", _sourceId], ["title", (_description param [1, ""]) select [0, 512]],
            ["description", (_description param [0, ""]) select [0, 1024]], ["destination", taskDestination _task],
            ["type", taskType _task], ["status", taskState _task], ["parentSourceId", _parentId],
            ["active", _task isEqualTo _activeTask]
        ]);
    } forEach (simpleTasks player);
    ["tasks", createHashMapFromArray [["tasks", _taskRecords]]] call _finish;
    false
}
] call _runSection;

[
    "markers",
    4,
{
    private _markerRecords = [];
    {
        if ((count _markerRecords) >= 256) exitWith {};
        if ((markerAlpha _x) > 0) then
        {
            private _polyline = markerPolyline _x;
            if ((count _polyline) > 128) then { _polyline resize 128; };
            _markerRecords pushBack (createHashMapFromArray
            [
                ["sourceId", _x], ["text", (markerText _x) select [0, 512]], ["position", getMarkerPos _x],
                ["type", markerType _x], ["color", markerColor _x], ["shape", markerShape _x],
                ["size", markerSize _x], ["direction", markerDir _x], ["alpha", markerAlpha _x],
                ["polyline", _polyline]
            ]);
        };
    } forEach allMapMarkers;
    ["markers", createHashMapFromArray [["markers", _markerRecords]]] call _finish;
    false
}
] call _runSection;

missionNamespace setVariable ["AAB_stateSectionCaches", _caches];
missionNamespace setVariable ["AAB_stateLastSamples", _lastSamples];

private _lastPublish = missionNamespace getVariable ["AAB_lastStatePublishAt", -100];
if ((_now - _lastPublish) < 4) exitWith { false };
private _playerSection = _caches getOrDefault ["player", createHashMap];
if ((count _playerSection) <= 2) exitWith { false };
private _required = ["player", "environment", "timeAstronomy", "loadout", "friendlyForces", "knownContacts", "tasks", "markers"];
private _sections = createHashMap;
{
    private _section = _caches getOrDefault [_x, createHashMapFromArray [["sampledAt", time], ["readiness", "unavailable"]]];
    _sections set [_x, _section];
} forEach _required;
private _lastFull = missionNamespace getVariable ["AAB_lastStateFullAt", -100];
private _full = (_now - _lastFull) >= 30;
if (_full) then { missionNamespace setVariable ["AAB_lastStateFullAt", _now]; };
missionNamespace setVariable ["AAB_lastStatePublishAt", _now];
[
    "arma-ai-bridge/arma3/state-snapshot-v2",
    createHashMapFromArray [["fullReconciliation", _full], ["sections", _sections]]
] call AAB_fnc_publishWorldEvent;
true
