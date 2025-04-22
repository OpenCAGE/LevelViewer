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
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public class SetSceneAndDisableTools
{
    static bool _sceneLoaded;
    static bool _tryingToPlay;

    static SetSceneAndDisableTools()
    {
#if !LOCAL_DEV
        EditorApplication.update += WaitForSceneAndPlay;
        EditorApplication.update += ForceNoTools;
#endif
    }

    static void WaitForSceneAndPlay()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        if (!_sceneLoaded)
        {
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene == null || activeScene.path != "Assets/Scene.unity")
            {
                EditorSceneManager.OpenScene("Assets/Scene.unity");
                return; // wait for next update to continue
            }
            _sceneLoaded = true;
        }

        if (!_tryingToPlay)
        {
            _tryingToPlay = true;
            EditorApplication.EnterPlaymode();
        }
    }

    static void ForceNoTools()
    {
        if (Tools.current != Tool.None)
            Tools.current = Tool.None;
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