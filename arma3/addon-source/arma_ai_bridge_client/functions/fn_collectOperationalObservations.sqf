private _viewerSide = playerSide;
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
    !isNull _x && { alive _x } && { !((lifeState _x) in ["INCAPACITATED", "UNCONSCIOUS"]) } &&
    { (side (group _x)) isEqualTo _viewerSide }
};

private _observers = [];
private _representedVehicles = [];
{
    private _observerVehicle = vehicle _x;
    if (_observerVehicle isEqualTo _x) then
    {
        _observers pushBack [_x, _x, "unit"];
    }
    else
    {
        if !(_observerVehicle in _representedVehicles) then
        {
            private _eligibleCrew = (crew _observerVehicle) select { _x in _friendlyUnits };
            if ((count _eligibleCrew) > 0) then
            {
                _observers pushBack [_eligibleCrew select 0, _observerVehicle, "vehicle"];
                _representedVehicles pushBack _observerVehicle;
            };
        };
    };
} forEach _friendlyUnits;

private _observations = [];
private _observationSequence = missionNamespace getVariable ["AAB_sourceObservationSequence", 0];
private _publishCache = missionNamespace getVariable ["AAB_observationPublishCache", createHashMap];
private _nowTick = diag_tickTime;

private _appendObservation =
{
    params ["_cacheKey", "_record"];
    if ((count _observations) >= 24) exitWith {};
    private _signature = toJSON _record;
    private _last = _publishCache getOrDefault [_cacheKey, ["", -100]];
    if (!((_last select 0) isEqualTo _signature) || { (_nowTick - (_last select 1)) >= 5 }) then
    {
        _observationSequence = _observationSequence + 1;
        _record set ["observationId", format ["source-observation-%1", _observationSequence]];
        _observations pushBack _record;
        _publishCache set [_cacheKey, [_signature, _nowTick]];
    };
};

