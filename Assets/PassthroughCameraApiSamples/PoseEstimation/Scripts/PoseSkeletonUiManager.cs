// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace PassthroughCameraSamples.PoseEstimation
{
    [MetaCodeSample("PassthroughCameraApiSamples-PoseEstimation")]
    public class PoseSkeletonUiManager : MonoBehaviour
    {
        [Header("Placement configuration")]
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Skeleton Rendering")]
        [SerializeField] private GameObject m_jointPrefab;  // Small sphere for joints
        [SerializeField] private LineRenderer m_bonePrefab; // Line for bones
        [SerializeField] private float m_jointSize = 0.03f;
        [SerializeField] private float m_boneWidth = 0.015f;

        [Header("Joint Colors")]
        [SerializeField] private Color m_headColor = Color.yellow;
        [SerializeField] private Color m_torsoColor = Color.blue;
        [SerializeField] private Color m_armsColor = Color.green;
        [SerializeField] private Color m_legsColor = Color.red;
        [SerializeField] private Color m_boneColor = Color.white;

        private readonly List<SkeletonInstance> m_activeSkeletons = new();
        private readonly List<GameObject> m_jointPool = new();
        private readonly List<LineRenderer> m_bonePool = new();

        // COCO 17 keypoint skeleton connections
        private static readonly int[][] BONE_CONNECTIONS = new int[][]
        {
            // Head
            new int[] {0, 1}, new int[] {0, 2}, new int[] {1, 3}, new int[] {2, 4},
            // Shoulders
            new int[] {5, 6},
            // Arms
            new int[] {5, 7}, new int[] {7, 9}, new int[] {6, 8}, new int[] {8, 10},
            // Torso
            new int[] {5, 11}, new int[] {6, 12}, new int[] {11, 12},
            // Legs
            new int[] {11, 13}, new int[] {13, 15}, new int[] {12, 14}, new int[] {14, 16}
        };

        internal class SkeletonInstance
        {
            public List<GameObject> Joints = new();
            public List<LineRenderer> Bones = new();
            public float LastUpdateTime;
        }

        private void Start()
        {
            Debug.Log("[POSE UI] PoseSkeletonUiManager started");

            // Reference checks
            Debug.Log($"[POSE REF] environmentRaycast={m_environmentRaycast != null}");
            Debug.Log($"[POSE REF] cameraAccess={m_cameraAccess != null}");
            Debug.Log($"[POSE REF] jointPrefab={m_jointPrefab != null}");
            Debug.Log($"[POSE REF] bonePrefab={m_bonePrefab != null}");
        }

        private void Update()
        {
            // Remove skeletons that haven't been updated recently
            for (int i = m_activeSkeletons.Count - 1; i >= 0; i--)
            {
                var skeleton = m_activeSkeletons[i];
                const float timeToPersist = 3f;
                if (Time.time - skeleton.LastUpdateTime > timeToPersist)
                {
                    ReturnSkeletonToPool(skeleton);
                    m_activeSkeletons.RemoveAt(i);
                }
            }
        }

        public void DrawPoseSkeletons(
            PoseInferenceRunManager.PersonSkeleton[] people,
            Pose cameraPose,
            float minScore)
        {
            Debug.Log($"[POSE DRAW] DrawSkeleton called, persons={people?.Length ?? 0}");

            Vector2 currentResolution = m_cameraAccess.CurrentResolution;

            if (people == null || people.Length == 0)
            {
                ClearSkeletons();
                return;
            }

            Debug.Log($"[POSE UI] Drawing {people.Length} person skeleton(s)");

            // Clear old skeletons
            foreach (var skel in m_activeSkeletons)
            {
                ReturnSkeletonToPool(skel);
            }
            m_activeSkeletons.Clear();

            // Draw each person's skeleton
            for (int personIdx = 0; personIdx < people.Length; personIdx++)
            {
                var person = people[personIdx];
                if (person.keypoints == null || person.keypoints.Count == 0)
                {
                    Debug.LogWarning($"[POSE UI] Person {personIdx} has no keypoints");
                    continue;
                }

                var skeleton = new SkeletonInstance();
                skeleton.LastUpdateTime = Time.time;

                // Convert keypoints to world positions
                Vector3[] worldPositions = new Vector3[person.keypoints.Count];
                bool[] isValid = new bool[person.keypoints.Count];

                for (int i = 0; i < person.keypoints.Count; i++)
                {
                    var kp = person.keypoints[i];

                    // Log first 5 keypoints in detail
                    if (i < 5)
                    {
                        Debug.Log($"[POSE DRAW] kp={kp.name} pos=({kp.x:F3},{kp.y:F3}) score={kp.score:F2}");
                    }

                    if (kp.score < minScore)
                    {
                        isValid[i] = false;
                        Debug.Log($"[POSE DRAW] Keypoint {i} ({kp.name}) skipped: score {kp.score:F2} < min {minScore:F2}");
                        continue;
                    }

                    // Convert normalized coordinates to viewport
                    Vector2 normalizedPos = new Vector2(kp.x, 1.0f - kp.y);

                    // Create ray from camera
                    var ray = m_cameraAccess.ViewportPointToRay(normalizedPos, cameraPose);

                    // Raycast to environment to get world position
                    var worldPos = m_environmentRaycast.Raycast(ray);

                    if (worldPos.HasValue)
                    {
                        worldPositions[i] = worldPos.Value;
                        isValid[i] = true;

                        if (i < 5)
                        {
                            Debug.Log($"[POSE DRAW] Keypoint {i} ({kp.name}) world pos: {worldPos.Value}");
                        }

                        // Create joint sphere - NULL SAFE
                        GameObject joint;
                        if (m_jointPrefab != null)
                        {
                            joint = Instantiate(m_jointPrefab, transform);
                            Debug.Log($"[POSE UI] Created joint from prefab");
                        }
                        else
                        {
                            Debug.Log($"[POSE UI] FALLBACK: Creating primitive sphere for joint");
                            joint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                            joint.transform.SetParent(transform);

                            // Set material with bright color
                            var mat = new Material(Shader.Find("Unlit/Color"));
                            mat.color = Color.yellow;
                            joint.GetComponent<Renderer>().material = mat;
                        }

                        joint.transform.position = worldPos.Value;
                        joint.transform.localScale = Vector3.one * m_jointSize;
                        joint.SetActive(true);

                        // Set color based on body part (only if using prefab)
                        if (m_jointPrefab != null)
                        {
                            var renderer = joint.GetComponent<Renderer>();
                            if (renderer != null)
                            {
                                renderer.material.color = GetJointColor(i);
                            }
                        }

                        skeleton.Joints.Add(joint);
                    }
                    else
                    {
                        isValid[i] = false;
                        Debug.LogWarning($"[POSE UI] Raycast failed for keypoint {i} ({kp.name})");
                    }
                }

                // Draw bones (lines connecting joints)
                foreach (var connection in BONE_CONNECTIONS)
                {
                    int idx1 = connection[0];
                    int idx2 = connection[1];

                    // Only draw bone if both endpoints are valid
                    if (isValid[idx1] && isValid[idx2])
                    {
                        // Create bone LineRenderer - NULL SAFE
                        LineRenderer bone;
                        if (m_bonePrefab != null)
                        {
                            bone = Instantiate(m_bonePrefab, transform);
                            Debug.Log($"[POSE UI] Created bone from prefab");
                        }
                        else
                        {
                            Debug.Log($"[POSE UI] FALLBACK: Creating LineRenderer for bone");
                            GameObject boneGO = new GameObject("Bone");
                            boneGO.transform.SetParent(transform);
                            bone = boneGO.AddComponent<LineRenderer>();

                            // Set material with bright color
                            var mat = new Material(Shader.Find("Unlit/Color"));
                            mat.color = Color.cyan;
                            bone.material = mat;

                            bone.startWidth = 0.015f;
                            bone.endWidth = 0.015f;
                        }

                        bone.positionCount = 2;
                        bone.SetPosition(0, worldPositions[idx1]);
                        bone.SetPosition(1, worldPositions[idx2]);
                        bone.startWidth = m_boneWidth;
                        bone.endWidth = m_boneWidth;
                        bone.startColor = m_boneColor;
                        bone.endColor = m_boneColor;
                        bone.gameObject.SetActive(true);

                        skeleton.Bones.Add(bone);
                    }
                }

                m_activeSkeletons.Add(skeleton);
                Debug.Log($"[POSE UI] Person {personIdx}: Drew {skeleton.Joints.Count} joints, {skeleton.Bones.Count} bones");
            }
        }

        public void ClearSkeletons()
        {
            foreach (var skeleton in m_activeSkeletons)
            {
                ReturnSkeletonToPool(skeleton);
            }
            m_activeSkeletons.Clear();
        }

        private Color GetJointColor(int jointIndex)
        {
            // Head (0-4): nose, left_eye, right_eye, left_ear, right_ear
            if (jointIndex <= 4) return m_headColor;

            // Torso (5-6, 11-12): shoulders, hips
            if ((jointIndex >= 5 && jointIndex <= 6) || (jointIndex >= 11 && jointIndex <= 12))
                return m_torsoColor;

            // Arms (7-10): elbows, wrists
            if (jointIndex >= 7 && jointIndex <= 10) return m_armsColor;

            // Legs (13-16): knees, ankles
            return m_legsColor;
        }

        private GameObject GetJointFromPool()
        {
            if (m_jointPool.Count > 0)
            {
                var joint = m_jointPool[m_jointPool.Count - 1];
                m_jointPool.RemoveAt(m_jointPool.Count - 1);
                return joint;
            }

            // Create new joint from prefab
            if (m_jointPrefab != null)
            {
                return Instantiate(m_jointPrefab, transform);
            }

            // Fallback: create sphere
            Debug.Log("[POSE UI] Creating fallback joint sphere (no prefab)");
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.SetParent(transform);

            // Set material with bright color for visibility in VR
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                // Try URP shader first, fallback to Standard if not available
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                {
                    shader = Shader.Find("Unlit/Color");
                    Debug.LogWarning("[POSE UI] URP/Unlit not found, using Unlit/Color");
                }
                if (shader == null)
                {
                    shader = Shader.Find("Standard");
                    Debug.LogWarning("[POSE UI] Unlit/Color not found, using Standard");
                }

                renderer.material = new Material(shader);
                renderer.material.color = Color.yellow;
            }

            return go;
        }

        private LineRenderer GetBoneFromPool()
        {
            if (m_bonePool.Count > 0)
            {
                var bone = m_bonePool[m_bonePool.Count - 1];
                m_bonePool.RemoveAt(m_bonePool.Count - 1);
                return bone;
            }

            // Create new bone from prefab or create one
            LineRenderer lineRenderer;
            if (m_bonePrefab != null)
            {
                lineRenderer = Instantiate(m_bonePrefab, transform);
            }
            else
            {
                Debug.Log("[POSE UI] Creating fallback bone LineRenderer (no prefab)");
                var go = new GameObject("Bone");
                go.transform.SetParent(transform);
                lineRenderer = go.AddComponent<LineRenderer>();

                // Try URP shader first, fallback to others if not available
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                {
                    shader = Shader.Find("Unlit/Color");
                    Debug.LogWarning("[POSE UI] URP/Unlit not found, using Unlit/Color for bones");
                }
                if (shader == null)
                {
                    shader = Shader.Find("Particles/Standard Unlit");
                    Debug.LogWarning("[POSE UI] Unlit/Color not found, using Particles/Standard Unlit for bones");
                }

                Material boneMaterial = new Material(shader != null ? shader : Shader.Find("Standard"));
                boneMaterial.color = Color.cyan;

                lineRenderer.material = boneMaterial;
                lineRenderer.startWidth = 0.015f;
                lineRenderer.endWidth = 0.015f;
                lineRenderer.numCapVertices = 2;
                lineRenderer.numCornerVertices = 2;
            }

            return lineRenderer;
        }

        private void ReturnSkeletonToPool(SkeletonInstance skeleton)
        {
            foreach (var joint in skeleton.Joints)
            {
                joint.SetActive(false);
                m_jointPool.Add(joint);
            }
            skeleton.Joints.Clear();

            foreach (var bone in skeleton.Bones)
            {
                bone.gameObject.SetActive(false);
                m_bonePool.Add(bone);
            }
            skeleton.Bones.Clear();
        }

        internal Transform ContentParent => transform;
    }
}
