params ["_parameters"];

private _fail = {
    params ["_reason"];
    createHashMapFromArray [["available", false], ["reason", _reason]]
};
private _finiteNumber = { params ["_value"]; _value isEqualType 0 && { finite _value } };

private _range = _parameters getOrDefault ["rangeMeters", -1];
private _bearing = _parameters getOrDefault ["bearingDegrees", -1];
private _fingerprint = _parameters getOrDefault ["profileFingerprint", ""];
private _hasElevation = "targetElevationAslMeters" in _parameters;
private _hasHeight = "targetHeightAboveTerrainMeters" in _parameters;
if !([_range] call _finiteNumber && { _range >= 25 && { _range <= 5000 } }) exitWith { ["ace_ballistic_argument_invalid"] call _fail };
if !([_bearing] call _finiteNumber) exitWith { ["ace_ballistic_argument_invalid"] call _fail };
if (_hasElevation isEqualTo _hasHeight) exitWith { ["ace_ballistic_argument_invalid"] call _fail };
if !(_fingerprint isEqualType "" && { count _fingerprint > 0 && { count _fingerprint <= 128 } }) exitWith { ["weapon_changed_during_calculation"] call _fail };
_bearing = (_bearing % 360 + 360) % 360;

if !(isClass (configFile >> "CfgPatches" >> "ace_advanced_ballistics")) exitWith { ["ace_advanced_ballistics_disabled"] call _fail };
if !(missionNamespace getVariable ["ace_advanced_ballistics_enabled", false]) exitWith { ["ace_advanced_ballistics_disabled"] call _fail };
private _aceVersion = getText (configFile >> "CfgPatches" >> "ace_main" >> "versionStr");
if !((_aceVersion find "3.21.") isEqualTo 0) exitWith { ["ace_advanced_ballistics_version_unsupported"] call _fail };
if (isNil "ace_atragmx_fnc_calculate_solution" || { isNil "ace_advanced_ballistics_fnc_readAmmoDataFromConfig" } ||
    { isNil "ace_advanced_ballistics_fnc_readWeaponDataFromConfig" } || { isNil "ace_scopes_fnc_getBoreHeight" } ||
    { isNil "ace_scopes_fnc_getCurrentZeroRange" }) exitWith { ["ace_advanced_ballistics_interface_unsupported"] call _fail };

private _session = missionNamespace getVariable ["AAB_sessionId", ""];
private _weapon = currentWeapon player;
private _muzzle = currentMuzzle player;
private _fireMode = currentWeaponMode player;
private _magazine = currentMagazine player;
private _magazineConfig = configFile >> "CfgMagazines" >> _magazine;
private _ammo = getText (_magazineConfig >> "ammo");
private _ammoConfig = configFile >> "CfgAmmo" >> _ammo;
private _zeroing = player currentZeroing [_weapon, _muzzle];
private _zeroRange = _zeroing param [0, 0];
private _variationEnabled = missionNamespace getVariable ["ace_advanced_ballistics_muzzleVelocityVariationEnabled", false];
private _temperatureEnabled = missionNamespace getVariable ["ace_advanced_ballistics_ammoTemperatureEnabled", false];
private _barrelEnabled = missionNamespace getVariable ["ace_advanced_ballistics_barrelLengthInfluenceEnabled", false];
private _currentFingerprint = str (hashValue (toJSON
[
    _session, _weapon, _muzzle, _fireMode, _magazine, _ammo, _zeroRange, _aceVersion, true,
    _variationEnabled, _temperatureEnabled, _barrelEnabled
]));
if !(_currentFingerprint isEqualTo _fingerprint) exitWith { ["weapon_changed_during_calculation"] call _fail };
if (_weapon isEqualTo "" || { _magazine isEqualTo "" }) exitWith { ["no_current_weapon"] call _fail };
if !(_ammo isKindOf ["BulletBase", configFile >> "CfgAmmo"]) exitWith { ["unsupported_projectile"] call _fail };

