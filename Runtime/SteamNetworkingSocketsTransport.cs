#if !DISABLESTEAMWORKS && STEAMWORKSNET && NETCODEGAMEOBJECTS

#region

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using Unity.Netcode;
using UnityEngine;
using Debug = UnityEngine.Debug;

#endregion

namespace Netcode.Transports
{
    public class SteamNetworkingSocketsTransport : NetworkTransport
    {
        #region Internal Object Model

        private class SteamConnectionData
        {
            internal SteamConnectionData(CSteamID steamId) {
                this.id = steamId;
            }

            internal readonly CSteamID id;
            internal HSteamNetConnection connection;
        }

        private Callback<SteamNetConnectionStatusChangedCallback_t> c_onConnectionChange;
        private HSteamListenSocket listenSocket;
        private SteamConnectionData client;
        private readonly Dictionary<ulong, SteamConnectionData> connectionMapping = new Dictionary<ulong, SteamConnectionData>();
        private readonly Queue<SteamNetConnectionStatusChangedCallback_t> connectionStatusChangeQueue = new Queue<SteamNetConnectionStatusChangedCallback_t>();
        private readonly Dictionary<ulong, string> pendingDisconnectReasons = new Dictionary<ulong, string>();
        private bool isServer;

        #endregion

        public string LastDisconnectReason { get; private set; }

        public ulong ConnectToSteamID;
        public SteamNetworkingConfigValue_t[] options = Array.Empty<SteamNetworkingConfigValue_t>();

        public override ulong ServerClientId => 0;

        public override bool IsSupported {
            get {
                try
                {
                    #if UNITY_SERVER
                    InteropHelp.TestIfAvailableGameServer();
                    #else
                    InteropHelp.TestIfAvailableClient();
                    #endif
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void SetPendingDisconnectReason(CSteamID clientId, string reason) {
            if (!clientId.IsValid() || string.IsNullOrEmpty(reason)) return;
            this.pendingDisconnectReasons[clientId.m_SteamID] = reason;
        }

        public override void DisconnectLocalClient() {
            if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) NetworkLog.LogInfoServer(nameof(SteamNetworkingSocketsTransport) + " - DisconnectLocalClient");

            if (this.client != null)
            {
                #if UNITY_SERVER
                SteamGameServerNetworkingSockets.CloseConnection(this.client.connection, 0, "", false);
                #else
                SteamNetworkingSockets.CloseConnection(this.client.connection, 0, "", false);
                #endif

                this.connectionMapping.Remove(this.client.id.m_SteamID);
            }

            this.client = null;
        }

        public override void DisconnectRemoteClient(ulong clientId) {
            if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) NetworkLog.LogInfoServer(nameof(SteamNetworkingSocketsTransport.DisconnectRemoteClient) + " - clientId: " + clientId);

            if (!this.connectionMapping.ContainsKey(clientId))
            {
                if (NetworkManager.Singleton?.LogLevel <= LogLevel.Error) NetworkLog.LogErrorServer(nameof(SteamNetworkingSocketsTransport) + " - Can't disconnect client, client not connected, clientId: " + clientId);
                this.pendingDisconnectReasons.Remove(clientId);
                return;
            }

            string reason = this.pendingDisconnectReasons.GetValueOrDefault(clientId, "Disconnected");
            this.pendingDisconnectReasons.Remove(clientId);

            #if UNITY_SERVER
            SteamGameServerNetworkingSockets.CloseConnection(connectionMapping[clientId].connection, 0, reason, false);
            #else
            SteamNetworkingSockets.CloseConnection(this.connectionMapping[clientId].connection, 0, reason, false);
            #endif

            this.connectionMapping.Remove(clientId);
        }

        public override ulong GetCurrentRtt(ulong clientId) {
            if (!this.connectionMapping.TryGetValue(clientId, out SteamConnectionData connectionData)) return 0ul;

            SteamNetConnectionRealTimeStatus_t status = new SteamNetConnectionRealTimeStatus_t();
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = new SteamNetConnectionRealTimeLaneStatus_t();

            #if UNITY_SERVER
            EResult result = SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connectionData.connection, ref status, 0, ref laneStatus);
            #else
            EResult result = SteamNetworkingSockets.GetConnectionRealTimeStatus(connectionData.connection, ref status, 0, ref laneStatus);
            #endif

            if (result == EResult.k_EResultOK) return (ulong)status.m_nPing;
            if (NetworkManager.Singleton?.LogLevel <= LogLevel.Error) Debug.LogError($"Failed to get RTT for client {clientId}: {result}");

            return 0ul;
        }

