private _missionId = missionNamespace getVariable ["AAB_missionId", ""];
if !(_missionId isEqualType "") then { _missionId = ""; };
if (_missionId isEqualTo "") then
{
    private _source = missionNameSource;
    if (_source isEqualTo "") then { _source = missionName; };
    _missionId = format ["%1/%2", toLower worldName, toLower _source];
};

private _sessionId = format
[
    "session-%1-%2-%3",
    diag_frameNo,
    floor (diag_tickTime * 1000),
    floor (random 1000000000)
];

missionNamespace setVariable ["AAB_activeMissionId", _missionId];
missionNamespace setVariable ["AAB_sessionId", _sessionId];
missionNamespace setVariable ["AAB_messageSequence", 0];
missionNamespace setVariable ["AAB_reconciliationSequence", 0];
missionNamespace setVariable ["AAB_capabilityRegistryVersion", 0];
missionNamespace setVariable ["AAB_forceDirty", true];
missionNamespace setVariable ["AAB_lastForceEvaluationAt", -100];
missionNamespace setVariable ["AAB_lastFullReconciliationAt", -100];
missionNamespace setVariable ["AAB_lastHandshakeAt", -100];
missionNamespace setVariable ["AAB_lastCapabilitiesAt", -100];
missionNamespace setVariable ["AAB_lastCapabilitySignature", ""];
missionNamespace setVariable ["AAB_lastReconciliationId", ""];
missionNamespace setVariable ["AAB_gazetteerSequence", 0];
missionNamespace setVariable ["AAB_observationBatchSequence", 0];
missionNamespace setVariable ["AAB_sourceObservationSequence", 0];
missionNamespace setVariable ["AAB_observerRoundRobinIndex", 0];
missionNamespace setVariable ["AAB_lastGazetteerAt", -100];
missionNamespace setVariable ["AAB_observationPublishCache", createHashMap];
missionNamespace setVariable ["AAB_knownPhysicalObjects", []];

call AAB_fnc_publishSessionHandshake