private _coefficients = getArray (_ammoConfig >> "ACE_ballisticCoefficients");
private _boundaries = getArray (_ammoConfig >> "ACE_velocityBoundaries");
private _dragModel = getNumber (_ammoConfig >> "ACE_dragModel");
private _atmosphere = toUpper (getText (_ammoConfig >> "ACE_standardAtmosphere"));
private _muzzleVelocities = getArray (_ammoConfig >> "ACE_muzzleVelocities");
private _barrelLengths = getArray (_ammoConfig >> "ACE_barrelLengths");
private _temperatureShifts = getArray (_ammoConfig >> "ACE_ammoTempMuzzleVelocityShifts");
private _validPositive = { params ["_values", "_limit"]; count _values <= _limit && { { !(_x isEqualType 0) || { !finite _x } || { _x <= 0 } } count _values isEqualTo 0 } };
private _validFinite = { params ["_values", "_limit"]; count _values <= _limit && { { !(_x isEqualType 0) || { !finite _x } } count _values isEqualTo 0 } };
if !(_dragModel in [1, 2, 5, 6, 7, 8] && { count _coefficients isEqualTo 1 } &&
    { count _boundaries isEqualTo 0 } && { [_coefficients, 8] call _validPositive } &&
    { [_boundaries, 7] call _validPositive } && { _atmosphere in ["ICAO", "ASM"] } &&
    { count _muzzleVelocities isEqualTo count _barrelLengths } && { [_muzzleVelocities, 32] call _validPositive } &&
    { [_barrelLengths, 32] call _validPositive } && { !_temperatureEnabled || { count _temperatureShifts isEqualTo 11 && { [_temperatureShifts, 11] call _validFinite } } } &&
    { !_barrelEnabled || { count _muzzleVelocities > 0 } }) exitWith { ["ace_ballistic_profile_incomplete"] call _fail };

private _shooter = eyePos player;
private _targetX = (_shooter select 0) + (sin _bearing) * _range;
private _targetY = (_shooter select 1) + (cos _bearing) * _range;
private _targetElevation = if (_hasElevation) then { _parameters get "targetElevationAslMeters" } else
    { getTerrainHeightASL [_targetX, _targetY] + (_parameters get "targetHeightAboveTerrainMeters") };
if !([_targetElevation] call _finiteNumber && { _targetElevation >= -1000 && { _targetElevation <= 10000 } }) exitWith { ["ace_ballistic_argument_invalid"] call _fail };

private _magazineSpeed = getNumber (_magazineConfig >> "initSpeed");
private _muzzleConfig = configFile >> "CfgWeapons" >> _weapon >> _muzzle;
if !(isClass _muzzleConfig) then { _muzzleConfig = configFile >> "CfgWeapons" >> _weapon; };
private _weaponSpeed = if (isNumber (_muzzleConfig >> "initSpeed")) then { getNumber (_muzzleConfig >> "initSpeed") } else { 0 };
private _muzzleVelocity = if (_weaponSpeed > 0) then { _weaponSpeed } else { if (_weaponSpeed < 0) then { _magazineSpeed * abs _weaponSpeed } else { _magazineSpeed } };
private _items = if (_weapon isEqualTo primaryWeapon player) then { primaryWeaponItems player } else { if (_weapon isEqualTo handgunWeapon player) then { handgunItems player } else { secondaryWeaponItems player } };
private _muzzleDevice = _items param [0, ""];
if !(_muzzleDevice isEqualTo "") then {
    private _itemInfo = configFile >> "CfgWeapons" >> _muzzleDevice >> "ItemInfo";
    if (isNumber (_itemInfo >> "MagazineCoef" >> "initSpeed")) then { _muzzleVelocity = _muzzleVelocity * getNumber (_itemInfo >> "MagazineCoef" >> "initSpeed"); };
};

private _ammoData = _ammo call ace_advanced_ballistics_fnc_readAmmoDataFromConfig;
private _weaponData = [_weapon, _muzzle] call ace_advanced_ballistics_fnc_readWeaponDataFromConfig;
_ammoData params ["_airFriction", "_caliber", "_bulletLength", "_bulletMass", "_transonic", "_aceDrag", "_aceCoefficients", "_aceBoundaries", "_aceAtmosphere", "_aceTempShifts", "_aceMuzzleTable", "_aceBarrelTable", "_variationSd"];
_weaponData params ["_barrelTwist", "_twistDirection", "_barrelLength"];
private _temperature = (_shooter select 2) call ace_weather_fnc_calculateTemperatureAtHeight;
if (_barrelEnabled) then { _muzzleVelocity = _muzzleVelocity + ([_barrelLength, _aceMuzzleTable, _aceBarrelTable, _muzzleVelocity] call ace_advanced_ballistics_fnc_calculateBarrelLengthVelocityShift); };
if (_temperatureEnabled) then { _muzzleVelocity = _muzzleVelocity + ([_aceTempShifts, _temperature] call ace_advanced_ballistics_fnc_calculateAmmoTemperatureVelocityShift); };
if !([_muzzleVelocity] call _finiteNumber && { _muzzleVelocity > 0 }) exitWith { ["missing_ballistic_config"] call _fail };

private _pressure = (_shooter select 2) call ace_weather_fnc_calculateBarometricPressure;
private _stability = if (_caliber * _bulletLength * _bulletMass * _barrelTwist > 0) then
    { [_caliber, _bulletLength, _bulletMass, _barrelTwist, _muzzleVelocity, _temperature, _pressure] call ace_advanced_ballistics_fnc_calculateStabilityFactor } else { 1.5 };
