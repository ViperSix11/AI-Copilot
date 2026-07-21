# Telemetry schema v1

The root document is a JSON object.

```json
{
  "schema": "ai-copilot/arma3/telemetry-v1",
  "timestamp": 123.45,
  "map": {
    "name": "Altis",
    "sizeMeters": 30720
  },
  "player": {},
  "vehicle": null,
  "contacts": [],
  "sensorContacts": [],
  "environment": {}
}
```

## Environment probes

The environment scan samples circular zones ahead of the current view vector instead of scanning the whole map.

```json
{
  "viewHeading": 42.5,
  "probes": [
    {
      "distanceMeters": 150,
      "radiusMeters": 60,
      "center": [1234, 5678, 12],
      "buildingCount": 2,
      "vegetationCount": 48,
      "forestLikely": true,
      "buildingsInVegetation": true,
      "nearestBuildingDistanceFromPlayer": 173.2
    }
  ]
}
```

`forestLikely` is a heuristic based on the number of nearby terrain vegetation objects. It is not a claim about map-maker forest polygons.

## Contact privacy

Contact positions are derived from `targetKnowledge`. The schema contains the estimated position and error margin known to the player; it does not add the object's actual hidden position.
