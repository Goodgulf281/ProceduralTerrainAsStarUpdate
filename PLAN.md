# Multiplayer Plan (FishNet)

Plan for making this project network-enabled with FishNet 4.x. Reference guide: `fishnet.md` (FishNet reference notes, section numbers below point to it).

## Decisions

| Decision | Choice |
|---|---|
| Topology | **Host (listen server).** One player's process is both server and client. The code stays compatible with a later dedicated server. |
| Build-piece sync | **One networked object per `BuildingArea`**, holding a synced dictionary of piece records. Pieces themselves are plain local GameObjects on clients. |
| Player movement | **Client-authoritative** `NetworkTransform` and `NetworkAnimator`. Client-side prediction can be added later if cheating becomes a concern. |

## Guiding principles

- **Terrain is never sent over the network.** The server shares the seed, and every machine generates identical chunks and scatter prefabs locally.
- **The server is the authority for the world:** building, terrain edits, structural integrity, pathfinding, NPCs, inventory and save/load. Clients send requests and render the results.
- **On the host, server and client share one process,** so the host's `TerrainStreamingController` and physics serve both roles. Server-only code must check `IsServerInitialized` or `InstanceFinder.IsServerStarted`, not "am I the host".

## Constraints found in the code

1. **Scatter placement is not deterministic across machines.** [AsyncTerrainPrefabPlacer.cs:310](Assets/Goodgulf/TerrainUtils/AsyncTerrainPrefabPlacer.cs) seeds with `rule.prefab.GetInstanceID()`, which differs between the editor, builds and sessions. Colliders and pathfinding would disagree between machines. This must be fixed first (Phase 0).
2. **`TerrainStreamingController` follows a single `player` Transform.** The server needs chunks loaded around every player.
3. **Building has a clean request point.** `BuilderInput` computes the preview, raycast and snap, then calls `Builder.InstantiateBuildingPrefab(templateId, pos, rot, snapIndex)`, which becomes the client-to-server request.
4. **The central services are singletons**: `Builder`, `RecalculationWorker`, `TerrainGraphIntegration` and `GameManager`. They stay singletons, since there is one NetworkManager, but their authoritative logic is gated to the server.
5. **`GameManager.InitializeGameLogic()` is triggered by the streaming controller's initial-load event and enables a scene-placed player.** The player will now be spawned by the network, so this flow changes.
6. **FishNet exists in the main checkout's `Assets/FishNet` but is not committed**, so it isn't in git or this worktree.

---

## Phase 0: Groundwork

- [ ] Put FishNet under version control: commit `Assets/FishNet`, or install through the Git URL (`https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet`).
- [ ] Add Multiplayer Play Mode (`com.unity.multiplayer.playmode`) for testing a host and a client in the editor.
- [x] Per-player chunk caches: `PlayModeInstance.GetInstanceDataPath` gives MPPM virtual players their own `TerrainPrefabCache_<id>` folder.
- [x] `NetworkBootstrap` (on the NetworkManager): starts host, server or client from `-host`/`-server`/`-client` (plus `-address`, `-port`), then MPPM tags `Host`/`Server`/`Client`, then the Inspector default. Set the NetworkHudCanvas Auto Start Type to Disabled.
- [x] Fix placement determinism in `AsyncTerrainPrefabPlacer` (and the older `TerrainPrefabPlacer`):
  - Replaced `GetInstanceID()` in the rule seed with `StableHash.Fnv1a(prefab.name)`.
  - `prefabId` and `PrefabPlacementConfig.GetConfigHash()` also use `StableHash.Fnv1a` instead of `string.GetHashCode()`.
  - `ChunkPrefabCache.CurrentCacheVersion` is now 2, and `IsValid` rejects older caches.
  - Note: rules are identified by prefab *name*, so two different prefabs with the same name in one config would share a seed.
- [ ] Verify determinism: start two editors with the same seed, dump the placed-prefab records for several chunks, and diff them.

## Phase 1: Connecting and spawning the player

Goal: a host and one client walk around the same terrain.

- [ ] Add a NetworkManager scene object (start from `FishNet/Demos/Prefabs/NetworkManager.prefab`):
  - ObserverManager with a **SceneCondition**.
  - PlayerSpawner with **Add To Default Scene** checked.
  - The HUD, for starting host or client.
  - The NetworkManager must not carry a NetworkObject (§4).
