// ============================================================
// Script:      OrbitCamera.cs
// Description: Smooth orbit camera that follows a target with
//              configurable pitch/yaw limits, zoom, and
//              collision avoidance. Driven by the New Input System.
// Date:        2026-02-23
// ============================================================

using UnityEngine;
using UnityEngine.InputSystem;

namespace Goodgulf.Controller
{
    /// <summary>
    /// Third-person orbit camera that rotates around a follow target,
    /// clamps pitch, and pulls in when geometry is in the way.
    /// Attach to the Camera GameObject; assign the player as <see cref="target"/>.
    /// </summary>
    public class OrbitCamera : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────

        [Header("Target")]
        [Tooltip("The Transform the camera orbits around (usually the player).")]
        [SerializeField] private Transform target;

        [Tooltip("Local-space offset added to the target position (raise it to look at chest/head).")]
        [SerializeField] private Vector3 targetOffset = new Vector3(0f, 1.6f, 0f);

        [Header("Distance")]
        [Tooltip("Default distance from the target.")]
        [SerializeField] private float defaultDistance = 5f;

        [Tooltip("Minimum allowed zoom distance.")]
        [SerializeField] private float minDistance = 1.5f;

        [Tooltip("Maximum allowed zoom distance.")]
        [SerializeField] private float maxDistance = 12f;

        [Tooltip("Mouse-scroll zoom sensitivity.")]
        [SerializeField] private float zoomSpeed = 3f;

        [Tooltip("Smooth factor for zoom changes.")]
        [SerializeField] private float zoomSmoothing = 8f;

        [Header("Rotation Sensitivity")]
        [Tooltip("Mouse / stick look sensitivity (degrees per unit of input).")]
        [SerializeField] private float lookSensitivity = 200f;

        [Tooltip("Multiplier applied when using a gamepad stick.")]
        [SerializeField] private float gamepadLookMultiplier = 3f;

        [Header("Pitch Clamp")]
        [Tooltip("Minimum pitch (looking down, degrees).")]
        [SerializeField] private float minPitch = -30f;

        [Tooltip("Maximum pitch (looking up, degrees).")]
        [SerializeField] private float maxPitch = 70f;

        [Header("Smoothing")]
        [Tooltip("How quickly the camera follows position changes (higher = tighter).")]
        [SerializeField] private float followSmoothing = 10f;

        [Tooltip("How quickly the camera rotates to the desired orientation.")]
        [SerializeField] private float rotationSmoothing = 15f;

        [Header("Collision")]
        [Tooltip("Layers the camera should not clip through.")]
        [SerializeField] private LayerMask collisionMask;

        [Tooltip("Small buffer so the camera doesn't graze surfaces.")]
        [SerializeField] private float collisionBuffer = 0.3f;

        [Header("Cursor")]
        [Tooltip("Lock and hide the cursor while playing.")]
        [SerializeField] private bool lockCursorOnPlay = true;

        // ── Private state ────────────────────────────────────────────

        private float _yaw;                 // Horizontal rotation angle
        private float _pitch;               // Vertical rotation angle
        private float _currentDistance;     // Actual rendered distance (after collision)
        private float _targetDistance;      // Desired distance before collision

        private Vector3 _currentFollowPos;  // Smoothed pivot position

        // Raw look input this frame (from New Input System)
        private Vector2 _lookInput;
        private float   _zoomInput;

        // ── Unity Lifecycle ──────────────────────────────────────────

        private void Awake()
        {
            // Initialise from current camera orientation so there is no snap on start
            Vector3 angles = transform.eulerAngles;
            _yaw   = angles.y;
            _pitch = angles.x;

            _targetDistance  = defaultDistance;
            _currentDistance = defaultDistance;

            if (target != null)
                _currentFollowPos = target.position + targetOffset;

            if (lockCursorOnPlay)
                SetCursorLocked(true);
        }

        private void LateUpdate()
        {
            if (target == null) return;

            HandleZoom();
            ApplyLookInput();
            UpdateFollowPosition();
            ApplyTransform();
        }

        // ── Input callbacks (wired via PlayerInput component) ────────

        /// <summary>Receives Look input (mouse delta or right stick) from the New Input System.</summary>
        public void OnLook(InputValue value)
        {
            _lookInput = value.Get<Vector2>();
        }

