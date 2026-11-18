using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace GuardWave.Editor
{
    /// <summary>
    /// One Inspector field accepts "Infinity" or a nonnegative numerical torque.
    /// The underlying prefab property is a single serialized float.
    /// </summary>
    [CustomEditor(typeof(GuardWaveMudJointSettings))]
    [CanEditMultipleObjects]
    public sealed class GuardWaveMudJointSettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Mud FixedJoint - breaking", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("breakForce"));

            SerializedProperty torque = serializedObject.FindProperty("breakTorque");
            string displayed = torque.hasMultipleDifferentValues
                ? string.Empty
                : float.IsPositiveInfinity(torque.floatValue)
                    ? "Infinity"
                    : torque.floatValue.ToString("G9", CultureInfo.InvariantCulture);

            EditorGUI.BeginChangeCheck();
            string input = EditorGUILayout.DelayedTextField(
                new GUIContent("Break Torque",
                    "Type Infinity or a nonnegative number, such as 2800."),
                displayed);
            if (EditorGUI.EndChangeCheck())
            {
                string valueText = input.Trim();
                if (string.Equals(valueText, "Infinity", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(valueText, "Infinite", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(valueText, "Inf", StringComparison.OrdinalIgnoreCase) ||
                    valueText == "∞")
                {
                    torque.floatValue = Mathf.Infinity;
                }
                else if (float.TryParse(valueText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float number) &&
                    !float.IsNaN(number) && number >= 0f)
                {
                    torque.floatValue = number;
                }
                else
                {
                    Debug.LogWarning("Break Torque requires Infinity or a nonnegative number.");
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mud FixedJoint - stability", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enablePreprocessing"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("massScale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("connectedMassScale"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Connected Rigidbody solver", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("solverIterations"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("solverVelocityIterations"));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
