private _worldConfig = configFile >> "CfgWorlds" >> worldName;
private _nullableNumber =
{
    params ["_config"];
    if (isNumber _config) then { getNumber _config } else { objNull }
};

private _terrainInfo = getTerrainInfo;
if ((count _terrainInfo) != 5) then { _terrainInfo = [0, 0, 0, 0, 0]; };

private _product = productVersion;
private _productValue =
{
    params ["_index", "_fallback"];
    if ((count _product) > _index) then { _product select _index } else { _fallback }
};

private _addons = [];
{
    if (_forEachIndex >= 4096) exitWith {};
    if (_x isEqualType [] && { (count _x) >= 5 }) then
    {
        _addons pushBack (createHashMapFromArray
        [
            ["prefix", (_x select 0) select [0, 384]],
            ["version", (_x select 1) select [0, 128]],
            ["patched", _x select 2],
            ["hash", (_x select 4) select [0, 128]]
        ]);
    };
} forEach allAddonsInfo;

private _size = worldSize;
private _centre = _size / 2;
private _gridReferences =
[
    createHashMapFromArray [["position", [0, 0]], ["label", mapGridPosition [0, 0]]],
    createHashMapFromArray [["position", [_centre, _centre]], ["label", mapGridPosition [_centre, _centre]]],
    createHashMapFromArray [["position", [1000, 0]], ["label", mapGridPosition [1000, 0]]],
    createHashMapFromArray [["position", [0, 1000]], ["label", mapGridPosition [0, 1000]]]
];

private _tileSize = 512;
private _columns = ceil (_size / _tileSize);
private _payload = createHashMapFromArray
[
    ["indexVersion", 1],
    ["world", createHashMapFromArray
        [
            ["name", worldName],
            ["sizeMeters", _size],
            ["terrainInfo", _terrainInfo],
            ["config", createHashMapFromArray
                [
                    ["class", configName _worldConfig],
                    ["description", getText (_worldConfig >> "description")],
                    ["mapSize", [_worldConfig >> "mapSize"] call _nullableNumber],
                    ["mapZone", [_worldConfig >> "mapZone"] call _nullableNumber],
                    ["latitude", [_worldConfig >> "latitude"] call _nullableNumber],
                    ["longitude", [_worldConfig >> "longitude"] call _nullableNumber],
                    ["sourceAddons", configSourceAddonList _worldConfig]
                ]
            ],
            ["gridReferences", _gridReferences]
        ]
    ],
    ["product", createHashMapFromArray
        [
            ["shortName", [1, "Arma3"] call _productValue],
            ["version", [2, 0] call _productValue],
            ["build", [3, 0] call _productValue],
            ["buildType", [4, ""] call _productValue],
            ["platform", [6, ""] call _productValue],
            ["architecture", [7, ""] call _productValue],
            ["branch", [8, ""] call _productValue]
        ]
    ],
    ["addons", _addons],
    ["export", createHashMapFromArray
        [
            ["tileSizeMeters", _tileSize],
            ["terrainSampleSpacingMeters", 128],
            ["totalTiles", _columns * _columns],
            ["maxRecordsPerPage", 96]
        ]
    ]
];

missionNamespace setVariable ["AAB_lastMapManifestAt", diag_tickTime];
["arma-ai-bridge/arma3/map-manifest-v1", _payload] call AAB_fnc_publishWorldEvent
