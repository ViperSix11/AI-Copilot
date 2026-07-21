params ["_payload"];

private _json = toJSON _payload;
private _result = "arma_ai_bridge" callExtension ("telemetry|" + _json);

if !(_result isEqualTo "queued") then
{
    private _lastErrorLog = missionNamespace getVariable ["AAB_lastBridgeErrorLog", -100];
    if ((diag_tickTime - _lastErrorLog) >= 5) then
    {
        diag_log format ["[ArmA AI Bridge] Bridge telemetry response: %1", _result];
        missionNamespace setVariable ["AAB_lastBridgeErrorLog", diag_tickTime];
    };
};

_result
