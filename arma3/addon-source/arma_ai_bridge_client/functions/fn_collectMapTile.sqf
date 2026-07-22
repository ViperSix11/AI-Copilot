params ["_minX", "_minY", "_maxX", "_maxY", "_sampleSpacing"];

private _records = [];
private _centre = [(_minX + _maxX) / 2, (_minY + _maxY) / 2, 0];
private _radius = sqrt (((_maxX - _minX) / 2) ^ 2 + ((_maxY - _minY) / 2) ^ 2) + 2;
private _inside =
{
    params ["_position"];
    private _x = _position select 0;
    private _y = _position select 1;
    _x >= _minX && { _y >= _minY } &&
    { if (_maxX >= worldSize) then { _x <= _maxX } else { _x < _maxX } } &&
    { if (_maxY >= worldSize) then { _y <= _maxY } else { _y < _maxY } }
};

{
    private _position = locationPosition _x;
    private _name = text _x;
    private _locationType = type _x;
    if (_name != "" && { _locationType != "" } && { [_position] call _inside }) then
    {
        _records pushBack (createHashMapFromArray
        [
            ["kind", "location"],
            ["name", _name select [0, 256]],
            ["locationType", _locationType select [0, 64]],
            ["positionASL", [_position select 0, _position select 1, getTerrainHeightASL _position]]
        ]);
    };
} forEach (nearestLocations [_centre, [], _radius]);
uiSleep 0.001;

private _waterSamples = 0;
private _landSamples = 0;
private _startX = _minX + (_sampleSpacing / 2);
private _startY = _minY + (_sampleSpacing / 2);
for "_x" from _startX to _maxX step _sampleSpacing do
{
    for "_y" from _startY to _maxY step _sampleSpacing do
    {
        private _position = [_x min _maxX, _y min _maxY];
        private _water = surfaceIsWater _position;
        if (_water) then { _waterSamples = _waterSamples + 1; } else { _landSamples = _landSamples + 1; };
        private _normal = surfaceNormal _position;
        private _normalZ = (((_normal select 2) max -1) min 1);
        _records pushBack (createHashMapFromArray
        [
            ["kind", "terrain"],
            ["positionASL", [_position select 0, _position select 1, getTerrainHeightASL _position]],
            ["slopeDegrees", acos _normalZ],
            ["water", _water]
        ]);
    };
    uiSleep 0.001;
};

private _buildingTypes =
[
    "HOUSE", "BUILDING", "BUNKER", "RUIN", "CHAPEL", "CHURCH",
    "FUELSTATION", "HOSPITAL", "LIGHTHOUSE", "FORTRESS", "FOUNTAIN",
    "POWER LINES", "RAILWAY", "TRANSMITTER", "VIEW-TOWER", "WATERTOWER"
];
{
    private _position = getPosWorld _x;
    if ([_position] call _inside) then
    {
        private _modelInfo = getModelInfo _x;
        private _model = if ((count _modelInfo) > 1) then { _modelInfo select 1 } else { "" };
        _records pushBack (createHashMapFromArray
        [
            ["kind", "building"],
            ["class", (typeOf _x) select [0, 256]],
            ["model", _model select [0, 384]],
            ["terrainType", "BUILDING"],
            ["positionASL", _position]
        ]);
    };
} forEach (nearestTerrainObjects [_centre, _buildingTypes, _radius, false, true]);
uiSleep 0.001;

{
    private _info = getRoadInfo _x;
    if ((count _info) >= 9) then
    {
        _info params ["_roadType", "_width", "_pedestrian", "_texture", "_textureEnd", "_material", "_begin", "_end", "_bridge"];
        private _midpoint = [((_begin select 0) + (_end select 0)) / 2, ((_begin select 1) + (_end select 1)) / 2];
        if ([_midpoint] call _inside) then
        {
            _records pushBack (createHashMapFromArray
            [
                ["kind", "road"],
                ["roadType", _roadType select [0, 64]],
                ["widthMeters", _width],
                ["pedestrian", _pedestrian],
                ["beginASL", _begin],
                ["endASL", _end],
                ["bridge", _bridge]
            ]);
        };
    };
} forEach (_centre nearRoads _radius);
uiSleep 0.001;

private _trees = 0;
private _bushes = 0;
private _forests = 0;
{
    private _position = getPosWorld _x;
    if ([_position] call _inside) then
    {
        private _model = toLower ((getModelInfo _x) param [0, ""]);
        if (_model find "bush" >= 0) then { _bushes = _bushes + 1; }
        else { _trees = _trees + 1; };
    };
} forEach (nearestTerrainObjects [_centre, ["TREE", "SMALL TREE", "BUSH", "FOREST", "FOREST BORDER", "FOREST SQUARE", "FOREST TRIANGLE"], _radius, false, true]);
private _areaHectares = ((_maxX - _minX) * (_maxY - _minY)) / 10000;
_records pushBack (createHashMapFromArray
[
    ["kind", "vegetation"],
    ["treeCount", _trees],
    ["bushCount", _bushes],
    ["forestCount", _forests],
    ["densityPerHectare", if (_areaHectares > 0) then { (_trees + _bushes + _forests) / _areaHectares } else { 0 }]
]);
private _classification = if (_waterSamples > 0 && { _landSamples > 0 }) then { "coast" } else { if (_waterSamples > 0) then { "water" } else { "land" } };
_records pushBack (createHashMapFromArray
[
    ["kind", "water"],
    ["classification", _classification],
    ["waterSamples", _waterSamples],
    ["landSamples", _landSamples]
]);
_records