        /// <summary>Receives Zoom input (scroll wheel or triggers) from the New Input System.</summary>
        public void OnZoom(InputValue value)
        {
            _zoomInput = value.Get<float>();
        }

        /// <summary>Toggles cursor lock (e.g. bound to Escape) from the New Input System.</summary>
        public void OnToggleCursor(InputValue value)
        {
            if (value.isPressed)
                SetCursorLocked(Cursor.lockState != CursorLockMode.Locked);
        }

        // ── Private helpers ──────────────────────────────────────────

        /// <summary>
        /// Adjusts the desired zoom distance based on scroll / trigger input.
        /// </summary>
        private void HandleZoom()
        {
            if (Mathf.Abs(_zoomInput) > 0.01f)
            {
                _targetDistance -= _zoomInput * zoomSpeed * Time.deltaTime;
                _targetDistance  = Mathf.Clamp(_targetDistance, minDistance, maxDistance);
            }

            // Smooth zoom towards desired distance
            _currentDistance = Mathf.Lerp(_currentDistance, _targetDistance, zoomSmoothing * Time.deltaTime);
        }

        /// <summary>
        /// Accumulates look input into yaw and pitch, clamping pitch within allowed range.
        /// Detects mouse vs. gamepad input and scales accordingly.
        /// </summary>
        private void ApplyLookInput()
        {
            if (_lookInput.sqrMagnitude < 0.001f) return;

            // Heuristic: mouse deltas are typically large pixel values; stick input is -1..1
            bool isGamepad = Mathf.Abs(_lookInput.x) <= 1f && Mathf.Abs(_lookInput.y) <= 1f;
            float multiplier = isGamepad
                ? gamepadLookMultiplier * Time.deltaTime
                : Time.deltaTime;

            _yaw   += _lookInput.x * lookSensitivity * multiplier;
            _pitch -= _lookInput.y * lookSensitivity * multiplier;   // inverted Y for natural feel
            _pitch  = Mathf.Clamp(_pitch, minPitch, maxPitch);
        }

        /// <summary>
        /// Smoothly moves the pivot position to follow the target, accounting for the offset.
        /// </summary>
        private void UpdateFollowPosition()
        {
            Vector3 desiredFollowPos = target.position + targetOffset;
            _currentFollowPos = Vector3.Lerp(
                _currentFollowPos,
                desiredFollowPos,
                followSmoothing * Time.deltaTime
            );
        }

        /// <summary>
        /// Positions and rotates the camera, then pulls it in if geometry is blocking the view.
        /// </summary>
        private void ApplyTransform()
        {
            Quaternion desiredRotation = Quaternion.Euler(_pitch, _yaw, 0f);

            // Desired camera position (pivot + rotation applied backwards along Z)
            Vector3 desiredPosition = _currentFollowPos - desiredRotation * Vector3.forward * _currentDistance;

            // Collision: shorten distance if something is between pivot and camera
            float safeDistance = GetSafeDistance(desiredRotation);
            Vector3 finalPosition = _currentFollowPos - desiredRotation * Vector3.forward * safeDistance;

            // Smooth rotation
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                desiredRotation,
                rotationSmoothing * Time.deltaTime
            );

            transform.position = finalPosition;
        }

        /// <summary>
        /// Casts a sphere from the pivot to the desired camera position and returns
        /// the maximum safe distance that avoids geometry.
        /// </summary>
        private float GetSafeDistance(Quaternion rotation)
        {
            Vector3 direction = -(rotation * Vector3.forward);
            float   radius    = collisionBuffer;

            if (Physics.SphereCast(
                    _currentFollowPos,
                    radius,
                    direction,
                    out RaycastHit hit,
                    _currentDistance,
                    collisionMask, QueryTriggerInteraction.Ignore))
            {
                // Pull camera in so it sits just outside the hit surface
                return Mathf.Max(minDistance, hit.distance - collisionBuffer);
            }

            return _currentDistance;
        }

        /// <summary>
        /// Locks or unlocks the system cursor.
        /// </summary>
        private static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible   = !locked;
        }

        // ── Editor helpers ───────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            if (target == null) return;

            // Draw the pivot point
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(target.position + targetOffset, 0.15f);

            // Draw a line from pivot to camera
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(target.position + targetOffset, transform.position);
        }
    }
}
