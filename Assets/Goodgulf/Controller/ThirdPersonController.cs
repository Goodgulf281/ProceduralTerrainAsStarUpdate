// ============================================================
// Script:      ThirdPersonController.cs
// Episode:     EP## — Third Person Character Controller
// Description: Handles third-person movement, rotation, and
//              running using Unity's New Input System.
//              Pairs with OrbitCamera.cs.
// Author:      YOUR NAME
// Date:        2026-02-23
// ============================================================

using Goodgulf.Building;
using Goodgulf.Pathfinding;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goodgulf.Controller
{
    /// <summary>
    /// Third-person character controller supporting walk, run, and
    /// smooth camera-relative movement via the New Input System.
    /// Requires a CharacterController component on the same GameObject.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class ThirdPersonController : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────

        [Header("Movement")]
        [Tooltip("Walking speed in metres per second.")]
        [SerializeField] private float walkSpeed = 4f;

        [Tooltip("Running speed in metres per second.")]
        [SerializeField] private float runSpeed = 8f;

        [Tooltip("How quickly the character reaches target speed (higher = snappier).")]
        [SerializeField] private float acceleration = 10f;

        [Tooltip("How quickly the character decelerates when no input is given.")]
        [SerializeField] private float deceleration = 15f;

        [Header("Rotation")]
        [Tooltip("How fast the character rotates to face the movement direction.")]
        [SerializeField] private float rotationSpeed = 10f;

        [Header("Gravity & Jump")]
        [Tooltip("Downward gravity force applied each second.")]
        [SerializeField] private float gravity = -20f;

        [Tooltip("Jump height in metres.")]
        [SerializeField] private float jumpHeight = 1.5f;

        [Header("Ground Detection")]
        [Tooltip("Transform placed at the character's feet, used to check ground contact.")]
        [SerializeField] private Transform groundCheck;

        [Tooltip("Radius of the ground-check overlap sphere.")]
        [SerializeField] private float groundCheckRadius = 0.25f;

        [Tooltip("Layers considered as ground.")]
        [SerializeField] private LayerMask groundMask;

        [Header("References")]
        [Tooltip("The orbit camera used to determine movement direction.")]
        [SerializeField] private OrbitCamera orbitCamera;

        [Tooltip("The BuilderInput used to process the Build System's input.")]
        [SerializeField] private BuilderInput builderInput;
        
        
        // ── Input Actions (auto-wired via PlayerInput component) ─────

        // Called by PlayerInput — Move action
        private Vector2 _moveInput;

        // Called by PlayerInput — Run action (held)
        private bool _isRunning;

        // Called by PlayerInput — Jump action
        private bool _jumpPressed;

        // ── Private state ────────────────────────────────────────────

        private CharacterController _controller;
        private Animator _animator;

        private Vector3 _velocity;          // Horizontal velocity (world space)
        private float   _verticalVelocity;  // Vertical (gravity + jump)
        private bool    _isGrounded;

        // Animator parameter hashes (cached for performance)
        private static readonly int _animSpeed    = Animator.StringToHash("Speed");
        private static readonly int _animIsGrounded = Animator.StringToHash("IsGrounded");

        // ── Unity Lifecycle ──────────────────────────────────────────

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            // Animator is optional — controller works without it
            _animator   = GetComponent<Animator>();

            if (orbitCamera == null)
                Debug.LogWarning("[ThirdPersonController] OrbitCamera reference not set — movement will use world forward.");
        }

        private void Update()
        {
            CheckGround();
            HandleGravityAndJump();
            HandleMovement();
            UpdateAnimator();
        }

        // ── Input callbacks (wired via PlayerInput component) ────────

        /// <summary>Receives Move input from the New Input System.</summary>
        public void OnMove(InputValue value)
        {
            _moveInput = value.Get<Vector2>();
        }

        /// <summary>Receives Run input (held button) from the New Input System.</summary>
        public void OnRun(InputValue value)
        {
            _isRunning = value.isPressed;
        }

        /// <summary>Receives Jump input from the New Input System.</summary>
        public void OnJump(InputValue value)
        {
            if (value.isPressed)
                _jumpPressed = true;
        }

        public void OnBuild(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnBuild();
            }
        }

        public void OnDebugBuild(InputValue value)
        {
            if (value.isPressed)
            {
                if (builderInput)
                {
                    Vector3 position = builderInput.OnDebugBuild();
                    
                    TerrainGraphIntegration terrainGraphIntegration = TerrainGraphIntegration.Instance;
                    if (terrainGraphIntegration)
                    {
                        if (position != Vector3.zero)
                        {
                            Debug.LogWarning("<color=green>InvalidateObstacleCache</color>");
                            terrainGraphIntegration.InvalidateObstacleCache(position, 8.0f);
                        }
                    }
                    
                }
            }
        }

        
        public void OnOnlyRotate(InputValue value)
        {
            if (value.isPressed && builderInput)
            {
                builderInput.OnOnlyRotate(true);
            }
            else
            {
                builderInput.OnOnlyRotate(false);
            }
        }

        public void OnRotate(InputValue value)
        {
            if (builderInput)
            {
                float direction = value.Get<float>();
                builderInput.OnRotate(direction);
            }
        }

        public void OnModifyTerrainDown(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnModifyTerrainDown();
            }            
        }
        
        public void OnModifyTerrainUp(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnModifyTerrainUp();
            }            
        }
        
        public void OnSaveBuildings(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnSave();
            }            
        }
        
        public void OnLoadBuildings(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnLoad();
            }            
        }
        
        public void OnNextSnapPoint(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnNextSnapPoint();
            }            
        }

        public void OnToggleSnapToGrid(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnToggleSnapToGrid();
            }            
        }

        public void OnToggleSnapPoints(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnToggleSnapPoints();
            }            
        }

        public void OnToggleMagneticSnap(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnToggleMagneticPoints();
            }            
        }

        public void OnDestruct(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnDestruct();
            }            
        }

        public void OnFire(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnClick();
            }            
        }


        public void OnSelectBlock1(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnSelect(1);
            }            
        }

        public void OnSelectBlock2(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnSelect(2);
            }            
        }
        
        public void OnSelectBlock3(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnSelect(3);
            }            
        }

        public void OnSelectBlock0(InputValue value)
        {
            if (value.isPressed)
            {
                if(builderInput)
                    builderInput.OnSelect(0);
            }            
        }

        
        // ── Private helpers ──────────────────────────────────────────

        /// <summary>
        /// Uses an overlap sphere at the feet to determine if the character is grounded.
        /// </summary>
        private void CheckGround()
        {
            Vector3 checkOrigin = groundCheck != null
                ? groundCheck.position
                : transform.position + Vector3.down * (_controller.height * 0.5f);

            _isGrounded = Physics.CheckSphere(checkOrigin, groundCheckRadius, groundMask);

            // Prevent vertical velocity from accumulating while grounded
            if (_isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;
        }

        /// <summary>
        /// Applies gravity each frame and handles jump impulse.
        /// </summary>
        private void HandleGravityAndJump()
        {
            if (_jumpPressed && _isGrounded)
            {
                // v = sqrt(h * -2 * g)  —  classic jump formula
                _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }

            _jumpPressed = false;

            // Accumulate gravity
            _verticalVelocity += gravity * Time.deltaTime;
        }

        /// <summary>
        /// Calculates camera-relative horizontal movement and rotates the character.
        /// </summary>
        private void HandleMovement()
        {
            float targetSpeed = _moveInput.sqrMagnitude > 0.01f
                ? (_isRunning ? runSpeed : walkSpeed)
                : 0f;

            // Camera-relative input direction
            Vector3 inputDir = GetCameraRelativeInputDir();

            // Smooth horizontal speed
            float currentHorizontalSpeed = new Vector3(_velocity.x, 0f, _velocity.z).magnitude;
            float speedDelta = targetSpeed > currentHorizontalSpeed ? acceleration : deceleration;
            float smoothedSpeed = Mathf.MoveTowards(currentHorizontalSpeed, targetSpeed, speedDelta * Time.deltaTime);

            // Update horizontal velocity
            if (inputDir.sqrMagnitude > 0.01f)
            {
                _velocity = inputDir * smoothedSpeed;

                // Smoothly rotate the character to face movement direction
                Quaternion targetRotation = Quaternion.LookRotation(inputDir);
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    targetRotation,
                    rotationSpeed * Time.deltaTime
                );
            }
            else
            {
                // Decelerate to zero
                _velocity = Vector3.MoveTowards(_velocity, Vector3.zero, deceleration * Time.deltaTime);
            }

            // Combine horizontal and vertical movement, then move
            Vector3 motion = _velocity + Vector3.up * _verticalVelocity;
            _controller.Move(motion * Time.deltaTime);
        }

        /// <summary>
        /// Returns the movement direction in world space, relative to the camera's yaw.
        /// </summary>
        private Vector3 GetCameraRelativeInputDir()
        {
            if (_moveInput.sqrMagnitude < 0.01f)
                return Vector3.zero;

            // Use orbit camera yaw if available, otherwise fall back to world axes
            Transform camTransform = orbitCamera != null
                ? orbitCamera.transform
                : Camera.main?.transform;

            if (camTransform == null)
                return new Vector3(_moveInput.x, 0f, _moveInput.y).normalized;

            // Project camera forward and right onto the horizontal plane
            Vector3 camForward = Vector3.ProjectOnPlane(camTransform.forward, Vector3.up).normalized;
            Vector3 camRight   = Vector3.ProjectOnPlane(camTransform.right,   Vector3.up).normalized;

            return (camForward * _moveInput.y + camRight * _moveInput.x).normalized;
        }

        /// <summary>
        /// Passes locomotion data to the Animator if one is present.
        /// </summary>
        private void UpdateAnimator()
        {
            if (_animator == null) return;

            float normalizedSpeed = new Vector3(_velocity.x, 0f, _velocity.z).magnitude
                                    / runSpeed;

            _animator.SetFloat(_animSpeed,      normalizedSpeed, 0.1f, Time.deltaTime);
            _animator.SetBool (_animIsGrounded, _isGrounded);
        }

        public void TeleportCharacter(Vector3 newPosition)
        {
            _controller.enabled = false;
            transform.position = newPosition;
            _controller.enabled = true;
        }

        
        
        // ── Editor helpers ───────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            // Visualise the ground-check sphere in the Scene view
            if (groundCheck == null) return;

            Gizmos.color = _isGrounded ? Color.green : Color.red;
            Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius);
        }
    }
}
