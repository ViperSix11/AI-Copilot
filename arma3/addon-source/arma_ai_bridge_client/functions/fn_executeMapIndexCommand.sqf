params ["_request"];

private _command = toLower (_request getOrDefault ["command", ""]);
private _parameters = _request getOrDefault ["parameters", createHashMap];
if !(_parameters isEqualType createHashMap) exitWith { false };

private _sessionId = _parameters getOrDefault ["sessionId", ""];
private _exportId = _parameters getOrDefault ["exportId", ""];
private _fingerprint = toLower (_parameters getOrDefault ["fingerprint", ""]);
if !(_sessionId isEqualTo (missionNamespace getVariable ["AAB_sessionId", ""])) exitWith { false };
if !(_exportId isEqualType "" && { (count _exportId) >= 1 } && { (count _exportId) <= 128 }) exitWith { false };
if !(_fingerprint isEqualType "" && { (count _fingerprint) isEqualTo 64 }) exitWith { false };

if (_command isEqualTo "cancel") exitWith
{
    if (
        _exportId isEqualTo (missionNamespace getVariable ["AAB_mapExportId", ""]) &&
        { _fingerprint isEqualTo (missionNamespace getVariable ["AAB_mapFingerprint", ""] ) }
    ) then
    {
        missionNamespace setVariable ["AAB_mapExportGeneration", (missionNamespace getVariable ["AAB_mapExportGeneration", 0]) + 1];
        missionNamespace setVariable ["AAB_mapExportActive", false];
    };
    true
};

if !(_command isEqualTo "start") exitWith { false };
private _indexVersion = _parameters getOrDefault ["indexVersion", 0];
private _startTile = _parameters getOrDefault ["startTileOrdinal", -1];
private _tileSize = _parameters getOrDefault ["tileSizeMeters", 0];
private _sampleSpacing = _parameters getOrDefault ["terrainSampleSpacingMeters", 0];
private _pageSize = _parameters getOrDefault ["maxRecordsPerPage", 0];
if !(_indexVersion isEqualTo 1) exitWith { false };
if !(_tileSize isEqualType 0 && { _tileSize >= 256 } && { _tileSize <= 1024 }) exitWith { false };
if !(_sampleSpacing isEqualType 0 && { _sampleSpacing >= 32 } && { _sampleSpacing <= 256 }) exitWith { false };
if !(_pageSize isEqualType 0 && { _pageSize >= 16 } && { _pageSize <= 96 }) exitWith { false };
private _columns = ceil (worldSize / _tileSize);
private _totalTiles = _columns * _columns;
if !(_startTile isEqualType 0 && { _startTile >= 0 } && { _startTile <= _totalTiles }) exitWith { false };

private _generation = (missionNamespace getVariable ["AAB_mapExportGeneration", 0]) + 1;
missionNamespace setVariable ["AAB_mapExportGeneration", _generation];
missionNamespace setVariable ["AAB_mapExportActive", true];
missionNamespace setVariable ["AAB_mapExportId", _exportId];
missionNamespace setVariable ["AAB_mapFingerprint", _fingerprint];
[
    _generation, _sessionId, _exportId, _fingerprint, _startTile,
    _tileSize, _sampleSpacing, _pageSize
] spawn AAB_fnc_exportMapKnowledge;
true
