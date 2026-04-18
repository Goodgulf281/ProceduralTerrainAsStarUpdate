using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Goodgulf.Logging;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Goodgulf.Pathfinding
{
    // =========================================================================
    // Enums
    // =========================================================================

    public enum AgentMode
    {
        Idle,
        MoveTo,
        Patrol,
        FollowLeader,
    }

    public enum LOSBehavior
    {
        None,
        Approach,
        Flee,
    }

    // =========================================================================
    // PathfindingAgent
    // =========================================================================

    /// <summary>
    /// A fully self-contained agent that moves across procedurally generated
    /// terrain using the custom A* pathfinding system.
    ///
    /// Features
    /// ────────
    ///  • Move-to-target
    ///  • Waypoint patrol with optional Scene-view gizmo display
    ///  • Line-of-sight approach / flee vs. the Player
    ///  • Leader / Follower flocking (Reynolds-lite: stay within a radius)
    ///
    /// The agent does NOT use Unity's NavMesh — all movement is driven by
    /// following the path produced by <see cref="AStarSearch"/>.
    /// </summary>
    [SelectionBase]
    public class PathfindingAgent : MonoBehaviour, IDebuggable
    {
        // =====================================================================
        // Inspector — Movement
        // =====================================================================

        [Header("Movement")]
        [Tooltip("Maximum movement speed (world units / second).")]
        public float MoveSpeed = 5f;

        [Tooltip("Angular speed for rotation toward movement direction (deg/s).")]
        public float RotationSpeed = 360f;

        [Tooltip("Distance from a waypoint at which it is considered 'reached'.")]
        public float WaypointReachDistance = 0.5f;

        [Tooltip("How high above the current node to hover (Y offset applied after terrain sampling).")]
        public float HeightOffset = 0.1f;


        [Tooltip("Layer mask for terrain height raycasting. Must include your Terrain layer.")]
        public LayerMask TerrainLayerMask = ~0;

        // =====================================================================
        // Inspector — Mode
        // =====================================================================

        [Header("Behaviour Mode")]
        public AgentMode StartingMode = AgentMode.Idle;

        // ---- Move-to ----
        [Header("Move To")]
        [Tooltip("Target position for MoveToTarget mode.")]
        public Transform MoveTarget;

        // ---- Patrol ----
        [Header("Patrol")]
        [Tooltip("World-space patrol waypoints.")]
        public List<Transform> PatrolPoints = new();

        [Tooltip("If true, patrol reverses at the end (ping-pong). If false, it loops.")]
        public bool PatrolPingPong = false;

        // ---- Line-of-Sight ----
        [Header("Line of Sight")]
        public LOSBehavior LOSBehaviour = LOSBehavior.None;

        [Tooltip("Reference to the player transform for LOS detection.")]
        public Transform PlayerTransform;

        [Tooltip("Distance within which the LOS check activates.")]
        public float LOSActivationDistance = 20f;

        [Tooltip("LayerMask used for LOS raycast occlusion check.")]
        public LayerMask LOSOcclusionMask;

        [Tooltip("For Approach: how close to stop when approaching the player.")]
        public float ApproachStopDistance = 3f;

        [Tooltip("For Flee: minimum distance to maintain from the player.")]
        public float FleeDistance = 15f;

        // ---- Leader / Follower ----
        [Header("Flocking")]
        [Tooltip("Mark this agent as a leader. Leaders pick their own targets.")]
        public bool IsLeader = false;

        [Tooltip("The leader to follow (only used when IsLeader = false).")]
        public PathfindingAgent Leader;

        [Tooltip("Followers try to stay within this radius of the leader.")]
        public float FlockRadius = 5f;

        [Tooltip("Minimum spacing between followers.")]
        public float FollowerSeparation = 1.5f;

        // =====================================================================
        // Runtime state (public-read for debugging / editor tools)
        // =====================================================================

        [Header("Debug (read-only)")]
        [SerializeField] private AgentMode  _currentMode = AgentMode.Idle;
        [SerializeField] private LOSBehavior _activeLOS = LOSBehavior.None;
        [SerializeField] private bool        _pathPending;
        [SerializeField] private int         _waypointIndex;

    
        [SerializeField, Tooltip("Enable verbose logging for this component")]
        private bool _debugEnabled = true;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;


        // =====================================================================
        // Private
        // =====================================================================

        private List<Vector3> _currentPath  = new();
        private int           _pathIndex    = 0;

        private int   _patrolIndex     = 0;
        private int   _patrolDirection = 1;  // +1 forward, -1 backward (ping-pong)

        private bool  _losActive       = false;
        private float _pathCooldown    = 0f;
        private const float PathCooldownTime = 0.5f; // re-path at most every N seconds

        // Flocking offset from leader — computed once when follower attaches
        private Vector3 _flockOffset = Vector3.zero;

        // =====================================================================
        // Unity lifecycle
        // =====================================================================

        private void Start()
        {
            _currentMode = StartingMode;

            if (!IsLeader && Leader != null)
                ComputeFlockOffset();
        }

        private void Update()
        {
            _pathCooldown -= Time.deltaTime;

            // Priority: LOS overrides other modes
            if (EvaluateLOS())
            {
                HandleLOSMovement();
                return;
            }

            switch (_currentMode)
            {
                case AgentMode.Idle:
                    break;

                case AgentMode.MoveTo:
                    HandleMoveTo();
                    break;

                case AgentMode.Patrol:
                    HandlePatrol();
                    break;

                case AgentMode.FollowLeader:
                    HandleFollowLeader();
                    break;
            }

            FollowCurrentPath();
        }

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>Move to a specific world position.</summary>
        public void MoveTo(Vector3 worldPosition)
        {
            this.LogVerbose($"MoveTo called with target {worldPosition}");

            _currentMode = AgentMode.MoveTo;
            if (MoveTarget == null)
            {
                var go = new GameObject("AgentMoveTarget");
                MoveTarget = go.transform;
            }
            MoveTarget.position = worldPosition;
            RequestPath(worldPosition);
        }

        /// <summary>Begin patrolling the current <see cref="PatrolPoints"/>.</summary>
        public void StartPatrol()
        {
            _currentMode    = AgentMode.Patrol;
            _patrolIndex    = 0;
            _patrolDirection = 1;
            if (PatrolPoints.Count > 0)
                RequestPath(PatrolPoints[0].position);
        }

        /// <summary>Begin following <see cref="Leader"/>.</summary>
        public void StartFollowing(PathfindingAgent leader)
        {
            Leader       = leader;
            IsLeader     = false;
            _currentMode = AgentMode.FollowLeader;
            ComputeFlockOffset();
        }

        /// <summary>Stop all movement and return to Idle.</summary>
        public void Stop()
        {
            _currentMode  = AgentMode.Idle;
            _currentPath.Clear();
            _pathIndex = 0;
        }

        // =====================================================================
        // Path management
        // =====================================================================

        private void RequestPath(Vector3 destination)
        {
            if (_pathCooldown > 0f) return;
            if (TerrainGraphIntegration.Instance == null) return;

            // Debug.Log($"RequestPath called from: {new System.Diagnostics.StackTrace()}");

            this.LogVerbose($"Requesting path to {destination}");

            _pathCooldown = PathCooldownTime;
            _pathPending  = true;

            PathResult result = TerrainGraphIntegration.Instance.RequestPath(
                transform.position, destination);

            _pathPending = false;

            if (result.Status == PathStatus.Success && result.Waypoints != null)
            {
                this.LogVerbose($"Path found with {result.Waypoints.Count} waypoints");
                _currentPath = result.Waypoints;
                _pathIndex   = 0;
            }
            else
            {
                this.LogVerbose($"Path request failed (status={result.Status}) or returned no waypoints");
                _currentPath.Clear();
            }
        }

        private void FollowCurrentPath()
        {
            if (_currentPath == null || _pathIndex >= _currentPath.Count) return;

            Vector3 target = _currentPath[_pathIndex];
            Vector3 flat   = new Vector3(target.x, transform.position.y, target.z);
            float   dist   = Vector3.Distance(
                new Vector3(transform.position.x, 0, transform.position.z),
                new Vector3(target.x,             0, target.z));

            if (dist <= WaypointReachDistance)
            {
                _pathIndex++;
                return;
            }

            // Move toward waypoint
            Vector3 dir    = (flat - transform.position).normalized;
            Vector3 move   = dir * (MoveSpeed * Time.deltaTime);
            transform.position = new Vector3(
                transform.position.x + move.x,
                GetTerrainY() + HeightOffset,
                transform.position.z + move.z);

            // Rotate toward movement direction
            if (dir.sqrMagnitude > 0.01f)
            {
                Quaternion targetRot = Quaternion.LookRotation(dir);
                transform.rotation   = Quaternion.RotateTowards(
                    transform.rotation, targetRot, RotationSpeed * Time.deltaTime);
            }
        }

        // =====================================================================
        // Mode handlers
        // =====================================================================

        private void HandleMoveTo()
        {
            if (MoveTarget == null) return;

            // Only re-path if we have no path, the path is exhausted,
            // or we've drifted far from the expected trajectory
            bool needsPath = _currentPath.Count == 0
                        || _pathIndex >= _currentPath.Count;

            if (needsPath && _pathCooldown <= 0f)
                RequestPath(MoveTarget.position);
        }

        private void HandlePatrol()
        {
            if (PatrolPoints.Count == 0) return;

            // Check if we've reached the current patrol point
            if (_currentPath.Count > 0 && _pathIndex >= _currentPath.Count)
            {
                AdvancePatrolIndex();
                if (_patrolIndex >= 0 && _patrolIndex < PatrolPoints.Count)
                    RequestPath(PatrolPoints[_patrolIndex].position);
            }
            else if (_currentPath.Count == 0)
            {
                RequestPath(PatrolPoints[_patrolIndex].position);
            }
        }

        private void AdvancePatrolIndex()
        {
            if (PatrolPingPong)
            {
                _patrolIndex += _patrolDirection;
                if (_patrolIndex >= PatrolPoints.Count - 1 || _patrolIndex <= 0)
                    _patrolDirection *= -1;
                _patrolIndex = Mathf.Clamp(_patrolIndex, 0, PatrolPoints.Count - 1);
            }
            else
            {
                _patrolIndex = (_patrolIndex + 1) % PatrolPoints.Count;
            }
        }

        private void HandleFollowLeader()
        {
            if (Leader == null || !Leader.isActiveAndEnabled) return;

            // Compute offset lazily if not yet done (handles late Leader assignment)
            if (_flockOffset == Vector3.zero)
                ComputeFlockOffset();

            Vector3 desiredPos    = Leader.transform.position + _flockOffset;
            float   distToDesired = Vector3.Distance(transform.position, desiredPos);

            if (distToDesired > FlockRadius * 0.5f)
            {
                bool pathExhausted = _currentPath.Count == 0
                                || _pathIndex >= _currentPath.Count;
                if (pathExhausted && _pathCooldown <= 0f)
                    RequestPath(desiredPos);
            }

            ApplyFollowerSeparation();
        }

        private void ComputeFlockOffset()
        {
            if (Leader == null) return;

            // Give each follower a deterministic slot around the leader
            // based on instance ID (avoids all followers stacking at origin)
            int    id    = GetInstanceID();
            float  angle = (id % 8) * (360f / 8f) * Mathf.Deg2Rad;
            float  r     = FlockRadius * 0.6f;

            _flockOffset = new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
        }

        private void ApplyFollowerSeparation()
        {
            // Push away from nearby agents
            Collider[] nearby = Physics.OverlapSphere(transform.position, FollowerSeparation);
            foreach (var col in nearby)
            {
                if (col.gameObject == gameObject) continue;
                PathfindingAgent other = col.GetComponent<PathfindingAgent>();
                if (other == null) continue;

                Vector3 away = transform.position - other.transform.position;
                if (away.sqrMagnitude < 0.0001f) away = Random.insideUnitSphere;
                away.y = 0;
                transform.position += away.normalized * (FollowerSeparation * 0.1f);
            }
        }

        // =====================================================================
        // Line-of-Sight
        // =====================================================================

        private bool EvaluateLOS()
        {
            // LOS should not interrupt explicit move-to commands
            if (_currentMode == AgentMode.MoveTo || _currentMode == AgentMode.FollowLeader) return false;

            _activeLOS = LOSBehavior.None;

            if (LOSBehaviour == LOSBehavior.None || PlayerTransform == null)
                return false;

            float dist = Vector3.Distance(transform.position, PlayerTransform.position);
            if (dist > LOSActivationDistance) return false;

            Vector3 toPlayer = PlayerTransform.position - transform.position;
            bool lineOfSight = !Physics.Raycast(
                transform.position + Vector3.up * 0.5f,
                toPlayer.normalized,
                dist,
                LOSOcclusionMask,
                QueryTriggerInteraction.Ignore);

            if (!lineOfSight) return false;

            _losActive = true;
            _activeLOS = LOSBehaviour;
            return true;
        }

        private void HandleLOSMovement()
        {
            if (PlayerTransform == null) return;
            float dist = Vector3.Distance(transform.position, PlayerTransform.position);

            if (_activeLOS == LOSBehavior.Approach)
            {
                if (dist > ApproachStopDistance)
                {
                    bool pathExhausted = _currentPath.Count == 0
                                    || _pathIndex >= _currentPath.Count;
                    if (pathExhausted && _pathCooldown <= 0f)
                        RequestPath(PlayerTransform.position);
                }
            }
            else if (_activeLOS == LOSBehavior.Flee)
            {
                if (dist < FleeDistance)
                {
                    bool pathExhausted = _currentPath.Count == 0
                                    || _pathIndex >= _currentPath.Count;
                    if (pathExhausted && _pathCooldown <= 0f)
                    {
                        Vector3 fleeDir = (transform.position - PlayerTransform.position).normalized;
                        Vector3 fleePos = transform.position + fleeDir * FleeDistance;
                        RequestPath(fleePos);
                    }
                }
            }

            FollowCurrentPath();
        }
        // =====================================================================
        // Terrain height sampling
        // =====================================================================

        private float GetTerrainY()
        {
            Vector3 origin = new Vector3(
                transform.position.x,
                transform.position.y + 100f,
                transform.position.z);

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 500f, TerrainLayerMask))
                return hit.point.y;

            // Fall back to the graph's sampled height for this position
            if (TerrainGraphIntegration.Instance != null &&
                TerrainGraphIntegration.Instance.Graph != null)
            {
                NodeData nd = TerrainGraphIntegration.Instance.Graph.GetNode(
                    transform.position.x,
                    transform.position.z);

                if (nd.WorldY > 0f)
                    return nd.WorldY;
            }

            return transform.position.y;
        }

        // =====================================================================
        // Scene view gizmos
        // =====================================================================

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            DrawPatrolGizmos();
            DrawPathGizmos();
            DrawLOSGizmos();
            DrawFlockGizmos();
        }

        private void DrawPatrolGizmos()
        {
            if (_currentMode != AgentMode.Patrol && StartingMode != AgentMode.Patrol) return;
            if (PatrolPoints == null || PatrolPoints.Count == 0) return;

            Handles.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            Gizmos.color  = new Color(0.2f, 0.8f, 1f, 0.9f);

            for (int i = 0; i < PatrolPoints.Count; i++)
            {
                if (PatrolPoints[i] == null) continue;

                Vector3 pos = PatrolPoints[i].position;
                Gizmos.DrawSphere(pos, 0.4f);
                Handles.Label(pos + Vector3.up * 0.8f, $"P{i}");

                // Draw connection lines
                int next = (i + 1) % PatrolPoints.Count;
                if (!PatrolPingPong || i < PatrolPoints.Count - 1)
                {
                    if (PatrolPoints[next] != null)
                    {
                        Gizmos.DrawLine(pos, PatrolPoints[next].position);
                    }
                }
            }

            // Highlight current patrol target
            if (_patrolIndex < PatrolPoints.Count && PatrolPoints[_patrolIndex] != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(PatrolPoints[_patrolIndex].position, 0.6f);
            }
        }

        private void DrawPathGizmos()
        {
            if (_currentPath == null || _currentPath.Count == 0) return;

            for (int i = 0; i < _currentPath.Count; i++)
            {
                Vector3 wp = _currentPath[i];

                // Reached waypoints: grey
                // Current target: bright red sphere
                // Future waypoints: orange
                bool isReached = i < _pathIndex;
                bool isCurrent = i == _pathIndex;

                if (isReached)
                    Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.4f);
                else if (isCurrent)
                    Gizmos.color = Color.red;
                else
                    Gizmos.color = new Color(1f, 0.5f, 0f, 0.9f);

                float radius = isCurrent ? 1.0f : 0.5f;
                Gizmos.DrawSphere(wp, radius);

                // Label each waypoint with index and height
        #if UNITY_EDITOR
                UnityEditor.Handles.color = Color.white;
                UnityEditor.Handles.Label(
                    wp + Vector3.up * (radius + 0.5f),
                    $"[{i}] Y={wp.y:F1}");
        #endif

                // Draw line segment to next waypoint
                if (i < _currentPath.Count - 1)
                {
                    Gizmos.color = isReached
                        ? new Color(0.5f, 0.5f, 0.5f, 0.3f)
                        : new Color(1f, 0.5f, 0f, 0.8f);
                    Gizmos.DrawLine(wp, _currentPath[i + 1]);
                }
            }

            // Draw a vertical line from agent position down to the current waypoint's XZ
            if (_pathIndex < _currentPath.Count)
            {
                Vector3 wp  = _currentPath[_pathIndex];
                Vector3 agentFlat = new Vector3(transform.position.x, wp.y, transform.position.z);
                Gizmos.color = new Color(1f, 1f, 0f, 0.6f);
                Gizmos.DrawLine(transform.position, agentFlat);
                Gizmos.DrawLine(agentFlat, wp);

                // Show XZ reach distance ring at waypoint height
                DrawWireCircleXZ(wp, WaypointReachDistance);
            }
        }

        private void DrawWireCircleXZ(Vector3 centre, float radius)
        {
            int   segments = 16;
            float step     = 360f / segments * Mathf.Deg2Rad;
            for (int i = 0; i < segments; i++)
            {
                float a1 = i * step;
                float a2 = (i + 1) * step;
                Gizmos.DrawLine(
                    centre + new Vector3(Mathf.Cos(a1) * radius, 0, Mathf.Sin(a1) * radius),
                    centre + new Vector3(Mathf.Cos(a2) * radius, 0, Mathf.Sin(a2) * radius));
            }
        }

        private void DrawLOSGizmos()
        {
            if (LOSBehaviour == LOSBehavior.None || PlayerTransform == null) return;

            Gizmos.color = _losActive
                ? new Color(1f, 0.1f, 0.1f, 0.4f)
                : new Color(0.8f, 0.8f, 0f, 0.2f);
            Gizmos.DrawWireSphere(transform.position, LOSActivationDistance);

            if (_losActive && LOSBehaviour == LOSBehavior.Flee)
            {
                Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
                Gizmos.DrawWireSphere(transform.position, FleeDistance);
            }
            if (_losActive && LOSBehaviour == LOSBehavior.Approach)
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.5f);
                Gizmos.DrawWireSphere(transform.position, ApproachStopDistance);
            }
        }

        private void DrawFlockGizmos()
        {
            if (IsLeader)
            {
                Gizmos.color = new Color(1f, 0.85f, 0f, 0.3f);
                Gizmos.DrawWireSphere(transform.position, FlockRadius);
                Handles.Label(transform.position + Vector3.up * 2f, "LEADER");
            }
            else if (Leader != null && _currentMode == AgentMode.FollowLeader)
            {
                Gizmos.color = new Color(0.5f, 0.5f, 1f, 0.5f);
                Gizmos.DrawLine(transform.position, Leader.transform.position);
            }
        }
