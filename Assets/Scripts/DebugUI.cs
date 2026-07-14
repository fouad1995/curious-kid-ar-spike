// =============================================================================
// DebugUI.cs
// Curious Kid — AR Reveal Mechanism Feasibility Spike
// 2026-07-13
//
// PURPOSE:
//   On-screen debug overlay. Reads state from ARFeasibilitySpikeController and
//   updates TextMeshPro labels every frame. Lets the tester see what the system
//   is computing in real-time on device, without needing to read Xcode console logs.
//
// This is throwaway code — no abstractions, no production patterns.
// =============================================================================

using UnityEngine;
using TMPro;

/// <summary>
/// Updates on-screen debug text every frame by reading from the spike controller.
/// Attach to a UI Canvas GameObject. Assign all TMP labels in the Inspector.
///
/// WHAT EACH LABEL SHOWS (and why it matters for the spike):
///
///   STATE:       "STATIONARY" or "MOVING" — validates mechanic C is correctly
///               classifying the player's movement state.
///
///   DISTANCE:    Metres accumulated toward the reveal threshold — lets the tester
///               confirm the accumulator is growing at the right rate while walking.
///
///   THRESHOLD:   The randomized reveal target — confirms randomization is working
///               and gives the tester context for how far they need to walk.
///
///   TARGET DIST: XZ distance to the placed target — critical for validating that
///               proximity detection (mechanic D) fires at the right distance.
///
///   PLANES:      Count of usable horizontal planes — validates mechanic A;
///               should increment as ARKit detects more of the floor.
///
///   STATUS:      High-level state machine label showing what phase we're in.
///
///   FALLBACK:    Elapsed time vs. fallback timeout — lets the tester verify the
///               safety net is counting correctly.
/// </summary>
public class DebugUI : MonoBehaviour
{
    [Header("Reference to spike controller")]
    [Tooltip("Assign the GameObject that has ARFeasibilitySpikeController on it.")]
    public ARFeasibilitySpikeController controller;

    [Header("TMP Labels — assign in Inspector")]
    [Tooltip("Displays STATIONARY or MOVING")]
    public TMP_Text stateLabel;

    [Tooltip("Displays accumulated movement distance in metres")]
    public TMP_Text accumulatedDistanceLabel;

    [Tooltip("Displays the current randomized reveal threshold in metres")]
    public TMP_Text revealThresholdLabel;

    [Tooltip("Displays XZ distance to placed target, or 'No target' if none placed")]
    public TMP_Text distanceToTargetLabel;

    [Tooltip("Displays count of usable detected planes")]
    public TMP_Text planeCountLabel;

    [Tooltip("High-level status: Searching / Target Placed / Found")]
    public TMP_Text statusLabel;

    [Tooltip("Displays elapsed time vs fallback timeout")]
    public TMP_Text fallbackTimerLabel;

    // =========================================================================
    // UNITY LIFECYCLE
    // =========================================================================

    private void Start()
    {
        if (controller == null)
        {
            Debug.LogError("[DebugUI] ARFeasibilitySpikeController not assigned. " +
                           "Assign it in the Inspector.");
        }
    }

    private void Update()
    {
        if (controller == null) return;

        // --- STATE label (Mechanic C) ---
        // Color-codes the text so it's visually obvious at a glance.
        if (stateLabel != null)
        {
            stateLabel.text = $"STATE: {controller.CurrentState}";
            stateLabel.color = controller.CurrentState == "MOVING"
                ? Color.green
                : Color.yellow;
        }

        // --- ACCUMULATED DISTANCE label (Mechanic C) ---
        if (accumulatedDistanceLabel != null)
        {
            accumulatedDistanceLabel.text =
                $"MOVED: {controller.AccumulatedDistance:F2}m";
        }

        // --- REVEAL THRESHOLD label (Mechanic C) ---
        if (revealThresholdLabel != null)
        {
            revealThresholdLabel.text =
                $"THRESHOLD: {controller.RevealThreshold:F2}m";
        }

        // --- DISTANCE TO TARGET label (Mechanic D) ---
        if (distanceToTargetLabel != null)
        {
            float dist = controller.DistanceToTarget;
            if (dist < 0f)
            {
                distanceToTargetLabel.text = "TARGET DIST: ---";
                distanceToTargetLabel.color = Color.white;
            }
            else
            {
                distanceToTargetLabel.text = $"TARGET DIST: {dist:F2}m";
                // Turn red when very close — visual cue that proximity is about to trigger.
                distanceToTargetLabel.color = dist < 1.0f ? Color.red : Color.white;
            }
        }

        // --- PLANE COUNT label (Mechanic A) ---
        if (planeCountLabel != null)
        {
            int planes = controller.PlaneCount;
            planeCountLabel.text = $"PLANES: {planes}";
            // Turn green once at least one usable plane is found.
            planeCountLabel.color = planes > 0 ? Color.green : Color.red;
        }

        // --- STATUS label (high-level phase) ---
        if (statusLabel != null)
        {
            if (controller.TargetFoundState)
            {
                statusLabel.text = "STATUS: FOUND";
                statusLabel.color = Color.green;
            }
            else if (controller.TargetPlaced)
            {
                statusLabel.text = "STATUS: TARGET PLACED";
                statusLabel.color = Color.cyan;
            }
            else
            {
                statusLabel.text = "STATUS: SEARCHING";
                statusLabel.color = Color.white;
            }
        }

        // --- FALLBACK TIMER label ---
        // Shows how far through the fallback timeout we are.
        if (fallbackTimerLabel != null)
        {
            float elapsed = controller.FallbackTimer;
            float timeout = controller.FallbackTimeout;
            fallbackTimerLabel.text = $"FALLBACK: {elapsed:F1}s / {timeout:F0}s";
            // Turn orange in the last 10 seconds before fallback fires.
            fallbackTimerLabel.color = (timeout - elapsed) < 10f ? Color.red : Color.gray;
        }
    }
}
