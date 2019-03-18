#if UNITY_EDITOR

using UnityEditor;

namespace ModIO.UI.Editor
{
    [CustomEditor(typeof(ScaleFitter))]
    [CanEditMultipleObjects]
    public class ScaleFitterEditor : UnityEditor.Editor
    {
        private void OnEnable()
        {
            Tools.hidden = ((target as ScaleFitter).aspectMode != ScaleFitter.AspectMode.Disabled);
        }
        private void OnDisable()
        {
            Tools.hidden = false;
        }
        private void OnSceneGUI()
        {
            Tools.hidden = ((target as ScaleFitter).aspectMode != ScaleFitter.AspectMode.Disabled);
        }
    }
}
#endif