- [ ] Player prefab:
  - Add `NetworkObject`, a client-authoritative `NetworkTransform` (Component Configuration = CharacterController) and a client-authoritative `NetworkAnimator`.
  - Move the scene player into this prefab and assign it to PlayerSpawner.
- [ ] Convert `ThirdPersonController` to a `NetworkBehaviour`:
  - Guard `Update` and the input callbacks with `IsOwner`.
  - Keep `PlayerInput` disabled on the prefab. Enable it in `OnStartClient` when `IsOwner` is true (§27).
  - Disable the `CharacterController` on non-owners, or let the `NetworkTransform` configure it.
- [ ] Camera:
  - Keep one scene camera with `OrbitCamera`.
  - In `OnStartClient`/`OnOwnershipClient`, the owner sets itself as the `OrbitCamera` target and switches `GameManager` from the scene camera to the player camera.
- [ ] Rework `GameManager`:
  - Split `InitializeGameLogic()` into `InitializeWorld()` (server and client: terrain ready, `Builder.CacheTerrains`) and `OnLocalPlayerSpawned(player)` (client: camera, `Builder.SetPlayer`, `OnTerrainReady`).
  - Call `TerrainGraphIntegration` initialization from the server path only (Phase 5).
- [ ] Spawn safety:
  - Hold the local player's gravity and movement until the chunk under it has loaded locally.
  - Snap it to the terrain height when released.
  - The server picks spawn points on already-loaded chunks.

## Phase 2: World sync

- [ ] `WorldState`: a global NetworkObject (Is Global, Initialize Order -128, spawned by `ServerSpawner`) with `SyncVar<int> Seed`.
  - The server sets the seed in `OnStartServer`.
  - Clients apply it to `TerrainStreamingController`, `StreamingTerrainGeneratorJobs` and `AsyncTerrainPrefabPlacer.SetBaseSeed` before streaming starts.
  - Streaming must not auto-start in `Start()`. It waits for the seed.
- [ ] Multi-target streaming. Change `TerrainStreamingController` to follow a list of positions (`RegisterStreamingTarget` / `UnregisterStreamingTarget`):
  - The load set is the union of the radii around all targets.
  - A chunk unloads only when it is outside the unload radius of every target.
  - The server registers every player's transform (subscribe to `OnSpawned`/`OnRemoteConnectionState`).
  - A pure client registers only its own player.
  - The host registers everyone, and also renders chunks around remote players. That cost is accepted for host mode.
- [ ] Terrain edits:
  - `DepressTerrainAtPosition` and `RaiseTerrainAtPosition` become a ServerRpc request, with the server checking reach and rate.
  - The server applies the edit and appends `{chunkCoord, pos, radius, strength}` to a `SyncList<TerrainEdit>` on `WorldState`.
  - Every machine applies new edits to loaded chunks as they arrive, and replays the matching edits when a chunk loads (after generation, before colliders are used).
  - The server then calls `InvalidateHeightmapCache` and `RequestDirtyRegionRebuild`.

## Phase 3: Building (one networked object per BuildingArea)

- [ ] `NetworkBuildingArea` prefab: `NetworkObject` plus a `NetworkBehaviour` wrapping the existing `BuildingArea` data class.
  - `SyncVar<string> AreaId` and `SyncVar<Vector3> Position`.
  - `SyncDictionary<int, PieceRecord> Pieces`, where `PieceRecord` is a struct `{ int Id; int TemplateId; Vector3 Pos; Quaternion Rot; int SnapIndex; }`.
  - Per-area visibility: add a **GridCondition** (or DistanceCondition) to the prefab's `NetworkObserver` in *Add Missing* mode, so clients only receive nearby areas. This needs a HashGrid on the NetworkManager.
- [ ] Server:
  - `Builder.GetOrCreateBuildingArea` instantiates and spawns the `NetworkBuildingArea` (server only).
  - The outline visual (`BuildingAreaOutline`) is created locally on each client in `OnStartClient`.
