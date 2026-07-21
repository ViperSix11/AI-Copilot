params ["_parameters"];

private _origin = getPosATL player;
private _shape = toLower (_parameters getOrDefault ["shape", "circle"]);
if !(_shape in ["circle", "cone"]) then { _shape = "circle"; };

private _directionMode = toLower (_parameters getOrDefault ["direction", "view"]);
private _heading = getDir player;
if (_directionMode isEqualTo "view") then
{
    private _view = eyeDirection player;
    _heading = ((_view select 0) atan2 (_view select 1));
    if (_heading < 0) then { _heading = _heading + 360; };
};

private _range = if (_shape isEqualTo "circle") then
{
    _parameters getOrDefault ["radiusMeters", 300]
}
else
{
    _parameters getOrDefault ["rangeMeters", 800]
};
_range = ((_range max 25) min 1500);

private _angle = (((_parameters getOrDefault ["angleDegrees", 40]) max 5) min 180);
private _limit = round (((_parameters getOrDefault ["maxResultsPerCategory", 25]) max 1) min 50);
private _categories = _parameters getOrDefault ["categories", ["building", "vegetation"]];
if !(_categories isEqualType []) then { _categories = ["building", "vegetation"]; };

private _categoryTypes = createHashMapFromArray
[
    ["building", ["HOUSE", "BUILDING", "BUNKER", "RUIN", "CHAPEL", "CHURCH", "FUELSTATION", "HOSPITAL"]],
    ["vegetation", ["TREE", "SMALL TREE", "BUSH", "FOREST", "FOREST BORDER", "FOREST SQUARE", "FOREST TRIANGLE"]],
    ["road", ["ROAD", "MAIN ROAD", "TRACK", "TRAIL"]],
    ["wall", ["WALL", "FENCE"]],
    ["rock", ["ROCK", "ROCKS"]]
];

private _normaliseAngle =
{
    params ["_value"];
    private _normalised = _value;
    while { _normalised > 180 } do { _normalised = _normalised - 360; };
    while { _normalised < -180 } do { _normalised = _normalised + 360; };
    _normalised
};

private _bearingTo =
{
    params ["_from", "_to"];
    private _dx = (_to select 0) - (_from select 0);
    private _dy = (_to select 1) - (_from select 1);
    private _bearing = _dx atan2 _dy;
    if (_bearing < 0) then { _bearing = _bearing + 360; };
    _bearing
};

private _results = createHashMap;
private _acceptedBuildings = [];
private _vegetationCount = 0;
private _buildingCount = 0;
private _areaSquareMeters = if (_shape isEqualTo "circle") then
{
    3.14159265 * _range * _range
}
else
{
    3.14159265 * _range * _range * (_angle / 360)
};

{
    private _category = toLower _x;
    private _types = _categoryTypes getOrDefault [_category, []];
    if !(_types isEqualTo []) then
    {
        private _candidates = nearestTerrainObjects [_origin, _types, _range, false, true];
        private _matches = [];

        {
            private _position = getPosATL _x;
            private _distance = _origin distance2D _position;
            private _bearing = [_origin, _position] call _bearingTo;
            private _relativeBearing = [_bearing - _heading] call _normaliseAngle;
            private _inside = (_shape isEqualTo "circle") || { abs _relativeBearing <= (_angle / 2) };

            if (_inside) then
            {
                _matches pushBack [_distance, _forEachIndex, _x, _position, _bearing, _relativeBearing];
            };
        } forEach _candidates;

        _matches sort true;
        private _totalCount = count _matches;
        private _nearestDistance = if (_totalCount > 0) then { (_matches select 0) select 0 } else { -1 };
        private _objects = [];

        {
            if (_forEachIndex >= _limit) exitWith {};
            _x params ["_distance", "_sortIndex", "_object", "_position", "_bearing", "_relativeBearing"];
            private _modelInfo = getModelInfo _object;
            private _modelName = if ((count _modelInfo) > 0) then { _modelInfo select 0 } else { "" };

            _objects pushBack (createHashMapFromArray
            [
                ["class", typeOf _object],
                ["model", _modelName],
                ["positionATL", _position],
                ["distanceMeters", _distance],
                ["bearingDegrees", _bearing],
                ["relativeBearingDegrees", _relativeBearing]
            ]);
        } forEach _matches;

        if (_category isEqualTo "building") then
        {
            _buildingCount = _totalCount;
            _acceptedBuildings = _matches;
        };
        if (_category isEqualTo "vegetation") then
        {
            _vegetationCount = _totalCount;
        };

        _results set [_category, createHashMapFromArray
        [
            ["totalCount", _totalCount],
            ["returnedCount", count _objects],
            ["nearestDistanceMeters", _nearestDistance],
            ["objects", _objects]
        ]];
    };
} forEach _categories;

private _buildingsNearDenseVegetation = 0;
if ((_buildingCount > 0) && { _vegetationCount > 0 }) then
{
    {
        if (_forEachIndex >= 50) exitWith {};
        private _building = _x select 2;
        private _nearVegetation = nearestTerrainObjects [getPosATL _building, _categoryTypes get "vegetation", 60, false, true];
        if ((count _nearVegetation) >= 10) then
        {
            _buildingsNearDenseVegetation = _buildingsNearDenseVegetation + 1;
        };
    } forEach _acceptedBuildings;
};

private _vegetationPerHectare = if (_areaSquareMeters > 0) then
{
    _vegetationCount / (_areaSquareMeters / 10000)
}
else
{
    0
};

createHashMapFromArray
[
    ["query", createHashMapFromArray
        [
            ["origin", "player"],
            ["originPositionATL", _origin],
            ["shape", _shape],
            ["direction", _directionMode],
            ["headingDegrees", _heading],
            ["rangeMeters", _range],
            ["angleDegrees", if (_shape isEqualTo "cone") then { _angle } else { 360 }],
            ["maxResultsPerCategory", _limit],
            ["areaSquareMeters", _areaSquareMeters]
        ]
    ],
    ["categories", _results],
    ["analysis", createHashMapFromArray
        [
            ["vegetationObjectsPerHectare", _vegetationPerHectare],
            ["forestLikely", (_vegetationPerHectare >= 8 && _vegetationCount >= 20)],
            ["buildingsNearDenseVegetation", _buildingsNearDenseVegetation]
        ]
    ]
]