#endif
    }

    // =========================================================================
    // Custom Editor for patrol point list management
    // =========================================================================

#if UNITY_EDITOR
    [CustomEditor(typeof(PathfindingAgent))]
    public class PathfindingAgentEditor : Editor
    {
        private PathfindingAgent _agent;

        private void OnEnable()
        {
            _agent = (PathfindingAgent)target;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime Controls", EditorStyles.boldLabel);

            if (!Application.isPlaying) return;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Start Patrol"))  _agent.StartPatrol();
            if (GUILayout.Button("Stop"))          _agent.Stop();
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Move to MoveTarget"))
            {
                if (_agent.MoveTarget != null)
                    _agent.MoveTo(_agent.MoveTarget.position);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Graph Integration", EditorStyles.boldLabel);
            if (GUILayout.Button("Force Full Graph Rebuild"))
            {
                if (TerrainGraphIntegration.Instance != null)
                    TerrainGraphIntegration.Instance.ForceFullRebuildSync();
                else
                    Debug.LogWarning("No TerrainGraphIntegration instance found.");
            }

            // Dirty-region helper: draw a 10-unit AABB around agent
            if (GUILayout.Button("Dirty Region Around Agent (10u radius)"))
            {
                if (TerrainGraphIntegration.Instance != null)
                    TerrainGraphIntegration.Instance.RequestDirtyRegionRebuild(
                        _agent.transform.position, 10f);
            }
        }
    }
#endif
}
