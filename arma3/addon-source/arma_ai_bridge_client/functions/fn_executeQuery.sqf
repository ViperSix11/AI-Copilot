params ["_request"];

private _requestId = _request getOrDefault ["requestId", ""];
private _command = toLower (_request getOrDefault ["command", ""]);
private _parameters = _request getOrDefault ["parameters", createHashMap];

private _response = createHashMapFromArray
[
    ["schema", "arma-ai-bridge/arma3/query-result-v1"],
    ["requestId", _requestId],
    ["command", _command],
    ["timestamp", time],
    ["map", createHashMapFromArray
        [
            ["name", worldName],
            ["sizeMeters", worldSize]
        ]
    ]
];

if (_requestId isEqualTo "") exitWith
{
    _response set ["ok", false];
    _response set ["error", "Missing requestId."];
    [_response] call AAB_fnc_sendQueryResult;
};

if !(_parameters isEqualType createHashMap) exitWith
{
    _response set ["ok", false];
    _response set ["error", "Parameters must be a JSON object."];
    [_response] call AAB_fnc_sendQueryResult;
};

switch (_command) do
{
    case "query_environment":
    {
        private _result = [_parameters] call AAB_fnc_queryEnvironment;
        _response set ["ok", true];
        _response set ["result", _result];
    };
    case "query_terrain_height":
    {
        private _position = _parameters getOrDefault ["position", []];
        if !(_position isEqualType [] && { count _position isEqualTo 2 } && { (_position select 0) isEqualType 0 } && { (_position select 1) isEqualType 0 }) then
        {
            _response set ["ok", false];
            _response set ["error", "Position must contain two finite map coordinates."];
        }
        else
        {
            private _x = (_position select 0) max -10000000 min 10000000;
            private _y = (_position select 1) max -10000000 min 10000000;
            _response set ["ok", true];
            _response set ["result", createHashMapFromArray
            [
                ["position", [_x, _y]],
                ["terrainHeightAslMeters", getTerrainHeightASL [_x, _y]]
            ]];
        };
    };
    case "request_map_gazetteer":
    {
        [_requestId] spawn AAB_fnc_publishMapGazetteer;
        _response = objNull;
    };
    default
    {
        _response set ["ok", false];
        _response set ["error", format ["Unsupported command: %1", _command]];
    };
};

if !(_response isEqualType objNull) then
{
    [_response] call AAB_fnc_sendQueryResult;
};