if ((count _observers) > 0) then
{
    private _observerIndex = missionNamespace getVariable ["AAB_observerRoundRobinIndex", 0];
    _observerIndex = _observerIndex mod (count _observers);
    private _observer = _observers select _observerIndex;
    missionNamespace setVariable ["AAB_observerRoundRobinIndex", (_observerIndex + 1) mod (count _observers)];
    _observer params ["_knowledgeUnit", "_observerObject", "_observerKind"];
    private _sourceEntityId = [_observerObject, if (_observerKind isEqualTo "vehicle") then { "vehicle" } else { "unit" }] call AAB_fnc_getStableEntityId;

    private _sensorTargets = [];
    if (_observerKind isEqualTo "vehicle") then
    {
        {
            _x params ["_sensorTarget"];
            if (!isNull _sensorTarget) then { _sensorTargets pushBackUnique _sensorTarget; };
        } forEach (getSensorTargets _observerObject);
    };

    private _knownTargets = [];
    _knownTargets append (_knowledgeUnit targets [true, 2000, [], 120]);
    _knownTargets append ((group _knowledgeUnit) targets [true, 2000, [], 120]);
    private _uniqueTargets = [];
    { if (!isNull _x) then { _uniqueTargets pushBackUnique _x; }; } forEach _knownTargets;

    {
        if (_forEachIndex >= 32 || { (count _observations) >= 24 }) exitWith {};
        private _knowledge = _knowledgeUnit targetKnowledge _x;
        _knowledge params
        [
            "_knownByGroup", "_knownByUnit", "_lastSeen", "_lastThreat",
            "_perceivedSide", "_errorMargin", "_estimatedPosition", ["_ignored", false]
        ];
        if ((_knownByGroup || _knownByUnit) && { !_ignored }) then
        {
            private _lastSeenAge = if (_lastSeen < 0) then { 1e10 } else { (time - _lastSeen) max 0 };
            private _provenance = if (_x in _sensorTargets) then
            {
                "sensor"
            }
            else
            {
                if (_knownByUnit && { _lastSeenAge <= 2 }) then { "visual" } else { "side-knowledge" }
            };
            private _observedAt = if (_lastSeen >= 0) then { _lastSeen min time } else { time };
            private _targetId = [_x, "contact"] call AAB_fnc_getStableEntityId;
            private _record = createHashMapFromArray
            [
                ["sourceEntityId", _sourceEntityId],
                ["targetEntityId", _targetId],
                ["provenance", _provenance],
                ["entityKind", "contact"],
                ["classification", typeOf _x],
                ["displayName", getText (configFile >> "CfgVehicles" >> (typeOf _x) >> "displayName")],
                ["perceivedSide", str _perceivedSide],
                ["observedAt", _observedAt],
                ["position", _estimatedPosition],
                ["positionErrorMeters", _errorMargin max 5],
                ["state", "unknown"],
                ["alive", objNull],
                ["confidenceBasis", if (_provenance isEqualTo "sensor") then { "sensor-target-knowledge" } else { "target-knowledge" }],
                ["correlationHint", ""],
                ["retractsObservationId", ""]
            ];
            [format ["%1|%2|%3", _sourceEntityId, _targetId, _provenance], _record] call _appendObservation;
        };
    } forEach _uniqueTargets;

    private _radius = if (_observerKind isEqualTo "unit") then
    {
        350
    }
    else
    {
        if (_observerObject isKindOf "Air") then { 1200 } else { 700 }
    };
    private _candidateTypes =
    [
        "LandVehicle", "Air", "Ship", "ReammoBox_F", "WeaponHolderSimulated",
        "StaticWeapon", "Fortification", "BagFence_base_F", "HBarrier_base_F", "Land_BagBunker_base_F"
    ];
    private _candidates = nearestObjects [_observerObject, _candidateTypes, _radius, true];
    private _knownPhysical = missionNamespace getVariable ["AAB_knownPhysicalObjects", []];
    {
        if (!isNull _x && { (_x distance2D _observerObject) <= _radius }) then { _candidates pushBackUnique _x; };
    } forEach _knownPhysical;
    if ((count _candidates) > 48) then { _candidates resize 48; };

    private _eyePosition = eyePos _knowledgeUnit;
    private _eyeDirection = eyeDirection _knowledgeUnit;
    private _eyeMagnitude = sqrt (((_eyeDirection select 0) ^ 2) + ((_eyeDirection select 1) ^ 2));
    if (_eyeMagnitude < 0.001) then { _eyeMagnitude = 1; };

    {
        if ((count _observations) >= 24) exitWith {};
        private _candidate = _x;
        if (!isNull _candidate && { !(_candidate isEqualTo _observerObject) } && { (count (crew _candidate)) isEqualTo 0 }) then
        {
            private _targetPositionAsl = aimPos _candidate;
            private _offsetX = (_targetPositionAsl select 0) - (_eyePosition select 0);
            private _offsetY = (_targetPositionAsl select 1) - (_eyePosition select 1);
            private _offsetMagnitude = sqrt ((_offsetX ^ 2) + (_offsetY ^ 2));
            private _dot = if (_offsetMagnitude < 0.001) then
            {
                1
            }
            else
            {
                (((_eyeDirection select 0) * _offsetX) + ((_eyeDirection select 1) * _offsetY)) /
                    (_eyeMagnitude * _offsetMagnitude)
            };
            private _visibilityScore = [_observerObject, "VIEW", _candidate] checkVisibility [_eyePosition, _targetPositionAsl];
            if (_dot >= 0.5 && { _visibilityScore >= 0.55 }) then
            {
                private _kind = "other";
                private _uncertainty = 5;
                if (_candidate isKindOf "LandVehicle" || { _candidate isKindOf "Air" } || { _candidate isKindOf "Ship" }) then
                {
                    _kind = "vehicle";
                    _uncertainty = 3;
                }
                else
                {
                    if (_candidate isKindOf "ReammoBox_F") then { _kind = "supply"; };
                    if (_candidate isKindOf "WeaponHolderSimulated") then { _kind = "weapon"; };
                    if (_candidate isKindOf "StaticWeapon") then { _kind = "static"; };
                    if (_candidate isKindOf "Fortification" || { _candidate isKindOf "BagFence_base_F" } ||
                        { _candidate isKindOf "HBarrier_base_F" } || { _candidate isKindOf "Land_BagBunker_base_F" }) then
                    {
                        _kind = "fortification";
                    };
                };
                private _state = "intact";
                if !(alive _candidate) then { _state = "destroyed"; } else { if ((damage _candidate) > 0) then { _state = "damaged"; }; };
                private _targetId = [_candidate, _kind] call AAB_fnc_getStableEntityId;
                private _record = createHashMapFromArray
                [
                    ["sourceEntityId", _sourceEntityId],
                    ["targetEntityId", _targetId],
                    ["provenance", "visual"],
                    ["entityKind", _kind],
                    ["classification", typeOf _candidate],
                    ["displayName", getText (configFile >> "CfgVehicles" >> (typeOf _candidate) >> "displayName")],
                    ["perceivedSide", "UNKNOWN"],
                    ["observedAt", time],
                    ["position", getPosATL _candidate],
                    ["positionErrorMeters", _uncertainty],
                    ["state", _state],
                    ["alive", alive _candidate],
                    ["confidenceBasis", "los-confirmed"],
                    ["correlationHint", ""],
                    ["retractsObservationId", ""]
                ];
                [format ["%1|%2|visual", _sourceEntityId, _targetId], _record] call _appendObservation;
                _knownPhysical pushBackUnique _candidate;
            };
        };
    } forEach _candidates;
    _knownPhysical = _knownPhysical select { !isNull _x };
    if ((count _knownPhysical) > 512) then
    {
        _knownPhysical = _knownPhysical select [(count _knownPhysical) - 512, 512];
    };
    missionNamespace setVariable ["AAB_knownPhysicalObjects", _knownPhysical];
};

