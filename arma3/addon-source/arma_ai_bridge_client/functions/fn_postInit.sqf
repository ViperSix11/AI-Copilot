if (!hasInterface) exitWith {};

[] spawn
{
    waitUntil { uiSleep 0.25; !isNull player && { time >= 0 } };

    missionNamespace setVariable ["AAB_contactCache", []];
    missionNamespace setVariable ["AAB_sensorContactCache", []];
    missionNamespace setVariable ["AAB_contactCacheAt", -100];

    private _ping = "arma_ai_bridge" callExtension "ping";
    diag_log format ["[ArmA AI Bridge] Client bridge v0.2.0 starting. Bridge response: %1", _ping];

    [] spawn
    {
        while { true } do
        {
            private _payload = call AAB_fnc_collectTelemetry;
            [_payload] call AAB_fnc_sendTelemetry;
            uiSleep 0.25;
        };
    };

    while { true } do
    {
        call AAB_fnc_pollCommands;
        uiSleep 0.10;
    };
};
