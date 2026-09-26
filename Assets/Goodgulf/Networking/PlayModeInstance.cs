// ============================================================
// Script:      PlayModeInstance.cs
// Description: Identifies which Multiplayer Play Mode (MPPM) player
//              this process is, so per-player state can be kept apart.
//
//              • InstanceId — empty for the main editor and for builds;
//                the virtual player's clone folder name otherwise.
//                MPPM clones run from <project>/Library/VP/<name>/.
//              • Tags — the tags assigned to this player in
//                Window > Multiplayer > Multiplayer Play Mode.
//                Read through reflection so the project compiles
//                whether or not the MPPM package is installed.
//
//              Access from the main thread first: Application.dataPath
//              is a main-thread-only Unity API. Results are cached.
// Author:      Goodgulf
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Goodgulf.Networking
{
    /// <summary>
    /// Per-process identity for Multiplayer Play Mode testing.
    /// All members are safe to use in builds, where they report the main instance with no tags.
    /// </summary>
    public static class PlayModeInstance
    {
        private const string VirtualPlayerFolderMarker = "/Library/VP/";

        private static string   _instanceId;
        private static string[] _tags;

        /// <summary>
        /// Empty for the main editor and builds; the virtual player's folder name otherwise.
        /// </summary>
        public static string InstanceId => _instanceId ??= ResolveInstanceId();

        /// <summary>True when this process is an MPPM virtual player.</summary>
        public static bool IsVirtualPlayer => InstanceId.Length > 0;

        /// <summary>
        /// MPPM tags for this player. Empty in builds, or when the package is not installed.
        /// </summary>
        public static IReadOnlyList<string> Tags => _tags ??= ReadTags();

        /// <summary>Case-insensitive check for an MPPM tag on this player.</summary>
        public static bool HasTag(string tag)
        {
            foreach (string t in Tags)
            {
                if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns <c>persistentDataPath/folderName</c>, suffixed with the instance ID
        /// for virtual players so each player gets its own folder.
        /// </summary>
        public static string GetInstanceDataPath(string folderName)
        {
            string folder = IsVirtualPlayer ? $"{folderName}_{InstanceId}" : folderName;
            return Path.Combine(Application.persistentDataPath, folder);
        }

        private static string ResolveInstanceId()
        {
#if UNITY_EDITOR
            string dataPath = Application.dataPath.Replace('\\', '/');
            int markerIndex = dataPath.IndexOf(VirtualPlayerFolderMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                string rest = dataPath.Substring(markerIndex + VirtualPlayerFolderMarker.Length);
                int slash = rest.IndexOf('/');
                return slash >= 0 ? rest.Substring(0, slash) : rest;
            }
#endif
            return string.Empty;
        }

        private static string[] ReadTags()
        {
#if UNITY_EDITOR
            try
            {
                Type currentPlayer = Type.GetType(
                    "Unity.Multiplayer.Playmode.CurrentPlayer, Unity.Multiplayer.Playmode");
                MethodInfo readOnlyTags = currentPlayer?.GetMethod(
                    "ReadOnlyTags", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

                if (readOnlyTags?.Invoke(null, null) is IEnumerable<string> tags)
                    return new List<string>(tags).ToArray();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"PlayModeInstance: could not read Multiplayer Play Mode tags: {e.Message}");
            }
#endif
            return Array.Empty<string>();
        }
    }
}