- [ ] Build request flow:
  1. The client-side `BuilderInput` keeps the transparent preview, raycast, snapping, rotation and magnetic snap, all local.
  2. On confirm, the client calls `ServerRequestBuild(templateId, pos, rot, snapIndex)`. This is a ServerRpc on the player's NetworkBehaviour, where the caller's connection identifies the player.
  3. The server validates:
     - template index
     - inventory
     - `IsInNonBuildableArea`
     - reach distance from the caller's player
     - rate limit
     - an overlap check
  4. The server runs the existing `InstantiateBuildingPrefab` logic: the physics-based grounded and neighbour detection, and `ConstructedItem` registration with an ID from `GetNewGlobalID`. It then adds a `PieceRecord` to the area's `Pieces`.
  5. The server calls `TerrainGraphIntegration.InvalidateObstacleCache(pos, radius)`.
- [ ] Clients: on `Pieces.OnChange`, Add/Set instantiates the plain build prefab (collider on, rigidbody kinematic) under the area, and Remove destroys it. Handle `Complete`, and fill the dictionary on first spawn so late joiners and areas that come into view are built. On the host, skip instantiation when `asServer` is false, because the server already created the object.
- [ ] Destruction (`DestructConstructedItemsAtPos`) becomes a ServerRpc with the same validation. The server removes the records and invalidates the obstacle cache.
- [ ] Inventory: move `PlayerBuilderInventoryBridge` onto the player prefab as a `NetworkBehaviour` with a `SyncDictionary<int,int>` (ReadPermission.OwnerOnly), written only by the server. The client UI reads it; consumption happens only on the server during build validation.

## Phase 4: Structural integrity

- [ ] Run `RecalculationWorker`, `Builder.UpdateWorkerAreas` and neighbour detection only on the server. Disable them on pure clients.
- [ ] Removing weak pieces (`BuildingArea.RemoveWeakConstructedItems`) on the server:
  - Remove the records from `Pieces`.
  - Call `ObserversCollapse(int[] pieceIds)` on the area.
  - Clients run the existing "unfreeze rigidbody, destroy after 10 s" effect on their local copies.
  - The dictionary removal must not destroy a piece that is collapsing. Mark collapsing IDs first (the RPC arrives before the SyncType change, §13).
- [ ] Optional debug: sync `Distance` into `PieceRecord` for the `ShowDistances` overlay.

## Phase 5: Pathfinding and NPCs

- [ ] Initialize `TerrainGraphIntegration` only on the server (`OnStartServer` path of `GameManager`). Disable the component on pure clients.
- [ ] Obstacle and height invalidation on the server after every build, destroy, collapse and terrain edit. Phases 2–4 cover these.
- [ ] `PathfindingAgent`:
  - Becomes a spawnable NetworkObject prefab with a server-authoritative `NetworkTransform`. Path requests and movement run only on the server.
  - `PlayerTransform` becomes "nearest player" on the server, for LOS approach and flee.
  - Patrol points and leader references are set at spawn, or become NetworkObject references.
- [ ] Add a GridCondition or DistanceCondition to agents so clients only receive nearby NPCs.

## Phase 6: Persistence and hardening

- [ ] Only the server runs `SaveAllBuildingAreas` and `LoadBuildingArea`. Loading spawns the `NetworkBuildingArea`s and fills `Pieces`, and late joiners receive them automatically. Save the terrain edit list alongside.
- [ ] Handle disconnects:
  - Unregister the player's streaming target.
  - Decide whether building and inventory persist per player.
- [ ] Latency testing: TransportManager latency simulator at 100–150 ms and 1–3% loss. Check build confirmation latency and collapse timing.
- [ ] Logging: a FishNet `LevelLoggingConfiguration`. Keep project logging on `IDebuggable`/`GameLogger`.
- [ ] Later / optional:
  - Authenticator (player names)
  - dedicated-server build profile (skip grass/detail/visual scatter on headless)
  - client-side prediction for movement

## Testing checklist per phase

- Host alone, host plus one client (Multiplayer Play Mode), and a client that joins late after building and terrain edits have happened.
- Both clients see the same trees, rocks and terrain height at the same coordinates.
- Pieces built by either player appear for both. Collapse plays on both. NPCs path around new walls.
- No errors from the `[Server]`/`[Client]` guards in the console on either side.
