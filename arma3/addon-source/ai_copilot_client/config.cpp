class CfgPatches
{
    class AIC_ai_copilot_client
    {
        name = "AI Copilot Client";
        author = "ViperSix11";
        requiredVersion = 2.18;
        requiredAddons[] = {"A3_Functions_F"};
        units[] = {};
        weapons[] = {};
    };
};

class CfgFunctions
{
    class AIC
    {
        class Client
        {
            file = "\ai_copilot_client\functions";
            class postInit { postInit = 1; };
            class collectTelemetry {};
            class collectEnvironment {};
            class collectContacts {};
            class collectSensorContacts {};
            class sendTelemetry {};
        };
    };
};