private _missionReports = missionNamespace getVariable ["AAB_operationalReports", []];
if !(_missionReports isEqualType []) then { _missionReports = []; };
private _seenMissionReportIds = [];
{
    if ((count _observations) >= 24) exitWith {};
    if (_x isEqualType createHashMap) then
    {
        private _allowedSides = _x getOrDefault ["allowedSides", []];
        if !(_allowedSides isEqualType []) then { _allowedSides = []; };
        _allowedSides = _allowedSides select { _x isEqualType "" } apply { toUpper _x };
        if ((count _allowedSides) > 0 && { (str _viewerSide) in _allowedSides }) then
        {
            private _reportId = _x getOrDefault ["id", ""];
            private _reportSchema = _x getOrDefault ["schema", ""];
            private _targetId = _x getOrDefault ["targetId", ""];
            private _kind = toLower (_x getOrDefault ["entityKind", "other"]);
            private _position = _x getOrDefault ["position", objNull];
            private _uncertainty = _x getOrDefault ["positionErrorMeters", 100];
            private _observedAt = _x getOrDefault ["observedAt", time];
            private _retracts = _x getOrDefault ["retractsObservationId", ""];
            private _classification = _x getOrDefault ["classification", ""];
            private _displayName = _x getOrDefault ["displayName", ""];
            private _reportedSide = _x getOrDefault ["perceivedSide", "UNKNOWN"];
            private _reportedState = _x getOrDefault ["state", "unknown"];
            private _correlationHint = _x getOrDefault ["correlationHint", ""];
            if !(_uncertainty isEqualType 0) then { _uncertainty = 100; };
            if !(_observedAt isEqualType 0) then { _observedAt = time; };
            if !(_retracts isEqualType "") then { _retracts = ""; };
            if !(_classification isEqualType "") then { _classification = ""; };
            if !(_displayName isEqualType "") then { _displayName = ""; };
            if !(_reportedSide isEqualType "") then { _reportedSide = "UNKNOWN"; };
            if !(_reportedState isEqualType "") then { _reportedState = "unknown"; };
            if !(_correlationHint isEqualType "") then { _correlationHint = ""; };
            _reportedSide = toUpper _reportedSide;
            if !(_reportedSide in ["WEST", "EAST", "GUER", "CIV", "UNKNOWN"]) then { _reportedSide = "UNKNOWN"; };
            _reportedState = toLower _reportedState;
            if !(_reportedState in ["intact", "damaged", "destroyed", "changed", "unknown"]) then { _reportedState = "unknown"; };
            if ((count _classification) > 96) then { _classification = _classification select [0, 96]; };
            if ((count _displayName) > 96) then { _displayName = _displayName select [0, 96]; };
            if ((count _correlationHint) > 128) then { _correlationHint = _correlationHint select [0, 128]; };
            if (_reportSchema isEqualType "" && { _reportSchema isEqualTo "arma-ai-bridge/mission-report-v1" } &&
                { _reportId isEqualType "" } && { !(_reportId isEqualTo "") } && { !(_reportId in _seenMissionReportIds) } &&
                { _targetId isEqualType "" } && { !(_targetId isEqualTo "") } &&
                { (count _reportId) <= 128 } && { (count _targetId) <= 128 } &&
                { _kind in ["contact", "vehicle", "supply", "weapon", "fortification", "static", "other"] } &&
                { _position isEqualType [] || { _position isEqualType objNull } }) then
            {
                _seenMissionReportIds pushBack _reportId;
                private _record = createHashMapFromArray
                [
                    ["sourceEntityId", "mission-authority"],
                    ["targetEntityId", "mission-report:" + _targetId],
                    ["provenance", "mission-report"],
                    ["entityKind", _kind],
                    ["classification", _classification],
                    ["displayName", _displayName],
                    ["perceivedSide", _reportedSide],
                    ["observedAt", (_observedAt max 0) min time],
                    ["position", _position],
                    ["positionErrorMeters", (_uncertainty max 1) min 5000],
                    ["state", _reportedState],
                    ["alive", objNull],
                    ["confidenceBasis", "mission-authorized-report"],
                    ["correlationHint", _correlationHint],
                    ["retractsObservationId", _retracts]
                ];
                ["mission-report|" + _reportId, _record] call _appendObservation;
            };
        };
    };
} forEach _missionReports;

missionNamespace setVariable ["AAB_sourceObservationSequence", _observationSequence];
if ((count _publishCache) > 2048) then
{
    private _cacheKeys = keys _publishCache;
    for "_cacheIndex" from 0 to ((count _cacheKeys) - 2049) do
    {
        _publishCache deleteAt (_cacheKeys select _cacheIndex);
    };
};
missionNamespace setVariable ["AAB_observationPublishCache", _publishCache];
_observations
