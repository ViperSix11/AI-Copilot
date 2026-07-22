private _gazetteer = call AAB_fnc_collectMapGazetteer;
private _locations = _gazetteer getOrDefault ["locations", []];
private _world = _gazetteer getOrDefault ["world", createHashMap];
private _pageSize = 64;
private _pageCount = (ceil ((count _locations) / _pageSize)) max 1;
private _gazetteerSequence = (missionNamespace getVariable ["AAB_gazetteerSequence", 0]) + 1;
private _gazetteerId = format ["gazetteer-%1", _gazetteerSequence];

for "_pageIndex" from 0 to (_pageCount - 1) do
{
    private _payload = createHashMapFromArray
    [
        ["gazetteerId", _gazetteerId],
        ["pageIndex", _pageIndex],
        ["pageCount", _pageCount],
        ["world", _world],
        ["locations", _locations select [_pageIndex * _pageSize, _pageSize]]
    ];
    ["arma-ai-bridge/arma3/map-gazetteer-v1", _payload] call AAB_fnc_publishWorldEvent;
};

missionNamespace setVariable ["AAB_gazetteerSequence", _gazetteerSequence];
missionNamespace setVariable ["AAB_lastGazetteerAt", diag_tickTime];
true
