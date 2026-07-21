private _unit = player;
private _vehicleObject = vehicle _unit;
if (_vehicleObject isEqualTo _unit) exitWith { [] };

private _result = [];
{
    _x params ["_target", "_targetType", "_relationship", "_sensors"];
    private _id = netId _target;
    if (_id isEqualTo "") then { _id = str _target; };

    _result pushBack (createHashMapFromArray
    [
        ["id", _id],
        ["class", typeOf _target],
        ["targetType", _targetType],
        ["relationship", _relationship],
        ["sensors", _sensors]
    ]);
} forEach (getSensorTargets _vehicleObject);

_result
