private _unit = player;
private _vehicleObject = vehicle _unit;
if (_vehicleObject isEqualTo _unit) exitWith { [] };

private _result = [];
{
    _x params ["_target", "_targetType", "_relationship", "_sensors"];
    private _knowledge = _vehicleObject targetKnowledge _target;
    private _contact = [_vehicleObject, _target, _knowledge, playerSide] call AAB_fnc_normalizeKnownContact;
    if (_contact isEqualType createHashMap) then
    {
        _contact set ["id", [_target, "contact"] call AAB_fnc_getStableEntityId];
        _result pushBack _contact;
    };
} forEach (getSensorTargets _vehicleObject);

_result
