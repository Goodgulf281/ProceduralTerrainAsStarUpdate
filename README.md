
In episode 5, I show how building changes the grid of the custom A* pathfinding with a single API call:

```
                    TerrainGraphIntegration terrainGraphIntegration = TerrainGraphIntegration.Instance;
                    if (terrainGraphIntegration)
                    {
                        if (position != Vector3.zero)
                        {
                            Debug.LogWarning("<color=green>InvalidateObstacleCache</color>");
                            terrainGraphIntegration.InvalidateObstacleCache(position, 8.0f);
                        }
                    }
```

Check out the video: https://youtu.be/49oQ7giICds
