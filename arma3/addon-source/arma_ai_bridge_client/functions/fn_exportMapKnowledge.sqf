params ["_generation", "_sessionId", "_exportId", "_fingerprint", "_startTile", "_tileSize", "_sampleSpacing", "_pageSize"];

private _columns = ceil (worldSize / _tileSize);
private _totalTiles = _columns * _columns;
private _publishProgress =
{
    params ["_status", "_completed", "_next", ["_error", objNull]];
    ["arma-ai-bridge/arma3/map-index-progress-v1", createHashMapFromArray
        [
            ["exportId", _exportId], ["fingerprint", _fingerprint], ["indexVersion", 1],
            ["status", _status], ["completedTiles", _completed], ["totalTiles", _totalTiles],
            ["nextTileOrdinal", _next], ["errorCode", _error]
        ]
    ] call AAB_fnc_publishWorldEvent;
};

["started", _startTile, _startTile] call _publishProgress;
private _cancelled = false;
private _failed = false;
private _completed = _startTile;

for "_ordinal" from _startTile to (_totalTiles - 1) do
{
    if (
        _generation != (missionNamespace getVariable ["AAB_mapExportGeneration", -1]) ||
        { !(_sessionId isEqualTo (missionNamespace getVariable ["AAB_sessionId", ""])) }
    ) exitWith { _cancelled = true; };

    private _column = _ordinal mod _columns;
    private _row = floor (_ordinal / _columns);
    private _minX = _column * _tileSize;
    private _minY = _row * _tileSize;
    private _maxX = (_minX + _tileSize) min worldSize;
    private _maxY = (_minY + _tileSize) min worldSize;
    private _records = [_minX, _minY, _maxX, _maxY, _sampleSpacing] call AAB_fnc_collectMapTile;
    private _pageCount = 1 max ceil ((count _records) / _pageSize);
    if (_pageCount > 256) exitWith { _failed = true; };

    for "_pageIndex" from 0 to (_pageCount - 1) do
    {
        if (_generation != (missionNamespace getVariable ["AAB_mapExportGeneration", -1])) exitWith { _cancelled = true; };
        private _start = _pageIndex * _pageSize;
        private _pageRecords = _records select [_start, _pageSize];
        ["arma-ai-bridge/arma3/map-tile-v1", createHashMapFromArray
            [
                ["exportId", _exportId], ["fingerprint", _fingerprint], ["indexVersion", 1],
                ["tile", createHashMapFromArray
                    [
                        ["ordinal", _ordinal], ["column", _column], ["row", _row],
                        ["minX", _minX], ["minY", _minY], ["maxX", _maxX], ["maxY", _maxY]
                    ]
                ],
                ["pageIndex", _pageIndex], ["pageCount", _pageCount], ["records", _pageRecords]
            ]
        ] call AAB_fnc_publishWorldEvent;
        uiSleep 0.005;
    };
    if (_cancelled) exitWith {};
    _completed = _ordinal + 1;
    ["indexing", _completed, _completed] call _publishProgress;
    uiSleep 0.02;
};

if (_generation isEqualTo (missionNamespace getVariable ["AAB_mapExportGeneration", -1])) then
{
    missionNamespace setVariable ["AAB_mapExportActive", false];
};
if (_failed) exitWith { ["failed", _completed, _completed, "tile_page_limit_exceeded"] call _publishProgress; };
if (_cancelled) exitWith { ["cancelled", _completed, _completed] call _publishProgress; };
["completed", _totalTiles, _totalTiles] call _publishProgress;
