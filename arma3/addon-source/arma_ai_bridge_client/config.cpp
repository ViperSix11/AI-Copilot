class CfgPatches
{
    class AAB_arma_ai_bridge_client
    {
        name = "ArmA AI Bridge Client";
        author = "ViperSix11";
        requiredVersion = 2.18;
        requiredAddons[] = {"A3_Functions_F"};
        units[] = {};
        weapons[] = {};
    };
};

class CfgFunctions
{
    class AAB
    {
        class Client
        {
            file = "\arma_ai_bridge_client\functions";
            class postInit { postInit = 1; };
            class collectTelemetry {};
            class collectContacts {};
            class collectSensorContacts {};
            class normalizeKnownContact {};
            class calculateAceFiringSolution {};
            class getStableEntityId {};
            class initialiseSession {};
            class publishWorldEvent {};
            class publishSessionHandshake {};
            class collectFriendlyForces {};
            class updateFriendlyForcePicture {};
            class collectMissionCapabilities {};
            class publishMissionCapabilities {};
            class publishMapGazetteer {};
            class publishStateSnapshot {};
            class sendTelemetry {};
            class pollCommands {};
            class executeQuery {};
            class queryEnvironment {};
            class sendQueryResult {};
        };
    };
};
