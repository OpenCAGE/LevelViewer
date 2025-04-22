//#define LOCAL_DEV

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;
using UnityEditor.SceneManagement;
using System.IO;
using System.Reflection;

[InitializeOnLoad]
public class SetSceneAndDisableTools
{
    private static bool _subbed = false;

    static SetSceneAndDisableTools()
    {
        if (!_subbed)
        {
            EditorApplication.update += ForceNoTools;
            _subbed = true;
        }

        if (!EditorApplication.isPlaying)
        {
#if !LOCAL_DEV
            var scene = EditorSceneManager.GetActiveScene();
            if (scene == null || scene.name != "Assets/Scene.unity")
                EditorSceneManager.OpenScene("Assets/Scene.unity");

            EditorApplication.EnterPlaymode();
#endif
            return;
        }
    }

    static void ForceNoTools()
    {
#if !LOCAL_DEV
        if (Tools.current != Tool.None)
            Tools.current = Tool.None;
#endif
    }
}

#if !LOCAL_DEV
[InitializeOnLoad]
public static class CloseAllExceptSceneView
{
    static CloseAllExceptSceneView()
    {
        EditorApplication.delayCall += () =>
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (!(window is SceneView))
                    window.Close();
            }

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                var maximizeMethod = typeof(EditorWindow).GetMethod("Maximize", BindingFlags.NonPublic | BindingFlags.Instance);
                maximizeMethod?.Invoke(sceneView, new object[] { true });

                sceneView.sceneLighting = false;
                sceneView.Repaint();
            }
        };
    }
}
#endif
#endif