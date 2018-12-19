﻿#if UNITY_EDITOR
using UnityEditor;

namespace ModIO.Editor
{
    public interface IModProfileViewPart
    {
        void OnEnable(SerializedProperty serializedEditableModProfile, ModProfile profile, UserProfile user);
        void OnDisable();
        void OnGUI();
        void OnUpdate();
        bool IsRepaintRequired();
    }
}

#endif