        public override void Initialize(NetworkManager networkManager = null) {
            if (!this.IsSupported)
                if (NetworkManager.Singleton.LogLevel <= LogLevel.Error)
                    NetworkLog.LogErrorServer(nameof(SteamNetworkingSocketsTransport) + " - Initialize - Steamworks.NET not ready, " + nameof(SteamNetworkingSocketsTransport) + " can not run without it");
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime) {
            #region Connection State Changes

            while (this.connectionStatusChangeQueue.Count > 0)
            {
                SteamNetConnectionStatusChangedCallback_t param = this.connectionStatusChangeQueue.Dequeue();

                if (param.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
                {
                    //This happens when someone asked to connect to us, in the case of NetCode for GameObject this should only happen if we are a server/host
                    //the current standard is to blindly accept ... NetCode for GO should really consider a validation model for connections
                    if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) NetworkLog.LogInfoServer(nameof(SteamNetworkingSocketsTransport) + " - connection request from " + param.m_info.m_identityRemote.GetSteamID64());

                    if (this.isServer)
                    {
                        EResult res;
                        #if UNITY_SERVER
                        if ((res = SteamGameServerNetworkingSockets.AcceptConnection(param.m_hConn)) == EResult.k_EResultOK)
                            #else
                        if ((res = SteamNetworkingSockets.AcceptConnection(param.m_hConn)) == EResult.k_EResultOK)
                            #endif
                        {
                            if (this.isServer)
                            {
                                if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) Debug.Log($"Accepting connection {param.m_info.m_identityRemote.GetSteamID64()}");

                                clientId = param.m_info.m_identityRemote.GetSteamID64();
                                payload = new ArraySegment<byte>();
                                receiveTime = Time.realtimeSinceStartup;

                                if (this.connectionMapping.ContainsKey(clientId)) continue;
                                SteamConnectionData nCon = new SteamConnectionData(param.m_info.m_identityRemote.GetSteamID()) {
                                    connection = param.m_hConn
                                };

                                this.connectionMapping.Add(clientId, nCon);
                            }
                            else
                            {
                                if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) Debug.Log($"Connection {param.m_info.m_identityRemote.GetSteamID64()} could not be accepted: this is not a server");
                            }
                        }
                        else
                        {
                            if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) Debug.Log($"Connection {param.m_info.m_identityRemote.GetSteamID64()} could not be accepted: {res}");
                        }
                    }
                }
                else if (param.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                {
                    if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) Debug.Log(nameof(SteamNetworkingSocketsTransport) + " - connection request to " + param.m_info.m_identityRemote.GetSteamID64() + " was accepted!");

                    clientId = param.m_info.m_identityRemote.GetSteamID64();
                    payload = new ArraySegment<byte>();
                    receiveTime = Time.realtimeSinceStartup;

                    if (!this.connectionMapping.TryGetValue(clientId, out SteamConnectionData value))
                    {
                        SteamConnectionData nCon = new SteamConnectionData(param.m_info.m_identityRemote.GetSteamID()) {
                            connection = param.m_hConn
                        };

                        this.connectionMapping.Add(clientId, nCon);
                    }
                    else
                        value.connection = param.m_hConn;

                    return NetworkEvent.Connect;
                }
                else if (param.m_info.m_eState is ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer or ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                {
                    Debug.Log(nameof(SteamNetworkingSocketsTransport) + $" - connection closed for {param.m_info.m_identityRemote.GetSteamID64()} state: {param.m_info.m_eState}, reason: {param.m_info.m_szEndDebug}");

                    if (!this.isServer)
                        this.LastDisconnectReason = !string.IsNullOrEmpty(param.m_info.m_szEndDebug)
                            ? param.m_info.m_szEndDebug
                            : $"Connection closed: {param.m_info.m_eState}";

                    clientId = param.m_info.m_identityRemote.GetSteamID64();
                    payload = new ArraySegment<byte>();
                    receiveTime = Time.realtimeSinceStartup;

                    this.connectionMapping.Remove(clientId);
                    return NetworkEvent.Disconnect;
                }

                else
                {
                    if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) Debug.Log($"Connection {param.m_info.m_identityRemote.GetSteamID64()} state changed: {param.m_info.m_eState}");
                }
            }

            #endregion

            foreach (SteamConnectionData connectionData in this.connectionMapping.Values)
            {
                IntPtr[] ptrs = new IntPtr[1];
                int messageCount;

                #if UNITY_SERVER
                if ((messageCount = SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(connectionData.connection, ptrs, 1)) > 0)
                    #else
                if ((messageCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(connectionData.connection, ptrs, 1)) > 0)
                    #endif
                {
                    clientId = connectionData.id.m_SteamID;

                    SteamNetworkingMessage_t data = Marshal.PtrToStructure<SteamNetworkingMessage_t>(ptrs[0]);

                    byte[] buffer = new byte[data.m_cbSize - 1];
                    Marshal.Copy(data.m_pData, buffer, 0, data.m_cbSize - 1);

                    payload = new ArraySegment<byte>(buffer);
                    SteamNetworkingMessage_t.Release(ptrs[0]);

                    receiveTime = Time.realtimeSinceStartup;
                    return NetworkEvent.Data;
                }
            }

            payload = new ArraySegment<byte>();
            clientId = 0;
            receiveTime = Time.realtimeSinceStartup;
            return NetworkEvent.Nothing;
        }

        public override void Send(ulong clientId, ArraySegment<byte> segment, NetworkDelivery delivery) {
            if (segment.Array == null) return;

            if (clientId == 0)
            {
                if (this.client == null)
                {
                    if (NetworkManager.Singleton?.LogLevel <= LogLevel.Error) Debug.LogError("Cannot send: serverUser is null");
                    return;
                }

                clientId = this.client.id.m_SteamID;
            }

            if (this.connectionMapping.ContainsKey(clientId))
            {
                HSteamNetConnection connection = this.connectionMapping[clientId].connection;

                byte[] data = new byte[segment.Count + 1];
                Array.Copy(segment.Array, segment.Offset, data, 0, segment.Count);

                data[segment.Count] = Convert.ToByte((int)delivery);

                GCHandle pinnedArray = GCHandle.Alloc(data, GCHandleType.Pinned);
                IntPtr pData = pinnedArray.AddrOfPinnedObject();

                int sendFlag = delivery switch {
                    NetworkDelivery.Reliable or NetworkDelivery.ReliableFragmentedSequenced => Constants.k_nSteamNetworkingSend_Reliable,
                    NetworkDelivery.ReliableSequenced                                       => Constants.k_nSteamNetworkingSend_ReliableNoNagle,
                    NetworkDelivery.UnreliableSequenced                                     => Constants.k_nSteamNetworkingSend_UnreliableNoNagle,
                    var _                                                                   => Constants.k_nSteamNetworkingSend_Unreliable
                };

                #if UNITY_SERVER
                EResult response = SteamGameServerNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data.Length, sendFlag, out long _);
                #else
                EResult response = SteamNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data.Length, sendFlag, out long _);
                #endif

                pinnedArray.Free();

                //If we had some error report that and move on
                if (response is EResult.k_EResultNoConnection or EResult.k_EResultInvalidParam)
                {
                    Debug.LogWarning($"Connection to server was lost -> {response}");

                    string reason = this.pendingDisconnectReasons.GetValueOrDefault(clientId, "Disconnected");
                    this.pendingDisconnectReasons.Remove(clientId);

                    if (!this.isServer) this.LastDisconnectReason = reason;

                    #if UNITY_SERVER
                    SteamGameServerNetworkingSockets.CloseConnection(connection, 0, reason, false);
                    #else
                    SteamNetworkingSockets.CloseConnection(connection, 0, reason, false);
                    #endif
                }
                else if (response != EResult.k_EResultOK
                         && NetworkManager.Singleton.LogLevel <= LogLevel.Error)
                    Debug.LogError($"Could not send: {response}");
            }
            else
            {
                if (NetworkManager.Singleton?.LogLevel > LogLevel.Error) return;

                Debug.LogError("Trying to send on unknown connection: " + clientId);
                NetworkLog.LogErrorServer(nameof(SteamNetworkingSocketsTransport.Send) + " - Trying to send on unknown connection: " + clientId);
            }
        }

        public override void Shutdown() {
            if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) Debug.Log(nameof(SteamNetworkingSocketsTransport.Shutdown));

            if (this.isServer)
            {
                #if UNITY_SERVER
                SteamGameServerNetworkingSockets.CloseListenSocket(listenSocket);
                #else
                SteamNetworkingSockets.CloseListenSocket(this.listenSocket);
                #endif
            }

            this.isServer = false;

            if (NetworkManager.Singleton)
                NetworkManager.Singleton.StartCoroutine(SteamNetworkingSocketsTransport.Delay(0.1f, this.CloseP2PSessions));
            else
                this.CloseP2PSessions();
        }

        public override bool StartClient() {
            this.LastDisconnectReason = null;

            if (this.c_onConnectionChange == null)
                #if UNITY_SERVER
                c_onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);
            #else
                this.c_onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(this.OnConnectionStatusChanged);
            #endif

            this.client = new SteamConnectionData(new CSteamID(this.ConnectToSteamID));

            try
            {
                #if UNITY_SERVER
                SteamGameServerNetworkingUtils.InitRelayNetworkAccess();
                #else
                SteamNetworkingUtils.InitRelayNetworkAccess();
                #endif

                SteamNetworkingIdentity smi = new SteamNetworkingIdentity();
                smi.SetSteamID(this.client.id);

                #if UNITY_SERVER
                this.client.connection = SteamGameServerNetworkingSockets.ConnectP2P(ref smi, 0, options.Length, options);
                #else
                this.client.connection = SteamNetworkingSockets.ConnectP2P(ref smi, 0, this.options.Length, this.options);
                #endif

                this.connectionMapping.Add(this.ConnectToSteamID, this.client);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("Exception: " + ex.Message + ". Client could not be started.");
                this.client = null;
                return false;
            }
        }

        public override bool StartServer() {
            this.pendingDisconnectReasons.Clear();
            this.LastDisconnectReason = null;
            this.isServer = true;

            if (this.c_onConnectionChange == null)
                #if UNITY_SERVER
                c_onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);
            #else
                this.c_onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(this.OnConnectionStatusChanged);
            #endif

            this.options ??= Array.Empty<SteamNetworkingConfigValue_t>();

            #if UNITY_SERVER
            this.listenSocket = SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
            #else
            this.listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, this.options.Length, this.options);
            #endif

            if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) Debug.Log(nameof(SteamNetworkingSocketsTransport.StartServer));

            return true;
        }

        private void CloseP2PSessions() {
            // Close all connections
            foreach (SteamConnectionData user in this.connectionMapping.Values)
            {
                #if UNITY_SERVER
                SteamGameServerNetworkingSockets.CloseConnection(user.connection, 0, "Server shutdown", false);
                #else
                SteamNetworkingSockets.CloseConnection(user.connection, 0, "Server shutdown", false);
                #endif
            }

            this.pendingDisconnectReasons.Clear();
            this.connectionMapping.Clear();
            this.client = null;

            if (NetworkManager.Singleton?.LogLevel <= LogLevel.Developer) Debug.Log(nameof(SteamNetworkingSocketsTransport) + " - CloseP2PSessions - has Closed P2P Sessions With all Users");

            if (this.c_onConnectionChange != null)
            {
                this.c_onConnectionChange.Dispose();
                this.c_onConnectionChange = null;
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t param) {
            this.connectionStatusChangeQueue.Enqueue(param);
        }

        private static IEnumerator Delay(float time, Action action) {
            yield return new WaitForSeconds(time);
            action.Invoke();
        }
    }
}
#endif