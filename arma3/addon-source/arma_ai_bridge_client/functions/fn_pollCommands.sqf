private _json = "arma_ai_bridge" callExtension "poll";
if (_json isEqualTo "") exitWith { false };

private _command = createHashMap;
private _parseFailed = isNil
{
    _command = fromJSON _json;
    false
};

if (_parseFailed || { !(_command isEqualType createHashMap) }) exitWith
{
    diag_log "[ArmA AI Bridge] Discarded invalid command JSON.";
    false
};

private _schema = _command getOrDefault ["schema", ""];
switch (_schema) do
{
    case "arma-ai-bridge/command-v1": { [_command] call AAB_fnc_executeQuery; };
    case "arma-ai-bridge/map-index-command-v1": { [_command] call AAB_fnc_executeMapIndexCommand; };
    default { diag_log "[ArmA AI Bridge] Discarded command with unsupported schema."; };
};
true
