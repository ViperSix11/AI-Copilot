private _worldConfig = configFile >> "CfgWorlds" >> worldName;
private _namesConfig = _worldConfig >> "Names";
private _locationPairs = [];

for "_index" from 0 to ((count _namesConfig) - 1) do
{
    private _entry = _namesConfig select _index;
    if (isClass _entry) then
    {
        private _name = getText (_entry >> "name");
        private _type = getText (_entry >> "type");
        private _position = getArray (_entry >> "position");
        if (_name isEqualType "" && { !(_name isEqualTo "") } &&
            { _type isEqualType "" } && { !(_type isEqualTo "") } &&
            { (count _position) >= 2 }) then
        {
            if ((_name select [0, 1]) isEqualTo "$") then { _name = localize (_name select [1]); };
            if ((count _name) > 160) then { _name = _name select [0, 160]; };
            if ((count _type) > 64) then { _type = _type select [0, 64]; };
            private _configKey = configName _entry;
            if ((count _configKey) > 128) then { _configKey = _configKey select [0, 128]; };
            private _radiusA = getNumber (_entry >> "radiusA");
            private _radiusB = getNumber (_entry >> "radiusB");
            private _record = createHashMapFromArray
            [
                ["configKey", _configKey],
                ["officialName", _name],
                ["locationType", _type],
                ["position", [_position select 0, _position select 1]],
                ["size", if (_radiusA > 0 || { _radiusB > 0 }) then { [_radiusA max 0, _radiusB max 0] } else { objNull }]
            ];
            _locationPairs pushBack [format ["%1|%2|%3|%4", toLower _name, _type, _position select 0, _position select 1], _record];
        };
    };
};

_locationPairs sort true;
private _locations = _locationPairs apply { _x select 1 };
if ((count _locations) > 4096) then { _locations resize 4096; };

private _edge = (worldSize - 1) max 0;
private _samples = [];
{
    _samples pushBack (createHashMapFromArray
    [
        ["position", _x],
        ["grid", mapGridPosition _x]
    ]);
} forEach [[0, 0], [worldSize / 2, worldSize / 2], [_edge, _edge]];

createHashMapFromArray
[
    ["world", createHashMapFromArray
    [
        ["name", worldName],
        ["sizeMeters", worldSize],
        ["grid", createHashMapFromArray [["format", "arma-map-grid"], ["samples", _samples]]]
    ]],
    ["locations", _locations]
]
