// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Text.RegularExpressions;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Tool to fix incorrect IP addresses in scene files.
    /// Menu: Tools > PassthroughCameraApiSamples > Fix Scene IPs
    /// </summary>
    public static class FixSceneIPs
    {
        private const string CORRECT_IP = "192.168.0.135";
        private const string WRONG_IP_1 = "192.168.0.155";
        private const string WRONG_IP_2 = "35.9.28.119";

        private static readonly string[] SCENE_PATHS = new string[]
        {
            "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity",
            "Assets/PassthroughCameraApiSamples/PoseEstimation/PassthroughPoseEstimation.unity",
            "Assets/PassthroughCameraApiSamples/DepthEstimation/DepthEstimation.unity"
        };

        [MenuItem("Tools/PassthroughCameraApiSamples/Fix Scene IPs")]
        public static void FixAllSceneIPs()
        {
            bool proceed = EditorUtility.DisplayDialog(
                "Fix Scene IP Addresses",
                $"This will replace all incorrect IPs with:\n{CORRECT_IP}\n\n" +
                $"Incorrect IPs to fix:\n" +
                $"• {WRONG_IP_1}\n" +
                $"• {WRONG_IP_2}\n\n" +
                "Continue?",
                "Fix All",
                "Cancel"
            );

            if (!proceed)
            {
                Debug.Log("[Fix Scene IPs] Cancelled by user.");
                return;
            }

            int totalFixed = 0;
            int totalScenes = 0;

            foreach (string scenePath in SCENE_PATHS)
            {
                if (System.IO.File.Exists(scenePath))
                {
                    int fixCount = FixSceneFile(scenePath);
                    totalFixed += fixCount;
                    totalScenes++;
                }
                else
                {
                    Debug.LogWarning($"[Fix Scene IPs] Scene not found: {scenePath}");
                }
            }

            string message = $"IP Fix Complete!\n\n" +
                           $"Scenes processed: {totalScenes}\n" +
                           $"Total IPs fixed: {totalFixed}\n\n" +
                           $"Correct IP: {CORRECT_IP}";

            EditorUtility.DisplayDialog("Fix Complete", message, "OK");
            Debug.Log($"[Fix Scene IPs] Complete! Fixed {totalFixed} IPs across {totalScenes} scenes.");

            // Refresh AssetDatabase
            AssetDatabase.Refresh();
        }

        private static int FixSceneFile(string scenePath)
        {
            Debug.Log($"[Fix Scene IPs] Processing: {scenePath}");

            // Read scene file as text
            string content = System.IO.File.ReadAllText(scenePath);
            string originalContent = content;
            int fixCount = 0;

            // Replace wrong IP 1
            string pattern1 = WRONG_IP_1;
            int count1 = Regex.Matches(content, Regex.Escape(pattern1)).Count;
            if (count1 > 0)
            {
                content = content.Replace(pattern1, CORRECT_IP);
                fixCount += count1;
                Debug.Log($"[Fix Scene IPs]   Replaced {count1}x: {WRONG_IP_1} → {CORRECT_IP}");
            }

            // Replace wrong IP 2
            string pattern2 = WRONG_IP_2;
            int count2 = Regex.Matches(content, Regex.Escape(pattern2)).Count;
            if (count2 > 0)
            {
                content = content.Replace(pattern2, CORRECT_IP);
                fixCount += count2;
                Debug.Log($"[Fix Scene IPs]   Replaced {count2}x: {WRONG_IP_2} → {CORRECT_IP}");
            }

            // Write back if changed
            if (content != originalContent)
            {
                System.IO.File.WriteAllText(scenePath, content);
                Debug.Log($"[Fix Scene IPs]   ✅ Saved changes to: {scenePath}");
            }
            else
            {
                Debug.Log($"[Fix Scene IPs]   ✅ No changes needed: {scenePath}");
            }

            return fixCount;
        }

        [MenuItem("Tools/PassthroughCameraApiSamples/Verify Scene IPs")]
        public static void VerifySceneIPs()
        {
            Debug.Log("========== Scene IP Verification ==========");

            string report = "IP Verification Report:\n\n";
            int totalWrongIPs = 0;

            foreach (string scenePath in SCENE_PATHS)
            {
                if (System.IO.File.Exists(scenePath))
                {
                    string content = System.IO.File.ReadAllText(scenePath);
                    string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);

                    int correctCount = Regex.Matches(content, Regex.Escape(CORRECT_IP)).Count;
                    int wrong1Count = Regex.Matches(content, Regex.Escape(WRONG_IP_1)).Count;
                    int wrong2Count = Regex.Matches(content, Regex.Escape(WRONG_IP_2)).Count;

                    report += $"{sceneName}:\n";
                    report += $"  ✅ Correct ({CORRECT_IP}): {correctCount}\n";

                    if (wrong1Count > 0)
                    {
                        report += $"  ❌ Wrong ({WRONG_IP_1}): {wrong1Count}\n";
                        totalWrongIPs += wrong1Count;
                    }

                    if (wrong2Count > 0)
                    {
                        report += $"  ❌ Wrong ({WRONG_IP_2}): {wrong2Count}\n";
                        totalWrongIPs += wrong2Count;
                    }

                    report += "\n";
                }
            }

            if (totalWrongIPs > 0)
            {
                report += $"⚠️ Found {totalWrongIPs} incorrect IPs!\n";
                report += $"\nRun 'Fix Scene IPs' to correct them.";
            }
            else
            {
                report += "✅ All IPs are correct!";
            }

            Debug.Log(report);
            EditorUtility.DisplayDialog("IP Verification", report, "OK");
        }
    }
}
