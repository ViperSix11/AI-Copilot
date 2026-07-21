params ["_payload"];

private _json = toJSON _payload;
private _result = "arma_ai_bridge" callExtension ("query-result|" + _json);

if !(_result isEqualTo "queued") then
{
    diag_log format ["[ArmA AI Bridge] Bridge query result response: %1", _result];
};

_result
