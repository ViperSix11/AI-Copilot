if (!hasInterface) exitWith {};

[] spawn
{
    waitUntil { uiSleep 0.25; !isNull player && { time >= 0 } };

    missionNamespace setVariable ["AIC_environmentCache", createHashMap];
    missionNamespace setVariable ["AIC_environmentCacheAt", -100];
    missionNamespace setVariable ["AIC_contactCache", []];
    missionNamespace setVariable ["AIC_sensorContactCache", []];
    missionNamespace setVariable ["AIC_contactCacheAt", -100];

    private _ping = "ai_copilot_bridge" callExtension "ping";
    diag_log format ["[AI Copilot] Client telemetry starting. Bridge response: %1", _ping];

    while { true } do
    {
        private _payload = call AIC_fnc_collectTelemetry;
        [_payload] call AIC_fnc_sendTelemetry;
        uiSleep 0.25;
    };
};
