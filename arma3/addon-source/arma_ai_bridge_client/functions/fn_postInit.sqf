if (!hasInterface) exitWith {};

[] spawn
{
    waitUntil { uiSleep 0.25; !isNull player && { time >= 0 } };

    missionNamespace setVariable ["AAB_contactCache", []];
    missionNamespace setVariable ["AAB_sensorContactCache", []];
    missionNamespace setVariable ["AAB_contactCacheAt", -100];

    private _ping = "arma_ai_bridge" callExtension "ping";
    diag_log format ["[ArmA AI Bridge] Client bridge v0.8.0 starting. Bridge response: %1", _ping];

    call AAB_fnc_initialiseSession;

    addMissionEventHandler ["EntityCreated",
    {
        params ["_entity"];
        if (
            !isNull _entity &&
            {
                _entity isKindOf "CAManBase" ||
                { _entity isKindOf "LandVehicle" } ||
                { _entity isKindOf "Air" } ||
                { _entity isKindOf "Ship" }
            }
        ) then
        {
            missionNamespace setVariable ["AAB_forceDirty", true];
        };
    }];
    addMissionEventHandler ["EntityDeleted", { missionNamespace setVariable ["AAB_forceDirty", true]; }];
    addMissionEventHandler ["EntityKilled", { missionNamespace setVariable ["AAB_forceDirty", true]; }];
    addMissionEventHandler ["EntityRespawned", { missionNamespace setVariable ["AAB_forceDirty", true]; }];
    addMissionEventHandler ["GroupCreated", { missionNamespace setVariable ["AAB_forceDirty", true]; }];
    addMissionEventHandler ["GroupDeleted", { missionNamespace setVariable ["AAB_forceDirty", true]; }];

    [] spawn
    {
        while { true } do
        {
            call AAB_fnc_publishStateSnapshot;
            uiSleep 0.25;
        };
    };

    [] spawn
    {
        call AAB_fnc_publishMissionCapabilities;
        while { true } do
        {
            private _now = diag_tickTime;
            if ((_now - (missionNamespace getVariable ["AAB_lastHandshakeAt", -100])) >= 30) then
            {
                call AAB_fnc_publishSessionHandshake;
            };
            call AAB_fnc_publishMissionCapabilities;
            uiSleep 0.10;
        };
    };

    while { true } do
    {
        call AAB_fnc_pollCommands;
        uiSleep 0.10;
    };
};
