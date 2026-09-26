// ============================================================
// Script:      NetworkBootstrap.cs
// Description: Starts FishNet as host, server or client without
//              clicking the HUD. The start mode is chosen from, in
//              order of priority:
//
//                1. Command-line flags (builds):
//                     -host | -server | -client
//                     -address <ip>   -port <port>
//                2. Multiplayer Play Mode tags (editor):
//                     "Host", "Server" or "Client"
//                3. The Default Mode set in the Inspector.
//
//              Put this on the NetworkManager GameObject. Set the
//              NetworkHudCanvas Auto Start Type to Disabled so the
//              connection is not started twice.
// Author:      Goodgulf
// ============================================================

using System;
using FishNet.Managing;
using Goodgulf.Logging;
using UnityEngine;

namespace Goodgulf.Networking
{
    /// <summary>How this process joins the network session.</summary>
    public enum NetworkStartMode
    {
        None,
        Host,
        Server,
        Client
    }

    /// <summary>
    /// Automatically starts the FishNet connection based on command-line flags,
    /// Multiplayer Play Mode tags, or an Inspector default.
    /// </summary>
    public class NetworkBootstrap : MonoBehaviour, IDebuggable
    {
        // ==================================================================
        // Inspector fields
        // ==================================================================

        [Header("References")]
        [Tooltip("Falls back to the NetworkManager on this GameObject when empty.")]
        [SerializeField] private NetworkManager _networkManager;

        [Header("Start Mode")]
        [Tooltip("Used when no command-line flag or Multiplayer Play Mode tag selects a mode. " +
                 "None leaves starting to the HUD.")]
        [SerializeField] private NetworkStartMode _defaultMode = NetworkStartMode.None;

        [Tooltip("Server address for clients. Overridden by -address on the command line.")]
        [SerializeField] private string _clientAddress = "localhost";

        [Header("Debug")]
        [SerializeField] private bool _debugEnabled = true;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;

        // ==================================================================
        // Public accessors
        // ==================================================================

        /// <summary>The mode this process started in. None until Start has run.</summary>
        public NetworkStartMode ResolvedMode { get; private set; }

        // ==================================================================
        // Unity lifecycle
        // ==================================================================

        private void Awake()
        {
            if (_networkManager == null && !TryGetComponent(out _networkManager))
                this.LogError("No NetworkManager assigned or found on this GameObject.");
        }

        private void Start()
        {
            if (_networkManager == null)
                return;

            string[] args = Environment.GetCommandLineArgs();

            NetworkStartMode mode = ReadModeFromArgs(args, out string source);
            if (mode == NetworkStartMode.None)
                mode = ReadModeFromTags(out source);
            if (mode == NetworkStartMode.None)
            {
                mode   = _defaultMode;
                source = "Inspector default";
            }

            ApplyTransportSettings(args);

            ResolvedMode = mode;
            this.LogInfo($"Start mode {mode} (from {source}), instance '{PlayModeInstance.InstanceId}'.");

            StartNetwork(mode);
        }

        // ==================================================================
        // Mode resolution
        // ==================================================================

        private static NetworkStartMode ReadModeFromArgs(string[] args, out string source)
        {
            source = "command line";
            foreach (string arg in args)
            {
                if (arg.Equals("-host", StringComparison.OrdinalIgnoreCase))   return NetworkStartMode.Host;
                if (arg.Equals("-server", StringComparison.OrdinalIgnoreCase)) return NetworkStartMode.Server;
                if (arg.Equals("-client", StringComparison.OrdinalIgnoreCase)) return NetworkStartMode.Client;
            }
            return NetworkStartMode.None;
        }

        private static NetworkStartMode ReadModeFromTags(out string source)
        {
            source = "Multiplayer Play Mode tag";
            if (PlayModeInstance.HasTag("Host"))   return NetworkStartMode.Host;
            if (PlayModeInstance.HasTag("Server")) return NetworkStartMode.Server;
            if (PlayModeInstance.HasTag("Client")) return NetworkStartMode.Client;
            return NetworkStartMode.None;
        }

        private void ApplyTransportSettings(string[] args)
        {
            string address = _clientAddress;

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("-address", StringComparison.OrdinalIgnoreCase))
                {
                    address = args[i + 1];
                }
                else if (args[i].Equals("-port", StringComparison.OrdinalIgnoreCase))
                {
                    if (ushort.TryParse(args[i + 1], out ushort port))
                        _networkManager.TransportManager.Transport.SetPort(port);
                    else
                        this.LogWarning($"Ignoring invalid -port value '{args[i + 1]}'.");
                }
            }

            if (!string.IsNullOrEmpty(address))
                _networkManager.TransportManager.Transport.SetClientAddress(address);
        }

        // ==================================================================
        // Starting
        // ==================================================================

        private void StartNetwork(NetworkStartMode mode)
        {
            bool startServer = mode == NetworkStartMode.Host || mode == NetworkStartMode.Server;
            bool startClient = mode == NetworkStartMode.Host || mode == NetworkStartMode.Client;

            // A headless build may already have started the server (ServerManager > Start On Headless).
            if (startServer && !_networkManager.ServerManager.Started)
            {
                if (!_networkManager.ServerManager.StartConnection())
                    this.LogError("Server failed to start. Is the port already in use?");
            }

            if (startClient && !_networkManager.ClientManager.Started)
            {
                if (!_networkManager.ClientManager.StartConnection())
                    this.LogError("Client failed to start.");
            }
        }
    }
}
