using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PFound.ContentDelivery.Editor
{
    /// <summary>
    /// Inspector drawer for <c>AssetReference&lt;T&gt;</c>: shows the address as an editable field plus a dropdown of
    /// valid addresses gathered from the project's <see cref="AssetGroup"/>s, filtered to those whose asset is a
    /// <typeparamref name="T"/>. The address string is the must-have — the dropdown is convenience — so a manual
    /// text field always remains, and address gathering is a cheap on-open scan (no heavy cache). Editor-only.
    /// </summary>
    [CustomPropertyDrawer(typeof(AssetReference<>), true)]
    public sealed class AssetReferenceDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty address = property.FindPropertyRelative("_address");

            EditorGUI.BeginProperty(position, label, property);

            // Split: label + text field for the address, then a compact dropdown button.
            const float dropdownWidth = 22f;
            var fieldRect = new Rect(position.x, position.y, position.width - dropdownWidth - 2f, position.height);
            var buttonRect = new Rect(fieldRect.xMax + 2f, position.y, dropdownWidth, position.height);

            EditorGUI.PropertyField(fieldRect, address, label);

            if (GUI.Button(buttonRect, "▾", EditorStyles.miniButton))
                ShowAddressMenu(address);

            EditorGUI.EndProperty();
        }

        private void ShowAddressMenu(SerializedProperty address)
        {
            Type assetType = ResolveAssetType();
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("<none>"), string.IsNullOrEmpty(address.stringValue), () =>
            {
                address.stringValue = string.Empty;
                address.serializedObject.ApplyModifiedProperties();
            });
            menu.AddSeparator(string.Empty);

            foreach (string candidate in GatherAddresses(assetType))
            {
                string captured = candidate;
                menu.AddItem(new GUIContent(candidate), string.Equals(candidate, address.stringValue, StringComparison.Ordinal), () =>
                {
                    address.stringValue = captured;
                    address.serializedObject.ApplyModifiedProperties();
                });
            }
            menu.ShowAsContext();
        }

        // The T of AssetReference<T> for this field (unwrapping arrays / lists); UnityEngine.Object if unresolved.
        private Type ResolveAssetType()
        {
            Type type = fieldInfo.FieldType;
            if (type.IsArray) type = type.GetElementType();
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) type = type.GetGenericArguments()[0];
            if (type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AssetReference<>))
                return type.GetGenericArguments()[0];
            return typeof(UnityEngine.Object);
        }

        private static IEnumerable<string> GatherAddresses(Type assetType)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in ContentDeliveryMenu.LoadAllGroups())
            {
                if (group.Entries == null) continue;
                foreach (var entry in group.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Address)) continue;
                    if (entry.Asset != null && !assetType.IsInstanceOfType(entry.Asset)) continue;
                    if (seen.Add(entry.Address)) yield return entry.Address;
                }
            }
        }
    }
}
