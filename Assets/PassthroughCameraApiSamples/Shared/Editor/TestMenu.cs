// Test script to verify Unity menu system is working
using UnityEngine;
using UnityEditor;

namespace PassthroughCameraSamples.Shared.Editor
{
    public static class TestMenu
    {
        [MenuItem("Tools/PassthroughCameraApiSamples/Test Menu Item")]
        public static void TestMenuItem()
        {
            Debug.Log("Test menu item works!");
            EditorUtility.DisplayDialog("Test", "Menu system is working!", "OK");
        }
    }
}
