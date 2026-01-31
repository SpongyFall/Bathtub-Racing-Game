/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using GONet;
using System.Collections.Generic;
using UnityEngine;

namespace GONet.Sample
{
    /// <summary>
    /// Test component for demonstrating the GONet reparenting system.
    ///
    /// When two physics cubes collide:
    /// - If neither has a parent and neither is a parent → one becomes child of the other
    /// - Constraint: One parent can have at most one child, one child can have at most one parent
    /// - The child is "bolted on" at the collision point
    /// - Visual feedback via colors:
    ///   - Parent: Blue
    ///   - Child: Yellow
    ///   - Default (no relationship): Original color (usually white)
    ///
    /// Server-side logic only (server owns all physics cubes).
    ///
    /// NOTE: This is a regular MonoBehaviour (not GONetParticipantCompanionBehaviour)
    /// to avoid lifecycle complexity when added at runtime. It manually references
    /// the GONetParticipant component on the same GameObject.
    /// </summary>
    [RequireComponent(typeof(GONetParticipant))]
    public class PhysicsCubeReparentOnCollision : MonoBehaviour
    {
        #region Detachment Blacklist (prevents immediate re-parenting after manual detach)

        /// <summary>
        /// Tracks pairs of GONetIds that were recently detached and should not reparent with each other.
        /// Key format: smaller GONetId, Value: set of larger GONetIds that it cannot reparent with.
        /// </summary>
        private static readonly Dictionary<uint, HashSet<uint>> detachmentBlacklist = new Dictionary<uint, HashSet<uint>>();

        /// <summary>
        /// Adds a pair to the blacklist so they won't reparent with each other.
        /// </summary>
        private static void AddToBlacklist(uint id1, uint id2)
        {
            // Always store with smaller ID as key for consistency
            uint smaller = id1 < id2 ? id1 : id2;
            uint larger = id1 < id2 ? id2 : id1;

            if (!detachmentBlacklist.TryGetValue(smaller, out var set))
            {
                set = new HashSet<uint>();
                detachmentBlacklist[smaller] = set;
            }
            set.Add(larger);
        }

        /// <summary>
        /// Checks if two cubes are blacklisted from reparenting with each other.
        /// </summary>
        private static bool IsBlacklisted(uint id1, uint id2)
        {
            uint smaller = id1 < id2 ? id1 : id2;
            uint larger = id1 < id2 ? id2 : id1;

            if (detachmentBlacklist.TryGetValue(smaller, out var set))
            {
                return set.Contains(larger);
            }
            return false;
        }

        /// <summary>
        /// Removes all blacklist entries involving this GONetId (called when object is destroyed).
        /// </summary>
        private static void RemoveFromBlacklist(uint gonetId)
        {
            // Remove as key
            detachmentBlacklist.Remove(gonetId);

            // Remove from all value sets
            foreach (var kvp in detachmentBlacklist)
            {
                kvp.Value.Remove(gonetId);
            }
        }

        #endregion

        #region State Tracking

