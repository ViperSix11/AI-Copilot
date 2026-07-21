private _unit = player;
private _origin = getPosATL _unit;
private _view = eyeDirection _unit;
private _horizontal = [_view select 0, _view select 1, 0];
private _magnitude = sqrt (((_horizontal select 0) ^ 2) + ((_horizontal select 1) ^ 2));

if (_magnitude < 0.001) then
{
    _horizontal = vectorDir _unit;
    _magnitude = sqrt (((_horizontal select 0) ^ 2) + ((_horizontal select 1) ^ 2));
};

_horizontal =
[
    (_horizontal select 0) / (_magnitude max 0.001),
    (_horizontal select 1) / (_magnitude max 0.001),
    0
];

private _viewHeading = ((_horizontal select 0) atan2 (_horizontal select 1));
if (_viewHeading < 0) then { _viewHeading = _viewHeading + 360; };

private _buildingTypes = ["HOUSE", "BUILDING", "BUNKER", "RUIN", "CHAPEL", "CHURCH"];
private _vegetationTypes = ["TREE", "SMALL TREE", "BUSH", "FOREST", "FOREST BORDER", "FOREST SQUARE", "FOREST TRIANGLE"];
private _probeDefinitions = [[150, 60], [350, 100], [650, 160]];
private _probes = [];

{
    _x params ["_distance", "_radius"];
    private _center = _origin vectorAdd
    [
        (_horizontal select 0) * _distance,
        (_horizontal select 1) * _distance,
        0
    ];

    private _buildings = nearestTerrainObjects [_center, _buildingTypes, _radius, false, true];
    private _vegetation = nearestTerrainObjects [_center, _vegetationTypes, _radius, false, true];
    private _nearestBuildingDistance = -1;

    {
        private _distanceToPlayer = _origin distance2D (getPosATL _x);
        if (_nearestBuildingDistance < 0 || { _distanceToPlayer < _nearestBuildingDistance }) then
        {
            _nearestBuildingDistance = _distanceToPlayer;
        };
    } forEach _buildings;

    private _vegetationCount = count _vegetation;
    private _buildingCount = count _buildings;
    private _forestLikely = _vegetationCount >= 25;

    _probes pushBack (createHashMapFromArray
    [
        ["distanceMeters", _distance],
        ["radiusMeters", _radius],
        ["center", _center],
        ["buildingCount", _buildingCount],
        ["vegetationCount", _vegetationCount],
        ["forestLikely", _forestLikely],
        ["buildingsInVegetation", (_buildingCount > 0 && _forestLikely)],
        ["nearestBuildingDistanceFromPlayer", _nearestBuildingDistance]
    ]);
} forEach _probeDefinitions;

createHashMapFromArray
[
    ["viewHeading", _viewHeading],
    ["sampledAt", time],
    ["probes", _probes]
]
