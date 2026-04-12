// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using System;
using System.Linq;

namespace PassthroughCameraSamples.Shared.Editor
{
    /// <summary>
    /// Automated build script for Quest 3 deployment.
    /// </summary>
    public class BuildForQuest3
    {
        [MenuItem("Tools/Build for Quest 3")]
        public static void BuildQuest3()
        {
            Debug.Log("[BUILD] Starting Quest 3 build process...");

            // 1. Verify Android platform
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.LogWarning("[BUILD] Switching to Android platform...");
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            }

            // 2. Configure build settings
            EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // 3. Get scenes in build
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[BUILD] No scenes enabled in build settings!");
                EditorUtility.DisplayDialog("Build Error", "No scenes enabled in build settings!", "OK");
                return;
            }

            Debug.Log($"[BUILD] Building {scenes.Length} scenes:");
            foreach (var scene in scenes)
            {
                Debug.Log($"  - {scene}");
            }

            // 4. Set build path
            string buildPath = "Builds/Quest3/PassthroughCameraSamples.apk";
            System.IO.Directory.CreateDirectory("Builds/Quest3");

            // 5. Build options
            BuildPlayerOptions buildOptions = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = buildPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Debug.Log($"[BUILD] Output: {buildPath}");

            // 6. Execute build
            BuildReport report = BuildPipeline.BuildPlayer(buildOptions);
            BuildSummary summary = report.summary;

            // 7. Report results
            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[BUILD] ✓ Build succeeded!");
                Debug.Log($"[BUILD] Size: {summary.totalSize / (1024 * 1024)} MB");
                Debug.Log($"[BUILD] Time: {summary.totalTime.TotalSeconds:F1}s");
                Debug.Log($"[BUILD] Output: {buildPath}");

                bool deploy = EditorUtility.DisplayDialog("Build Successful",
                    $"Build completed successfully!\n\n" +
                    $"Size: {summary.totalSize / (1024 * 1024)} MB\n" +
                    $"Output: {buildPath}\n\n" +
                    "Deploy to connected Quest 3 device?",
                    "Deploy", "Cancel");

                if (deploy)
                {
                    DeployToDevice(buildPath);
                }
            }
            else
            {
                Debug.LogError($"[BUILD] ✗ Build failed: {summary.result}");

                // Log errors
                if (report.steps != null)
                {
                    foreach (var step in report.steps)
                    {
                        foreach (var message in step.messages)
                        {
                            if (message.type == LogType.Error || message.type == LogType.Exception)
                            {
                                Debug.LogError($"[BUILD ERROR] {message.content}");
                            }
                        }
                    }
                }

                EditorUtility.DisplayDialog("Build Failed",
                    $"Build failed with result: {summary.result}\n\n" +
                    "Check Console for error details.",
                    "OK");
            }
        }

        [MenuItem("Tools/Deploy to Quest 3")]
        public static void DeployToDevice()
        {
            string apkPath = "Builds/Quest3/PassthroughCameraSamples.apk";

            if (!System.IO.File.Exists(apkPath))
            {
                EditorUtility.DisplayDialog("Deploy Error",
                    $"APK not found at: {apkPath}\n\nBuild first!",
                    "OK");
                return;
            }

            DeployToDevice(apkPath);
        }

        private static void DeployToDevice(string apkPath)
        {
            Debug.Log("[DEPLOY] Deploying to Quest 3...");

            // Check for ADB
            string adbPath = UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath + "/platform-tools/adb.exe";

            if (!System.IO.File.Exists(adbPath))
            {
                Debug.LogError($"[DEPLOY] ADB not found at: {adbPath}");
                EditorUtility.DisplayDialog("Deploy Error", "ADB not found! Check Android SDK installation.", "OK");
                return;
            }

            // Check for connected devices
            var checkDevices = new System.Diagnostics.ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "devices",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using (var process = System.Diagnostics.Process.Start(checkDevices))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (!output.Contains("device") || output.Split('\n').Length < 3)
                {
                    Debug.LogWarning("[DEPLOY] No Quest 3 device connected!");
                    EditorUtility.DisplayDialog("Deploy Warning",
                        "No Quest 3 device detected!\n\n" +
                        "1. Connect Quest 3 via USB\n" +
                        "2. Enable Developer Mode\n" +
                        "3. Allow USB debugging",
                        "OK");
                    return;
                }
            }

            // Install APK
            var installProcess = new System.Diagnostics.ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"install -r \"{apkPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Debug.Log($"[DEPLOY] Running: adb install -r \"{apkPath}\"");

            using (var process = System.Diagnostics.Process.Start(installProcess))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                Debug.Log($"[DEPLOY] Output: {output}");
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogWarning($"[DEPLOY] Errors: {error}");
                }

                if (output.Contains("Success"))
                {
                    Debug.Log("[DEPLOY] ✓ Successfully deployed to Quest 3!");
                    EditorUtility.DisplayDialog("Deploy Successful",
                        "App deployed to Quest 3!\n\n" +
                        "You can now launch it from the Quest 3 Apps library.",
                        "OK");
                }
                else
                {
                    Debug.LogError("[DEPLOY] ✗ Deployment failed!");
                    EditorUtility.DisplayDialog("Deploy Failed",
                        $"Deployment failed!\n\n{output}\n{error}",
                        "OK");
                }
            }
        }

        [MenuItem("Tools/Check Build Configuration")]
        public static void CheckConfiguration()
        {
            string report = "Build Configuration:\n\n";

            report += $"Platform: {EditorUserBuildSettings.activeBuildTarget}\n";
            report += $"Build System: {EditorUserBuildSettings.androidBuildSystem}\n";
            report += $"Architecture: {PlayerSettings.Android.targetArchitectures}\n";
            report += $"Package Name: {PlayerSettings.applicationIdentifier}\n";
            report += $"Version: {PlayerSettings.bundleVersion}\n";
            report += $"Min API Level: {PlayerSettings.Android.minSdkVersion}\n";
            report += $"Target API Level: {PlayerSettings.Android.targetSdkVersion}\n";

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).ToArray();
            report += $"\nScenes in Build: {scenes.Length}\n";
            foreach (var scene in scenes)
            {
                report += $"  - {scene.path}\n";
            }

            Debug.Log($"[CONFIG]\n{report}");
            EditorUtility.DisplayDialog("Build Configuration", report, "OK");
        }
    }
}