        /// <summary>
        /// True if this cube is currently a parent (has a child attached).
        /// Detected from hierarchy so it works on both server and client.
        /// </summary>
        public bool IsParent
        {
            get
            {
                // Check if any child has PhysicsCubeReparentOnCollision
                foreach (Transform child in transform)
                {
                    if (child.GetComponent<PhysicsCubeReparentOnCollision>() != null)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// True if this cube is currently a child (attached to a parent physics cube).
        /// </summary>
        public bool IsChild => transform.parent != null && transform.parent.GetComponent<PhysicsCubeReparentOnCollision>() != null;

        /// <summary>
        /// Reference to our child cube (if we're a parent).
        /// </summary>
        private PhysicsCubeReparentOnCollision childCube;

        /// <summary>
        /// Cached GONetParticipant reference.
        /// </summary>
        private GONetParticipant gonetParticipant;

        /// <summary>
        /// Cached renderer for color changes.
        /// </summary>
        private Renderer cubeRenderer;

        /// <summary>
        /// Original material color (to restore when detached).
        /// </summary>
        private Color originalColor;

        /// <summary>
        /// Cached rigidbody reference.
        /// </summary>
        private Rigidbody rb;

        /// <summary>
        /// Cached collider reference.
        /// </summary>
        private Collider cubeCollider;

        /// <summary>
        /// True once we've completed initialization.
        /// </summary>
        private bool isInitialized;

        /// <summary>
        /// Tracks the previous parent so we can notify it when we're detached.
        /// </summary>
        private Transform previousParent;

        #endregion

        #region Color Constants

        private static readonly Color ParentColor = Color.blue;
        private static readonly Color ChildColor = Color.yellow;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            gonetParticipant = GetComponent<GONetParticipant>();
            cubeRenderer = GetComponent<Renderer>();
            rb = GetComponent<Rigidbody>();
            cubeCollider = GetComponent<Collider>();

            if (cubeRenderer != null && cubeRenderer.material != null)
            {
                originalColor = cubeRenderer.material.color;
            }
            else
            {
                originalColor = Color.white;
            }
        }

        private void Start()
        {
            // Update visual state based on current parent/child relationship
            UpdateVisualState();
            isInitialized = true;
        }

        #endregion

        #region Collision Handling (Server Only)

        private void OnCollisionEnter(Collision collision)
        {
            // Only server handles reparenting decisions
            if (!GONetMain.IsServer) return;

            // Only process if GONetParticipant is ready and we own this object
            if (gonetParticipant == null || gonetParticipant.GONetId == GONetParticipant.GONetId_Unset) return;
            if (!gonetParticipant.IsMine) return;

            // CRITICAL: Only reparent when colliding with another PhysicsCubeReparentOnCollision
            // Ignore collisions with walls, cannons, ground, etc.
            var otherCube = collision.gameObject.GetComponent<PhysicsCubeReparentOnCollision>();
            if (otherCube == null)
            {
                // Not a physics cube - ignore completely
                return;
            }

            // Other cube must also be initialized and owned by server
            if (otherCube.gonetParticipant == null || otherCube.gonetParticipant.GONetId == GONetParticipant.GONetId_Unset) return;
            if (!otherCube.gonetParticipant.IsMine) return;

            // Both cubes must be eligible for reparenting
            if (!CanParticipateInReparenting() || !otherCube.CanParticipateInReparenting())
            {
                return;
            }

            // Check if this pair was recently detached (blacklisted from reparenting)
            if (IsBlacklisted(gonetParticipant.GONetId, otherCube.gonetParticipant.GONetId))
            {
                return;
            }

            // Determine which cube becomes the parent (lower GONetId becomes parent for determinism)
            // This prevents race conditions when both objects detect the same collision
            bool weAreParent = gonetParticipant.GONetId < otherCube.gonetParticipant.GONetId;

            if (weAreParent)
            {
                // We become the parent, other becomes our child
                AttachChildAtCollisionPoint(otherCube, collision);
            }
            // else: The other cube will handle making us the child (when it processes OnCollisionEnter)
        }

        /// <summary>
        /// Checks if this cube can participate in a new reparenting relationship.
        /// </summary>
        private bool CanParticipateInReparenting()
        {
            // Cannot participate if we're already a parent (have a child)
            if (IsParent) return false;

            // Cannot participate if we're already a child (have a parent)
            if (IsChild) return false;

            return true;
        }

        /// <summary>
        /// Attaches the other cube as our child at the collision contact point.
        /// </summary>
        private void AttachChildAtCollisionPoint(PhysicsCubeReparentOnCollision other, Collision collision)
        {
            // Get collision contact point for bolt-on position
            ContactPoint contact = collision.GetContact(0);
            Vector3 worldContactPoint = contact.point;

            // Calculate direction from parent center to child center (more reliable than contact normal)
            Vector3 directionToChild = (other.transform.position - transform.position).normalized;

            // Position child's CENTER so its SURFACE is at the contact point (not overlapping)
            // Offset = contact point + (direction to child * half cube size) + small gap
            // The 0.5f accounts for cube being 1x1x1, the gap prevents physics overlap
            const float HALF_CUBE_SIZE = 0.5f;
            const float SEPARATION_GAP = 0.02f; // Small gap to ensure no overlap
            Vector3 childWorldCenter = worldContactPoint + (directionToChild * (HALF_CUBE_SIZE + SEPARATION_GAP));

            // Convert to local space relative to parent
            Vector3 localOffset = transform.InverseTransformPoint(childWorldCenter);

            // Before reparenting, record the child's current world rotation
            Quaternion childWorldRotation = other.transform.rotation;

            // NOTE: Physics state (isKinematic, useGravity, velocities) is now handled automatically
            // by GONet's OnTransformParentChanged when SetParent is called below.
            // GONet saves original state and restores it on detach.

            // Ignore collisions between parent and child to prevent self-collision
            // Child still collides with everything else in the world
            // NOTE: This is game-specific logic and is NOT handled automatically by GONet.
            if (cubeCollider != null && other.cubeCollider != null)
            {
                Physics.IgnoreCollision(cubeCollider, other.cubeCollider, true);
            }

            // Reparent the child cube to us
            // This triggers GONetParticipant.OnTransformParentChanged which:
            // - Publishes the reparent event
            // - Automatically handles physics state (kinematic, gravity, velocities)
            other.transform.SetParent(transform);

            // Position child so its surface is at collision point (not overlapping)
            other.transform.localPosition = localOffset;

            // Preserve world rotation as much as possible (convert to local rotation under new parent)
            other.transform.rotation = childWorldRotation;

            // Call FinalizeReparentOffset for immediate publish with exact offsets
            // (Otherwise auto-publishes after ReparentAutoPublishDelayFrames)
            other.gonetParticipant.FinalizeReparentOffset(localOffset, other.transform.localRotation);

            // Track child reference locally (for detachment support)
            // Note: IsParent is now computed from hierarchy, no need to set it
            childCube = other;

            // Update visual states
            UpdateVisualState();
            other.UpdateVisualState();
        }

        #endregion

        #region Visual Feedback

        /// <summary>
        /// Updates the cube's color based on its parent/child state.
        /// </summary>
        public void UpdateVisualState()
        {
            if (cubeRenderer == null || cubeRenderer.material == null) return;

            if (IsParent)
            {
                cubeRenderer.material.color = ParentColor;
            }
            else if (IsChild)
            {
                cubeRenderer.material.color = ChildColor;
            }
            else
            {
                cubeRenderer.material.color = originalColor;
            }
        }

        /// <summary>
        /// Called by Unity when transform parent changes.
        /// Updates visual state on all machines when reparent event is applied.
        /// Also notifies old and new parents to update their visual states.
        /// </summary>
        private void OnTransformParentChanged()
        {
            // Update visual state when parent changes (including from remote reparent events)
            if (isInitialized)
            {
                UpdateVisualState();

                // Notify the OLD parent (if it was a physics cube) to update its visual state
                // This handles detachment - old parent needs to revert to original color
                if (previousParent != null)
                {
                    var oldParentCube = previousParent.GetComponent<PhysicsCubeReparentOnCollision>();
                    if (oldParentCube != null)
                    {
                        oldParentCube.UpdateVisualState();
                    }
                }

                // Notify the NEW parent (if it's a physics cube) to update its visual state
                // This is the key fix - parent needs to turn blue when child attaches
                if (transform.parent != null)
                {
                    var newParentCube = transform.parent.GetComponent<PhysicsCubeReparentOnCollision>();
                    if (newParentCube != null)
                    {
                        newParentCube.UpdateVisualState();
                    }
                }
            }

            // Track current parent for next change
            previousParent = transform.parent;
        }

        #endregion

        #region Detachment (Optional - for future extension)

        /// <summary>
        /// Detaches our child cube (if any).
        /// Call this if you want to implement manual detachment logic.
        /// </summary>
        public void DetachChild()
        {
            if (!IsParent || childCube == null)
            {
                return;
            }

            // Only server can detach
            if (!GONetMain.IsServer || !gonetParticipant.IsMine)
            {
                return;
            }

            // Store reference before clearing
            var detachedChild = childCube;
            var detachedChildGNP = detachedChild.gonetParticipant;

            // Add this pair to blacklist so they won't immediately reparent when still colliding
            if (detachedChildGNP != null && gonetParticipant != null)
            {
                AddToBlacklist(gonetParticipant.GONetId, detachedChildGNP.GONetId);
            }

            // NOTE: Physics state (isKinematic, useGravity) is now handled automatically
            // by GONet's OnTransformParentChanged when SetParent(null) is called below.
            // GONet restores original physics state that was saved during attach.

            // Restore collision between parent and child
            // NOTE: This is game-specific logic and is NOT handled automatically by GONet.
            if (cubeCollider != null && detachedChild.cubeCollider != null)
            {
                Physics.IgnoreCollision(cubeCollider, detachedChild.cubeCollider, false);
            }

            // Reparent child back to world root
            // This triggers GONetParticipant.OnTransformParentChanged which:
            // - Publishes the reparent event
            // - Automatically restores physics state (kinematic, gravity)
            // - Automatically calls ResumeTransformSync if needed
            detachedChild.transform.SetParent(null);

            // Clear tracking
            // Note: IsParent is now computed from hierarchy, clears automatically when child is detached
            childCube = null;

            // Update visuals
            UpdateVisualState();
            detachedChild.UpdateVisualState();
        }

        #endregion

        #region Cleanup

        private void OnDestroy()
        {
            // Clean up blacklist entries when this object is destroyed
            // This allows other cubes to potentially reparent with new cubes that get the same GONetId
            if (gonetParticipant != null && gonetParticipant.GONetId != GONetParticipant.GONetId_Unset)
            {
                RemoveFromBlacklist(gonetParticipant.GONetId);
            }
        }

        #endregion
    }
}
