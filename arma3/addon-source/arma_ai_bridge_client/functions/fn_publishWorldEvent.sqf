params ["_schema", "_payload"];

if !(_payload isEqualType createHashMap) exitWith { "invalid-payload" };

private _sequence = (missionNamespace getVariable ["AAB_messageSequence", 0]) + 1;
missionNamespace setVariable ["AAB_messageSequence", _sequence];

_payload set ["schema", _schema];
_payload set ["messageId", format ["message-%1", _sequence]];
_payload set ["missionId", missionNamespace getVariable ["AAB_activeMissionId", ""]];
_payload set ["sessionId", missionNamespace getVariable ["AAB_sessionId", ""]];
_payload set ["timestamp", time];
_payload set ["sequence", _sequence];

private _json = toJSON _payload;
private _result = "arma_ai_bridge" callExtension ("event|" + _json);
if !(_result isEqualTo "queued") then
{
    private _lastErrorLog = missionNamespace getVariable ["AAB_lastWorldEventErrorLog", -100];
    if ((diag_tickTime - _lastErrorLog) >= 5) then
    {
        diag_log format ["[ArmA AI Bridge] Bridge world-event response: %1", _result];
        missionNamespace setVariable ["AAB_lastWorldEventErrorLog", diag_tickTime];
    };
};

_result
