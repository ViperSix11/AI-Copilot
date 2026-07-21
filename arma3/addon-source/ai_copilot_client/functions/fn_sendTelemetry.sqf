params ["_payload"];

private _json = toJSON _payload;
private _result = "ai_copilot_bridge" callExtension ("telemetry|" + _json);

if !(_result isEqualTo "queued") then
{
    private _lastErrorLog = missionNamespace getVariable ["AIC_lastBridgeErrorLog", -100];
    if ((diag_tickTime - _lastErrorLog) >= 5) then
    {
        diag_log format ["[AI Copilot] Bridge telemetry response: %1", _result];
        missionNamespace setVariable ["AIC_lastBridgeErrorLog", diag_tickTime];
    };
};

_result
