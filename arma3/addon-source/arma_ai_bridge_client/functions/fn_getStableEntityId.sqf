params ["_entity", ["_kind", "entity"]];

if (isNull _entity) exitWith { "" };

private _networkId = netId _entity;
if !(_networkId isEqualTo "") exitWith { "net:" + _networkId };

private _registryName = format ["AAB_identityRegistry_%1", toLower _kind];
private _registry = missionNamespace getVariable [_registryName, []];
private _index = _registry findIf { ((_x select 0) isEqualTo _entity) };
if (_index >= 0) exitWith { (_registry select _index) select 1 };

private _id = format ["fallback:%1:%2", toLower _kind, (count _registry) + 1];
_registry pushBack [_entity, _id];
missionNamespace setVariable [_registryName, _registry];
_id
