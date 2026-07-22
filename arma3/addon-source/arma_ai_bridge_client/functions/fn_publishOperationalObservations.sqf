private _observations = call AAB_fnc_collectOperationalObservations;
if ((count _observations) isEqualTo 0) exitWith { false };

private _batchSequence = (missionNamespace getVariable ["AAB_observationBatchSequence", 0]) + 1;
missionNamespace setVariable ["AAB_observationBatchSequence", _batchSequence];
private _payload = createHashMapFromArray
[
    ["batchId", format ["observation-batch-%1", _batchSequence]],
    ["observations", _observations]
];
["arma-ai-bridge/arma3/operational-observation-batch-v1", _payload] call AAB_fnc_publishWorldEvent;
true