private _weaponIndex = if (_weapon isEqualTo primaryWeapon player) then { 0 } else { if (_weapon isEqualTo secondaryWeapon player) then { 1 } else { if (_weapon isEqualTo handgunWeapon player) then { 2 } else { -1 } } };
if (_weaponIndex < 0) exitWith { ["ace_scope_zero_unsupported"] call _fail };
private _boreHeight = [player, _weaponIndex] call ace_scopes_fnc_getBoreHeight;
if (_muzzle isEqualTo _weapon) then { _zeroRange = [player] call ace_scopes_fnc_getCurrentZeroRange; };
if !([_zeroRange] call _finiteNumber && { _zeroRange >= 25 && { _zeroRange <= 5000 } }) exitWith { ["ace_scope_zero_unsupported"] call _fail };

private _windSpeed = vectorMagnitude wind;
private _windClock = (((windDir - _bearing) % 360 + 360) % 360) / 30;
private _latitude = missionNamespace getVariable ["ace_common_mapLatitude", 0];
private _humidity = missionNamespace getVariable ["ace_weather_currentHumidity", 0.5];
private _inclination = atan ((_targetElevation - (_shooter select 2)) / _range);
private _solve = {
    params ["_targetRange", "_targetInclination"];
    [0, _bulletMass, _boreHeight, _airFriction, _muzzleVelocity, _temperature, _pressure, _humidity,
        20, [_windSpeed, _windSpeed], _windClock, _targetInclination, 0, _targetRange,
        _aceCoefficients select 0, _aceDrag, _aceAtmosphere, false, _stability, _twistDirection,
        _latitude, _bearing] call ace_atragmx_fnc_calculate_solution
};
private _started = diag_tickTime;
private _targetSolution = [_range, _inclination] call _solve;
private _zeroSolution = [_zeroRange, 0] call _solve;
if ((diag_tickTime - _started) > 8) exitWith { ["ace_ballistic_solver_timeout"] call _fail };
if !(count _targetSolution >= 9 && { count _zeroSolution >= 9 }) exitWith { ["ace_ballistic_extension_failed"] call _fail };
private _targetElevationMoa = _targetSolution select 0;
private _zeroElevationMoa = _zeroSolution select 0;
private _horizontalMoa = (_targetSolution select 1) select 0;
private _tof = _targetSolution select 3;
private _impactVelocity = _targetSolution select 4;
if ({ !([_x] call _finiteNumber) } count [_targetElevationMoa, _zeroElevationMoa, _horizontalMoa, _tof, _impactVelocity] > 0) exitWith { ["ace_ballistic_extension_failed"] call _fail };
if !((missionNamespace getVariable ["AAB_sessionId", ""]) isEqualTo _session && { currentWeapon player isEqualTo _weapon } && { currentMuzzle player isEqualTo _muzzle } && { currentMagazine player isEqualTo _magazine }) exitWith { ["weapon_changed_during_calculation"] call _fail };

private _verticalMrad = (_targetElevationMoa - _zeroElevationMoa) * 0.290888;
private _horizontalMrad = _horizontalMoa * 0.290888;
createHashMapFromArray
[
    ["available", true], ["model", "ace3-advanced-ballistics"], ["nominalSolution", true],
    ["rangeMeters", _range], ["bearingDegrees", _bearing], ["shooterElevationAslMeters", _shooter select 2],
    ["targetElevationAslMeters", _targetElevation], ["heightDifferenceMeters", _targetElevation - (_shooter select 2)],
    ["currentZeroingMeters", _zeroRange], ["requiredElevationAngleDegrees", _targetElevationMoa / 60],
    ["currentZeroElevationAngleDegrees", _zeroElevationMoa / 60], ["elevationCorrectionDegrees", (_targetElevationMoa - _zeroElevationMoa) / 60],
    ["elevationCorrectionMilliradians", _verticalMrad], ["verticalHoldDirection", if (abs _verticalMrad < 0.1) then { "no material correction" } else { if (_verticalMrad > 0) then { "high" } else { "low" } }],
    ["horizontalCorrectionMilliradians", _horizontalMrad], ["horizontalHoldDirection", if (abs _horizontalMrad < 0.1) then { "no material correction" } else { if (_horizontalMrad > 0) then { "right" } else { "left" } }],
    ["timeOfFlightSeconds", _tof], ["predictedImpactVelocityMetersPerSecond", _impactVelocity],
    ["terrainPointAssumed", !_hasElevation], ["windCorrectionAvailable", true],
    ["muzzleVelocityMetersPerSecond", _muzzleVelocity], ["muzzleVelocityVariationEnabled", _variationEnabled],
    ["muzzleVelocityVariationStandardDeviationPercent", (_variationSd * 100) max 0 min 10]
]
