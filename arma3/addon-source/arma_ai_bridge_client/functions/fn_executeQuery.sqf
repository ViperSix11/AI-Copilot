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
    default
    {
        _response set ["ok", false];
        _response set ["error", format ["Unsupported command: %1", _command]];
    };
};

[_response] call AAB_fnc_sendQueryResult;
