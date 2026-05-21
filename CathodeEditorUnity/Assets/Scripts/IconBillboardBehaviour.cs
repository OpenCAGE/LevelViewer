using UnityEngine;

/// <summary>
/// Rotates a quad to face the active scene camera (editor icon billboard).
/// </summary>
[ExecuteAlways]
public class IconBillboardBehaviour : MonoBehaviour
{
    private void LateUpdate()
    {
        Camera camera = GetFacingCamera();
        if (camera == null)
            return;

        Vector3 forward = camera.transform.rotation * Vector3.forward;
        Vector3 up = camera.transform.rotation * Vector3.up;
        transform.LookAt(transform.position + forward, up);
    }

    private static Camera GetFacingCamera()
    {
#if UNITY_EDITOR
        if (UnityEditor.SceneView.lastActiveSceneView != null)
            return UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
        return Camera.main;
    }
}
