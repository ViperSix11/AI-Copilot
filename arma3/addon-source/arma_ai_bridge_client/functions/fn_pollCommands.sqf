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

[_command] call AAB_fnc_executeQuery;
true
