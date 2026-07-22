private _unit = player;
private _targets = [];
_targets append (_unit targets [true, 2000, [], 120]);
_targets append ((group _unit) targets [true, 2000, [], 120]);

private _uniqueTargets = [];
{ _uniqueTargets pushBackUnique _x; } forEach _targets;

private _result = [];
{
    if ((count _result) >= 32) exitWith {};

    private _knowledge = _unit targetKnowledge _x;
    private _contact = [_unit, _x, _knowledge, playerSide] call AAB_fnc_normalizeKnownContact;
    if (_contact isEqualType createHashMap) then
    {
        _contact set ["id", [_x, "contact"] call AAB_fnc_getStableEntityId];
        _result pushBack _contact;
    };
} forEach _uniqueTargets;

_result
