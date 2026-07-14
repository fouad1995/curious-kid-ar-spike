// =============================================================================
// ARFeasibilitySpikeController.cs
// Curious Kid — AR Reveal Mechanism Feasibility Spike
// 2026-07-13
//
// PURPOSE OF THIS FILE:
//   This is a throwaway proof-of-concept. It is NOT production code. It does
//   NOT follow the Engine/Template/Story architecture. It does NOT respect the
//   detection-independence or Tool-implementation-independence rules — those
//   are production rules that explicitly do not apply here.
//
//   This file tests four mechanics in isolation to answer the 5 questions in
//   RD_Spike_Brief_AR_Feasibility.md before any production architecture is
//   committed to.
//
// FOUR MECHANICS UNDER TEST:
//   A. Ground plane detection via AR Foundation ARPlaneManager
//   B. Random-position placement within a configurable radius on a detected plane
//   C. Stationary vs. moving detection using camera pose delta (NOT pedometer)
//   D. Proximity detection: did the device get close enough to the placed object?
//
// TARGET: iOS (ARKit via AR Foundation), Unity 6.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;

/// <summary>
/// Single MonoBehaviour that orchestrates all four feasibility mechanics.
/// Attach to one GameObject in the scene. Everything is hard-coded or exposed
/// via serialized fields — no abstractions, no patterns, no production concerns.
/// </summary>
public class ARFeasibilitySpikeController : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // MECHANIC A: Ground plane detection
    // -------------------------------------------------------------------------
    // What we're testing: Does ARPlaneManager reliably find horizontal planes
    // (floor/ground) in a typical home environment? How quickly? How stable?
    //
    // ARPlaneManager tracks detected planes. We visualize the first large enough
    // horizontal plane we find so the tester can confirm detection is working.
    // -------------------------------------------------------------------------

    [Header("Mechanic A — Plane Detection")]
    [Tooltip("AR Plane Manager from the AR Session Origin / XR Origin. Assign in Inspector.")]
    public ARPlaneManager planeManager;

    [Tooltip("Minimum plane area in square metres to be considered usable. " +
             "ARKit can detect small planes quickly but they may be unreliable. " +
             "0.5 sqm (~70x70cm) is a reasonable floor for indoor use.")]
    [SerializeField] private float minPlaneArea = 0.5f;

    // The 'best' plane we've decided to use for placement.
    // We pick the largest horizontal plane that meets minPlaneArea.
    private ARPlane _activePlane = null;

    // -------------------------------------------------------------------------
    // MECHANIC B: Random-position placement within a radius
    // -------------------------------------------------------------------------
    // What we're testing: Can we place a virtual object at a random XZ position
    // on a detected plane, within a real-world radius of the device?
    //
    // "5–10 steps" ~ 3.5m–7m for an adult. For indoor testing, 1.5–3m is a
    // better starting range where we can confirm the math before scaling up.
    // The spec says "roughly 5-10 steps or any reasonable steps" — the radius
    // is an implementation parameter, not a game design constant.
    // -------------------------------------------------------------------------

    [Header("Mechanic B — Random Placement")]
    [Tooltip("Min distance from device to place the target object (metres). " +
             "1.5m is a good indoor minimum — anything closer feels wrong.")]
    [SerializeField] private float placementRadiusMin = 1.5f;

    [Tooltip("Max distance from device to place the target object (metres). " +
             "3m fits most indoor rooms. Scale to 7m for outdoor/hallway tests.")]
    [SerializeField] private float placementRadiusMax = 3.0f;

    [Tooltip("The sphere primitive that will be placed. Create a Sphere in the " +
             "scene, set it inactive, assign it here. We clone it at placement time.")]
    public GameObject targetPrefab;

    // The currently placed instance of the target object.
    private GameObject _placedTarget = null;

    // Whether a target has been placed and is awaiting proximity detection.
    private bool _targetIsActive = false;

    // -------------------------------------------------------------------------
    // MECHANIC C: Stationary vs. moving detection + movement threshold
    // -------------------------------------------------------------------------
    // What we're testing: Can we reliably distinguish a stationary child from a
    // moving one using the ARSession camera pose delta (position change per frame)?
    //
    // IMPORTANT — why camera pose delta, NOT the device pedometer/step counter:
    //   - The spec says "the system counts movement." A step counter is intuitive
    //     but it requires HealthKit permission on iOS (user-visible permission
    //     dialog, privacy concern) and is not available cross-platform in AR Foundation.
    //   - The AR camera pose is already computed by ARKit as part of tracking.
    //     Its position in world space is reliable to centimetre accuracy in good
    //     tracking conditions. Summing frame-to-frame displacement is a valid
    //     proxy for "distance walked."
    //   - Risk: if ARKit tracking quality drops (poor lighting, featureless walls),
    //     the pose may drift or jump — producing false movement readings. This is
    //     exactly what the spike is testing.
    //
    // STATIONARY threshold: if the camera moved less than stationaryThreshold
    // metres in a single frame, we consider the player stationary for that frame.
    // This filters out normal hand tremor and minor swaying.
    //
    // MOVEMENT accumulator: sum of per-frame displacements above the stationary
    // threshold. When this sum exceeds the current reveal threshold, we trigger
    // mechanic B. Accumulator resets after each reveal.
    // -------------------------------------------------------------------------

    [Header("Mechanic C — Movement Detection")]
    [Tooltip("Per-frame position delta below this value (metres) is treated as " +
             "stationary. Hand tremor is typically < 2cm/frame. 0.02m is a good " +
             "starting point; lower it if walking registers as stationary.")]
    [SerializeField] private float stationaryThreshold = 0.02f;

    [Tooltip("Minimum movement distance to accumulate before a reveal triggers (metres). " +
             "~1.5m corresponds to roughly 2-3 steps. Tune upward for longer searches.")]
    [SerializeField] private float revealThresholdMin = 1.5f;

    [Tooltip("Maximum movement distance threshold (metres). " +
             "~4m corresponds to roughly 5-6 steps. The actual threshold per reveal " +
             "is randomized between min and max so the child can't predict it.")]
    [SerializeField] private float revealThresholdMax = 4.0f;

    [Tooltip("Fallback timeout in seconds. If a reveal has not triggered after this " +
             "many seconds (e.g. tracking is failing), force a reveal anyway. " +
             "This is an engineering safety net — not part of the game experience.")]
    [SerializeField] private float fallbackRevealTimeout = 30.0f;

    // Internal state for mechanic C
    private bool _playerIsMoving = false;        // current frame's movement assessment
    private float _accumulatedDistance = 0f;     // running sum toward reveal threshold
    private float _currentRevealThreshold = 0f;  // randomized threshold for this cycle
    private float _timeSinceLastReveal = 0f;     // counts toward fallback timeout

    // The camera transform (grabbed from AR camera at Start).
    // We use this to compute world-space position delta each frame.
    private Transform _cameraTransform = null;
    private Vector3 _lastCameraPosition = Vector3.zero;
    private bool _lastPositionInitialized = false;

    // -------------------------------------------------------------------------
    // MECHANIC D: Proximity detection
    // -------------------------------------------------------------------------
    // What we're testing: Once a virtual object is placed, can we reliably detect
    // that the device (and thus the child holding it) is "very close" to that
    // object's world position?
    //
    // We compare the XZ distance (horizontal only — ignore Y/height because the
    // phone is held at varying heights and the placed object is on the floor).
    // When within proximityDistance, we fire a "found" event.
    // -------------------------------------------------------------------------

    [Header("Mechanic D — Proximity Detection")]
    [Tooltip("Distance in metres within which the target is considered 'found'. " +
             "0.5m (arm's length) is a tight threshold that requires the child " +
             "to walk close. 1.0m is more forgiving for young children. " +
             "Test both to find what feels correct.")]
    [SerializeField] private float proximityDistance = 0.7f;

    // Whether the 'found' event has already fired for the current target
    // (prevents re-triggering every frame while the child stands in range).
    private bool _targetFound = false;

    // Materials for visual feedback on found state
    [Header("Visual Feedback")]
    [Tooltip("Color of the sphere before it is 'found'. Assign a bright, obvious color.")]
    [SerializeField] private Color targetNormalColor = Color.yellow;

    [Tooltip("Color the sphere changes to when found. Should be visually distinct.")]
    [SerializeField] private Color targetFoundColor = Color.green;

    // -------------------------------------------------------------------------
    // DEBUG UI — populated by DebugUI.cs reading these public properties
    // -------------------------------------------------------------------------

    // Public read-only properties for DebugUI.cs to display each frame.
    public string CurrentState => _playerIsMoving ? "MOVING" : "STATIONARY";
    public float AccumulatedDistance => _accumulatedDistance;
    public float RevealThreshold => _currentRevealThreshold;
    public float DistanceToTarget => _targetIsActive && _placedTarget != null
        ? HorizontalDistance(_cameraTransform.position, _placedTarget.transform.position)
        : -1f;
    public int PlaneCount => planeManager != null
        ? CountUsablePlanes()
        : 0;
    public bool TargetPlaced => _targetIsActive;
    public bool TargetFoundState => _targetFound;
    public float FallbackTimer => _timeSinceLastReveal;
    public float FallbackTimeout => fallbackRevealTimeout;

    // =========================================================================
    // UNITY LIFECYCLE
    // =========================================================================

    private void Start()
    {
        // Grab the AR camera. In AR Foundation (Unity 6), the XR Origin has a
        // Camera Offset > Main Camera hierarchy. Camera.main works if the camera
        // is tagged "MainCamera", which is the default AR Foundation setup.
        Camera arCamera = Camera.main;
        if (arCamera == null)
        {
            Debug.LogError("[ARSpike] No main camera found. Make sure the AR camera " +
                           "is tagged 'MainCamera' in your AR session hierarchy.");
            return;
        }
        _cameraTransform = arCamera.transform;

        // Subscribe to plane events so we can log when new planes appear.
        // AR Foundation fires these events as ARKit updates its plane mesh.
        if (planeManager != null)
        {
            planeManager.trackablesChanged.AddListener(OnPlanesChanged);
        }
        else
        {
            Debug.LogError("[ARSpike] ARPlaneManager not assigned. Assign it in the Inspector.");
        }

        // Pick an initial random reveal threshold for mechanic C.
        PickNewRevealThreshold();

        Debug.Log("[ARSpike] Spike initialized. Move around to accumulate distance toward reveal.");
    }

    private void Update()
    {
        // Guard: don't do anything until the camera transform is ready.
        if (_cameraTransform == null) return;

        // MECHANIC A + B: Find the best usable plane each frame.
        // (Plane positions update as ARKit refines its understanding of the space.)
        UpdateActivePlane();

        // MECHANIC C: Detect movement, accumulate distance, check for reveal.
        // Only run if we don't currently have an active (un-found) target.
        if (!_targetIsActive)
        {
            UpdateMovementAndCheckReveal();
        }

        // MECHANIC D: Check proximity to the placed target.
        if (_targetIsActive && !_targetFound)
        {
            CheckProximity();
        }

        // Store the camera position for the next frame's delta calculation.
        _lastCameraPosition = _cameraTransform.position;
        _lastPositionInitialized = true;
    }

    private void OnDestroy()
    {
        if (planeManager != null)
            planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
    }

    // =========================================================================
    // MECHANIC A — PLANE DETECTION
    // =========================================================================

    /// <summary>
    /// Called by AR Foundation whenever tracked planes are added, updated, or removed.
    /// We log this so the tester can see in the device console when detection happens.
    /// </summary>
    private void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> changes)
    {
        // Log new planes for debugging — this tells us how quickly ARKit finds planes
        // and how many it finds in a typical home environment.
        foreach (var plane in changes.added)
        {
            Debug.Log($"[ARSpike] Plane ADDED: id={plane.trackableId}, " +
                      $"alignment={plane.alignment}, " +
                      $"size={plane.size.x:F2}x{plane.size.y:F2}m, " +
                      $"area={PlaneArea(plane):F2}sqm");
        }

        foreach (var plane in changes.updated)
        {
            // Only log updates for the active plane to avoid log spam.
            if (_activePlane != null && plane.trackableId == _activePlane.trackableId)
            {
                Debug.Log($"[ARSpike] Active plane UPDATED: " +
                          $"size={plane.size.x:F2}x{plane.size.y:F2}m");
            }
        }

        foreach (var plane in changes.removed)
        {
            Debug.Log($"[ARSpike] Plane REMOVED: id={plane.Key}");
            // If our active plane was removed, clear it so we re-select next frame.
            if (_activePlane != null && plane.Key == _activePlane.trackableId)
            {
                _activePlane = null;
            }
        }
    }

    /// <summary>
    /// Each frame, pick the largest usable horizontal plane as our active plane.
    /// "Usable" means: horizontal alignment (floor), area >= minPlaneArea.
    ///
    /// WHY largest: ARKit often detects multiple small plane patches before merging
    /// them. Using the largest gives us the most stable and accurately positioned
    /// plane to place objects on.
    /// </summary>
    private void UpdateActivePlane()
    {
        float bestArea = 0f;
        ARPlane bestPlane = null;

        foreach (var plane in planeManager.trackables)
        {
            // We only want horizontal planes facing upward (floors, tables, ground).
            // PlaneAlignment.HorizontalUp is the ARKit classification for floor-like surfaces.
            if (plane.alignment != PlaneAlignment.HorizontalUp) continue;

            float area = PlaneArea(plane);
            if (area >= minPlaneArea && area > bestArea)
            {
                bestArea = area;
                bestPlane = plane;
            }
        }

        _activePlane = bestPlane;
    }

    private float PlaneArea(ARPlane plane)
    {
        // ARPlane.size is a Vector2 in (width, height) of the plane's bounding rectangle.
        return plane.size.x * plane.size.y;
    }

    private int CountUsablePlanes()
    {
        int count = 0;
        foreach (var plane in planeManager.trackables)
        {
            if (plane.alignment == PlaneAlignment.HorizontalUp && PlaneArea(plane) >= minPlaneArea)
                count++;
        }
        return count;
    }

    // =========================================================================
    // MECHANIC C — MOVEMENT DETECTION + REVEAL TRIGGER
    // =========================================================================

    /// <summary>
    /// Computes per-frame camera position delta to assess movement.
    /// Accumulates toward a randomized reveal threshold.
    /// Fires a reveal (mechanic B) when threshold is crossed or fallback timer expires.
    /// </summary>
    private void UpdateMovementAndCheckReveal()
    {
        if (!_lastPositionInitialized)
        {
            // First frame — no delta available yet.
            _lastCameraPosition = _cameraTransform.position;
            _lastPositionInitialized = true;
            return;
        }

        // Compute how far the camera moved this frame in world space.
        // We use 3D distance here (not just XZ) because the camera also bobs
        // vertically as someone walks. In practice, vertical movement from walking
        // is small compared to horizontal, so this is a reasonable approximation.
        float frameDelta = Vector3.Distance(_cameraTransform.position, _lastCameraPosition);

        // STATIONARY vs MOVING classification for this frame.
        // If the delta is below the stationary threshold, the player is considered
        // stationary — this filters out hand tremor (typically < 1cm/frame).
        _playerIsMoving = (frameDelta >= stationaryThreshold);

        if (_playerIsMoving)
        {
            // Accumulate distance only while moving.
            _accumulatedDistance += frameDelta;
            _timeSinceLastReveal += Time.deltaTime;

            // CHECK: Has the player moved enough to trigger a reveal?
            if (_accumulatedDistance >= _currentRevealThreshold)
            {
                Debug.Log($"[ARSpike] Movement threshold reached! " +
                          $"Accumulated: {_accumulatedDistance:F2}m, " +
                          $"Threshold was: {_currentRevealThreshold:F2}m. " +
                          $"Triggering reveal.");
                TriggerReveal("movement_threshold");
                return;
            }
        }
        else
        {
            // Still count the fallback timer even when stationary —
            // we want the fallback to fire if the child simply stops moving.
            _timeSinceLastReveal += Time.deltaTime;
        }

        // FALLBACK: If too much time has passed without a reveal, force one.
        // This is an engineering safety net for the case where tracking fails
        // (e.g. poor lighting, featureless surface, tracking lost).
        if (_timeSinceLastReveal >= fallbackRevealTimeout)
        {
            Debug.LogWarning($"[ARSpike] Fallback timeout reached ({fallbackRevealTimeout}s). " +
                             $"Forcing reveal. Accumulated distance was: {_accumulatedDistance:F2}m. " +
                             $"If this fires often, check ARKit tracking quality.");
            TriggerReveal("fallback_timeout");
        }
    }

    private void PickNewRevealThreshold()
    {
        // Randomize the threshold so the child can't predict exactly how far to walk.
        _currentRevealThreshold = Random.Range(revealThresholdMin, revealThresholdMax);
        Debug.Log($"[ARSpike] New reveal threshold set: {_currentRevealThreshold:F2}m " +
                  $"(range: {revealThresholdMin}-{revealThresholdMax}m)");
    }

    // =========================================================================
    // MECHANIC B — RANDOM PLACEMENT ON DETECTED PLANE
    // =========================================================================

    /// <summary>
    /// Places a target object at a random position on the active plane, within
    /// the configured radius from the device's current world position.
    ///
    /// PLACEMENT STRATEGY:
    ///   1. Pick a random angle and distance within [placementRadiusMin, placementRadiusMax].
    ///   2. Compute the candidate world XZ position.
    ///   3. Project it onto the active plane's Y position (the plane's center Y).
    ///   4. Place the sphere there.
    ///
    /// LIMITATION of this approach (important to note for the report):
    ///   We use the plane's Y position as the floor height. We do NOT raycast against
    ///   the plane mesh to verify the candidate XZ position actually falls within the
    ///   plane's detected boundary. In small rooms, the plane may not extend to
    ///   placementRadiusMax. A production implementation would need to handle this —
    ///   either by raycasting or by clamping the radius to the plane's extent.
    ///   For this spike, we're testing whether the basic positioning math works.
    /// </summary>
    private void PlaceTarget()
    {
        if (_activePlane == null)
        {
            Debug.LogWarning("[ARSpike] Cannot place target: no usable plane detected yet. " +
                             "Walk around more to help ARKit find the floor.");
            return;
        }

        if (targetPrefab == null)
        {
            Debug.LogError("[ARSpike] targetPrefab not assigned in Inspector. Cannot place target.");
            return;
        }

        // Clean up any previously placed target.
        if (_placedTarget != null)
        {
            Destroy(_placedTarget);
        }

        // Pick a random direction in the XZ plane (horizontal).
        float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
        float distance = Random.Range(placementRadiusMin, placementRadiusMax);

        // Compute the position relative to the camera's current world position,
        // then snap to the plane's Y (floor height).
        Vector3 cameraPos = _cameraTransform.position;
        float planeY = _activePlane.transform.position.y;

        Vector3 spawnPosition = new Vector3(
            cameraPos.x + Mathf.Cos(angle) * distance,
            planeY,          // Floor height from the detected plane
            cameraPos.z + Mathf.Sin(angle) * distance
        );

        // Instantiate the target object.
        _placedTarget = Instantiate(targetPrefab, spawnPosition, Quaternion.identity);
        _placedTarget.SetActive(true);

        // Set initial color.
        SetTargetColor(targetNormalColor);

        _targetIsActive = true;
        _targetFound = false;

        Debug.Log($"[ARSpike] Target placed at {spawnPosition}, " +
                  $"distance from camera: {distance:F2}m, " +
                  $"angle: {angle * Mathf.Rad2Deg:F1} degrees, " +
                  $"plane Y: {planeY:F2}m");
    }

    private void TriggerReveal(string reason)
    {
        Debug.Log($"[ARSpike] Reveal triggered (reason: {reason})");

        // Reset accumulator and pick a new threshold for the next cycle.
        _accumulatedDistance = 0f;
        _timeSinceLastReveal = 0f;
        PickNewRevealThreshold();

        // Place the target (mechanic B).
        PlaceTarget();
    }

    // =========================================================================
    // MECHANIC D — PROXIMITY DETECTION
    // =========================================================================

    /// <summary>
    /// Checks each frame whether the device is within proximityDistance of the
    /// placed target. Uses horizontal (XZ) distance only.
    ///
    /// WHY XZ ONLY:
    ///   The placed target is on the floor. The phone is held at chest height
    ///   (roughly 1.2–1.5m above the floor). A 3D distance check would almost
    ///   always read > 1m even when the child is standing directly over the object.
    ///   XZ distance isolates the horizontal proximity we actually care about.
    /// </summary>
    private void CheckProximity()
    {
        if (_placedTarget == null) return;

        float dist = HorizontalDistance(_cameraTransform.position, _placedTarget.transform.position);

        if (dist <= proximityDistance)
        {
            OnTargetFound(dist);
        }
    }

    private void OnTargetFound(float distanceAtFind)
    {
        _targetFound = true;
        _targetIsActive = false;  // Stop checking proximity.

        // Visual feedback: change sphere color to green.
        SetTargetColor(targetFoundColor);

        Debug.Log($"[ARSpike] TARGET FOUND! XZ distance at find: {distanceAtFind:F2}m " +
                  $"(threshold: {proximityDistance:F2}m). " +
                  $"The 'found' event would fire here in production.");

        // In a production implementation, this is where the Engine would be notified
        // that the Target was found — e.g. a C# event, a callback, or an interface call.
        // For this spike, we just change the color and log.
        //
        // After a brief pause, we'd restart the cycle. For the spike, we don't
        // auto-restart — the tester manually re-runs by moving away and back.
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Horizontal (XZ) distance between two world-space positions.
    /// Ignores Y (height) differences — used for proximity checks.
    /// </summary>
    private float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private void SetTargetColor(Color color)
    {
        if (_placedTarget == null) return;
        Renderer rend = _placedTarget.GetComponent<Renderer>();
        if (rend != null)
        {
            // Create a new material instance to avoid modifying the shared material.
            rend.material = new Material(rend.sharedMaterial);
            rend.material.color = color;
        }
    }
}
