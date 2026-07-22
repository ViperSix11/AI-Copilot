private _allowedKinds =
[
    "rotary_transport",
    "ground_transport",
    "medevac",
    "resupply",
    "reconnaissance",
    "vehicle_recovery",
    "other",
    "artillery",
    "cas",
    "marker_management",
    "task_management"
];
private _allowedSides = ["WEST", "EAST", "GUER", "CIV"];
private _viewerSide = str playerSide;
private _registry = missionNamespace getVariable ["AAB_missionCapabilities", []];
if !(_registry isEqualType []) exitWith { [] };

private _result = [];
{
    if ((count _result) >= 64) exitWith {};
    if (_x isEqualType createHashMap) then
    {
        private _kind = toLower (_x getOrDefault ["capability", ""]);
        private _id = _x getOrDefault ["id", _kind];
        private _enabled = _x getOrDefault ["enabled", false];
        private _provider = _x getOrDefault ["provider", "mission-script"];
        private _constraints = _x getOrDefault ["constraints", createHashMap];

        if !(_constraints isEqualType createHashMap) then { _constraints = createHashMap; };
        private _requesterSides = _constraints getOrDefault ["allowedRequesterSides", []];
        if !(_requesterSides isEqualType []) then { _requesterSides = []; };
        _requesterSides = _requesterSides
            select { _x isEqualType "" }
            apply { toUpper _x };
        _requesterSides = _requesterSides select { _x in _allowedSides };

        private _visible = ((count _requesterSides) isEqualTo 0) || { _viewerSide in _requesterSides };
        if (
            _visible &&
            { _kind in _allowedKinds } &&
            { _id isEqualType "" } &&
            { !(_id isEqualTo "") } &&
            { _enabled isEqualType true } &&
            { _provider isEqualType "" }
        ) then
        {
            private _maxConcurrent = _constraints getOrDefault ["maxConcurrent", 0];
            private _maxRange = _constraints getOrDefault ["maxRangeMeters", 0];
            private _maxPassengers = _constraints getOrDefault ["maxPassengers", 0];
            private _supportsCasualties = _constraints getOrDefault ["supportsCasualties", false];
            private _requiresConfirmation = _constraints getOrDefault ["requiresConfirmation", true];

            if !(_maxConcurrent isEqualType 0) then { _maxConcurrent = 0; };
            if !(_maxRange isEqualType 0) then { _maxRange = 0; };
            if !(_maxPassengers isEqualType 0) then { _maxPassengers = 0; };
            if !(_supportsCasualties isEqualType true) then { _supportsCasualties = false; };
            if !(_requiresConfirmation isEqualType true) then { _requiresConfirmation = true; };

            _result pushBack (createHashMapFromArray
            [
                ["id", "capability:" + _id],
                ["capability", _kind],
                ["enabled", _enabled],
                ["provider", _provider],
                ["constraints", createHashMapFromArray
                    [
                        ["maxConcurrent", round ((_maxConcurrent max 0) min 64)],
                        ["allowedRequesterSides", _requesterSides],
                        ["maxRangeMeters", (_maxRange max 0) min 100000],
                        ["maxPassengers", round ((_maxPassengers max 0) min 256)],
                        ["supportsCasualties", _supportsCasualties],
                        ["requiresConfirmation", _requiresConfirmation]
                    ]
                ]
            ]);
        };
    };
} forEach _registry;

_result
