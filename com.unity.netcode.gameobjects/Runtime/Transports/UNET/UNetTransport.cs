#if UNITY_UNET_PRESENT
#pragma warning disable 618 // disable is obsolete
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.Netcode.Transports.UNET
{
    public class UNetTransport : NetworkTransport, ITransportProfilerData
    {
        public enum SendMode
        {
            Immediately,
            Queued
        }

        private static ProfilingDataStore s_TransportProfilerData = new ProfilingDataStore();
        public static bool ProfilerEnabled;

        private int m_UnreliableChannelId;
        private int m_UnreliableSequencedChannelId;
        private int m_ReliableChannelId;
        private int m_ReliableSequencedChannelId;
        private int m_ReliableFragmentedSequencedChannelId;

        // Inspector / settings
        public int MessageBufferSize = 1024 * 5;
        public int MaxConnections = 100;
        public int MaxSentMessageQueueSize = 128;

        public string ConnectAddress = "127.0.0.1";
        public int ConnectPort = 7777;
        public int ServerListenPort = 7777;
        public int ServerWebsocketListenPort = 8887;
        public bool SupportWebsocket = false;

        // user-definable channels.  To add your own channel, do something of the form:
        //  #define MY_CHANNEL 0
        //  ...
        //  transport.Channels.Add(
        //     new UNetChannel()
        //       {
        //         Id = Channel.ChannelUnused + MY_CHANNEL,  <<-- must offset from reserved channel offset in netcode SDK
        //         Type = QosType.Unreliable
        //       }
        //  );
        public List<UNetChannel> Channels = new List<UNetChannel>();

        // Relay
        public bool UseNetcodeRelay = false;
        public string NetcodeRelayAddress = "127.0.0.1";
        public int NetcodeRelayPort = 8888;

        public SendMode MessageSendMode = SendMode.Immediately;

        // Runtime / state
        private byte[] m_MessageBuffer;
        private WeakReference m_TemporaryBufferReference;

        // Lookup / translation
        private readonly Dictionary<NetworkChannel, int> m_ChannelNameToId =
            new Dictionary<NetworkChannel, int>();
        private readonly Dictionary<int, NetworkChannel> m_ChannelIdToName =
            new Dictionary<int, NetworkChannel>();
        private int m_ServerConnectionId;
        private int m_ServerHostId;

        private SocketTask m_ConnectTask;
        public override ulong ServerClientId => GetNetcodeClientId(0, 0, true);

        internal NetworkManager NetworkManager;

#if UNITY_EDITOR
        private void OnValidate()
        {
            for (int i = 0; i < Channels.Count; i++)
            {
                // Set the channels to a incrementing value
                Channels[i].Id = (byte)((byte)NetworkChannel.ChannelUnused + i);
            }
        }
#endif

        protected void LateUpdate()
        {
            if (
                UnityEngine.Networking.NetworkTransport.IsStarted
                && MessageSendMode == SendMode.Queued
            )
            {
#if UNITY_WEBGL
                Debug.LogError("Cannot use queued sending mode for WebGL");
#else
                if (NetworkManager != null && NetworkManager.IsServer)
                {
                    foreach (var targetClient in NetworkManager.Singleton.ConnectedClientsList)
                    {
                        SendQueued(targetClient.ClientId);
                    }
                }
                else
                {
                    SendQueued(NetworkManager.Singleton.LocalClientId);
                }
#endif
            }
        }

        public override void Send(
            ulong clientId,
            ArraySegment<byte> payload,
            NetworkDelivery networkDelivery
        )
        {
            if (ProfilerEnabled)
            {
                s_TransportProfilerData.Increment(ProfilerConstants.NumberOfTransportSends);
            }

            GetUNetConnectionDetails(clientId, out byte hostId, out ushort connectionId);

            byte[] buffer;
            if (payload.Offset > 0)
            {
                // UNET cant handle this, do a copy

                if (m_MessageBuffer.Length >= payload.Count)
                {
                    buffer = m_MessageBuffer;
                }
                else
                {
                    object bufferRef;
                    if (
                        m_TemporaryBufferReference != null
                        && ((bufferRef = m_TemporaryBufferReference.Target) != null)
                        && ((byte[])bufferRef).Length >= payload.Count
                    )
                    {
                        buffer = (byte[])bufferRef;
                    }
                    else
                    {
                        buffer = new byte[payload.Count];
                        m_TemporaryBufferReference = new WeakReference(buffer);
                    }
                }

                Buffer.BlockCopy(payload.Array, payload.Offset, buffer, 0, payload.Count);
            }
            else
            {
                buffer = payload.Array;
            }

            int channelId = -1;
            switch (networkDelivery)
            {
                case NetworkDelivery.Unreliable:
                    channelId = m_UnreliableChannelId;
                    break;
                case NetworkDelivery.UnreliableSequenced:
                    channelId = m_UnreliableSequencedChannelId;
                    break;
                case NetworkDelivery.Reliable:
                    channelId = m_ReliableChannelId;
                    break;
                case NetworkDelivery.ReliableSequenced:
                    channelId = m_ReliableSequencedChannelId;
                    break;
                case NetworkDelivery.ReliableFragmentedSequenced:
                    channelId = m_ReliableFragmentedSequencedChannelId;
                    break;
            }

            if (MessageSendMode == SendMode.Queued)
            {
#if UNITY_WEBGL
                Debug.LogError("Cannot use queued sending mode for WebGL");
#else
                if (UseNetcodeRelay)
                {
                    RelayTransport.QueueMessageForSending(
                        hostId,
                        connectionId,
                        channelId,
                        buffer,
                        payload.Count,
                        out byte error
                    );
                }
                else
                {
                    UnityEngine.Networking.NetworkTransport.QueueMessageForSending(
                        hostId,
                        connectionId,
                        channelId,
                        buffer,
                        payload.Count,
                        out byte error
                    );
                }
#endif
            }
            else
            {
                if (UseNetcodeRelay)
                {
                    RelayTransport.Send(
                        hostId,
                        connectionId,
                        channelId,
                        buffer,
                        payload.Count,
                        out byte error
                    );
                }
                else
                {
                    UnityEngine.Networking.NetworkTransport.Send(
                        hostId,
                        connectionId,
                        channelId,
                        buffer,
                        payload.Count,
                        out byte error
                    );
                }
            }
        }

#if !UNITY_WEBGL
        private void SendQueued(ulong clientId)
        {
            if (ProfilerEnabled)
            {
                s_TransportProfilerData.Increment(ProfilerConstants.NumberOfTransportSendQueues);
            }

            GetUNetConnectionDetails(clientId, out byte hostId, out ushort connectionId);

            if (UseNetcodeRelay)
            {
                RelayTransport.SendQueuedMessages(hostId, connectionId, out byte error);
            }
            else
            {
                UnityEngine.Networking.NetworkTransport.SendQueuedMessages(
                    hostId,
                    connectionId,
                    out _
                );
            }
        }
#endif

        public override NetworkEvent PollEvent(
            out ulong clientId,
            out ArraySegment<byte> payload,
            out float receiveTime
        )
        {
            NetworkEventType eventType;
            int hostId;
            int connectionId;
            byte error;
            int receivedSize;
            int channelId;

            if (UseNetcodeRelay)
            {
                eventType = RelayTransport.Receive(
                    out hostId,
                    out connectionId,
                    out channelId,
                    m_MessageBuffer,
                    m_MessageBuffer.Length,
                    out receivedSize,
                    out error
                );
            }
            else
            {
                eventType = UnityEngine.Networking.NetworkTransport.Receive(
                    out hostId,
                    out connectionId,
                    out channelId,
                    m_MessageBuffer,
                    m_MessageBuffer.Length,
                    out receivedSize,
                    out error
                );
            }

            clientId = GetNetcodeClientId((byte)hostId, (ushort)connectionId, false);
            receiveTime = Time.realtimeSinceStartup;

            var networkError = (NetworkError)error;
            if (networkError == NetworkError.MessageToLong)
            {
                byte[] tempBuffer;

                if (
                    m_TemporaryBufferReference != null
                    && m_TemporaryBufferReference.IsAlive
                    && ((byte[])m_TemporaryBufferReference.Target).Length >= receivedSize
                )
                {
                    tempBuffer = (byte[])m_TemporaryBufferReference.Target;
                }
                else
                {
                    tempBuffer = new byte[receivedSize];
                    m_TemporaryBufferReference = new WeakReference(tempBuffer);
                }

                if (UseNetcodeRelay)
                {
                    eventType = RelayTransport.Receive(
                        out hostId,
                        out connectionId,
                        out channelId,
                        tempBuffer,
                        tempBuffer.Length,
                        out receivedSize,
                        out error
                    );
                }
                else
                {
                    eventType = UnityEngine.Networking.NetworkTransport.Receive(
                        out hostId,
                        out connectionId,
                        out _,
                        tempBuffer,
                        tempBuffer.Length,
                        out receivedSize,
                        out error
                    );
                }

                payload = new ArraySegment<byte>(tempBuffer, 0, receivedSize);
            }
            else
            {
                payload = new ArraySegment<byte>(m_MessageBuffer, 0, receivedSize);
            }

            // if (m_ChannelIdToName.TryGetValue(channelId, out NetworkChannel value))
            // {
            //     networkChannel = value;
            // }
            // else
            // {
            //     networkChannel = NetworkChannel.Internal;
            // }

            if (
                m_ConnectTask != null
                && hostId == m_ServerHostId
                && connectionId == m_ServerConnectionId
            )
            {
                if (eventType == NetworkEventType.ConnectEvent)
                {
                    // We just got a response to our connect request.
                    m_ConnectTask.Message = null;
                    m_ConnectTask.SocketError =
                        networkError == NetworkError.Ok
                            ? System.Net.Sockets.SocketError.Success
                            : System.Net.Sockets.SocketError.SocketError;
                    m_ConnectTask.State = null;
                    m_ConnectTask.Success = networkError == NetworkError.Ok;
                    m_ConnectTask.TransportCode = (byte)networkError;
                    m_ConnectTask.TransportException = null;
                    m_ConnectTask.IsDone = true;

                    m_ConnectTask = null;
                }
                else if (eventType == NetworkEventType.DisconnectEvent)
                {
                    // We just got a response to our connect request.
                    m_ConnectTask.Message = null;
                    m_ConnectTask.SocketError = System.Net.Sockets.SocketError.SocketError;
                    m_ConnectTask.State = null;
                    m_ConnectTask.Success = false;
                    m_ConnectTask.TransportCode = (byte)networkError;
                    m_ConnectTask.TransportException = null;
                    m_ConnectTask.IsDone = true;

                    m_ConnectTask = null;
                }
            }

            if (networkError == NetworkError.Timeout)
            {
                // In UNET. Timeouts are not disconnects. We have to translate that here.
                eventType = NetworkEventType.DisconnectEvent;
            }

            // Translate NetworkEventType to NetEventType
            switch (eventType)
            {
                case NetworkEventType.DataEvent:
                    return NetworkEvent.Data;
                case NetworkEventType.ConnectEvent:
                    return NetworkEvent.Connect;
                case NetworkEventType.DisconnectEvent:
                    return NetworkEvent.Disconnect;
                case NetworkEventType.BroadcastEvent:
                case NetworkEventType.Nothing:
                default:
                    return NetworkEvent.Nothing;
            }
        }

        public override bool StartClient()
        {
            var socketTask = SocketTask.Working;
            byte error;

            if (UseNetcodeRelay)
            {
                m_ServerHostId = RelayTransport.AddHost(new HostTopology(GetConfig(), 1), false);

                m_ServerConnectionId = RelayTransport.Connect(
                    m_ServerHostId,
                    ConnectAddress,
                    ConnectPort,
                    0,
                    out error
                );
            }
            else
            {
                m_ServerHostId = UnityEngine.Networking.NetworkTransport.AddHost(
                    new HostTopology(GetConfig(), 1),
                    0,
                    null
                );
                m_ServerConnectionId = UnityEngine.Networking.NetworkTransport.Connect(
                    m_ServerHostId,
                    ConnectAddress,
                    ConnectPort,
                    0,
                    out error
                );
            }

            // return (NetworkError)error == NetworkError.Ok;
            var connectError = (NetworkError)error;
            switch (connectError)
            {
                case NetworkError.Ok:
                    socketTask.Success = true;
                    socketTask.TransportCode = error;
                    socketTask.SocketError = System.Net.Sockets.SocketError.Success;
                    socketTask.IsDone = false;

                    // We want to continue to wait for the successful connect
                    m_ConnectTask = socketTask;
                    break;
                default:
                    socketTask.Success = false;
                    socketTask.TransportCode = error;
                    socketTask.SocketError = System.Net.Sockets.SocketError.SocketError;
                    socketTask.IsDone = true;
                    break;
            }
            return (NetworkError)error == NetworkError.Ok;
        }

        public override bool StartServer()
        {
            var topology = new HostTopology(GetConfig(), MaxConnections);
            // Undocumented, but AddHost returns -1 in case of any type of failure. See UNET::NetLibraryManager::AddHost

            if (SupportWebsocket)
            {
                if (!UseNetcodeRelay)
                {
                    int websocketHostId = UnityEngine.Networking.NetworkTransport.AddWebsocketHost(
                        topology,
                        ServerWebsocketListenPort
                    );
                }
                else
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    {
                        NetworkLog.LogError(
                            "Cannot create websocket host when using Unity.Netcode relay"
                        );
                    }
                }
            }

            if (UseNetcodeRelay)
            {
                return -1 != RelayTransport.AddHost(topology, ServerListenPort, true);
            }
            else
            {
                return -1
                    != UnityEngine.Networking.NetworkTransport.AddHost(
                        topology,
                        ServerListenPort,
                        null
                    );
            }
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            GetUNetConnectionDetails(clientId, out byte hostId, out ushort connectionId);

            if (UseNetcodeRelay)
            {
                RelayTransport.Disconnect((int)hostId, (int)connectionId, out byte error);
            }
            else
            {
                UnityEngine.Networking.NetworkTransport.Disconnect(
                    (int)hostId,
                    (int)connectionId,
                    out byte error
                );
            }
        }

        public override void DisconnectLocalClient()
        {
            if (UseNetcodeRelay)
            {
                RelayTransport.Disconnect(m_ServerHostId, m_ServerConnectionId, out byte error);
            }
            else
            {
                UnityEngine.Networking.NetworkTransport.Disconnect(
                    m_ServerHostId,
                    m_ServerConnectionId,
                    out byte error
                );
            }
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            GetUNetConnectionDetails(clientId, out byte hostId, out ushort connectionId);

            if (UseNetcodeRelay)
            {
                return 0;
            }
            else
            {
                return (ulong)
                    UnityEngine.Networking.NetworkTransport.GetCurrentRTT(
                        (int)hostId,
                        (int)connectionId,
                        out byte error
                    );
            }
        }

        public override void Shutdown()
        {
            m_ChannelIdToName.Clear();
            m_ChannelNameToId.Clear();
            UnityEngine.Networking.NetworkTransport.Shutdown();
        }

        public override void Initialize(NetworkManager networkManager = null)
        {
            UpdateRelay();

            NetworkManager = networkManager;

            m_MessageBuffer = new byte[MessageBufferSize];

            s_TransportProfilerData.Clear();
            UnityEngine.Networking.NetworkTransport.Init();
        }

        private ulong GetNetcodeClientId(byte hostId, ushort connectionId, bool isServer)
        {
            if (isServer)
            {
                return 0;
            }

            return (connectionId | (ulong)hostId << 16) + 1;
        }

        private void GetUNetConnectionDetails(
            ulong clientId,
            out byte hostId,
            out ushort connectionId
        )
        {
            if (clientId == 0)
            {
                hostId = (byte)m_ServerHostId;
                connectionId = (ushort)m_ServerConnectionId;
            }
            else
            {
                hostId = (byte)((clientId - 1) >> 16);
                connectionId = (ushort)((clientId - 1));
            }
        }

        private ConnectionConfig GetConfig()
        {
            var connectionConfig = new ConnectionConfig();

            if (UseNetcodeRelay)
            {
                // Built-in netcode channels
                for (int i = 0; i < NETCODE_CHANNELS.Length; i++)
                {
                    int channelId = AddNetcodeChannel(
                        NETCODE_CHANNELS[i].Delivery,
                        connectionConfig
                    );

                    m_ChannelIdToName.Add(channelId, NETCODE_CHANNELS[i].Channel);
                    m_ChannelNameToId.Add(NETCODE_CHANNELS[i].Channel, channelId);
                }

                // Custom user-added channels
                for (int i = 0; i < Channels.Count; i++)
                {
                    int channelId = AddUNETChannel(Channels[i].Type, connectionConfig);

                    if (m_ChannelNameToId.ContainsKey((NetworkChannel)Channels[i].Id))
                    {
                        throw new InvalidChannelException($"Channel {channelId} already exists");
                    }

                    m_ChannelIdToName.Add(channelId, (NetworkChannel)Channels[i].Id);
                    m_ChannelNameToId.Add((NetworkChannel)Channels[i].Id, channelId);
                }
            }
            else
            {
                m_UnreliableChannelId = connectionConfig.AddChannel(QosType.Unreliable);
                m_UnreliableSequencedChannelId = connectionConfig.AddChannel(
                    QosType.UnreliableSequenced
                );
                m_ReliableChannelId = connectionConfig.AddChannel(QosType.Reliable);
                m_ReliableSequencedChannelId = connectionConfig.AddChannel(
                    QosType.ReliableSequenced
                );
                m_ReliableFragmentedSequencedChannelId = connectionConfig.AddChannel(
                    QosType.ReliableFragmentedSequenced
                );
            }

            connectionConfig.MaxSentMessageQueueSize = (ushort)MaxSentMessageQueueSize;

            return connectionConfig;
        }

        public int AddNetcodeChannel(NetworkDelivery type, ConnectionConfig config)
        {
            switch (type)
            {
                case NetworkDelivery.Unreliable:
                    return config.AddChannel(QosType.Unreliable);
                case NetworkDelivery.Reliable:
                    return config.AddChannel(QosType.Reliable);
                case NetworkDelivery.ReliableSequenced:
                    return config.AddChannel(QosType.ReliableSequenced);
                case NetworkDelivery.ReliableFragmentedSequenced:
                    return config.AddChannel(QosType.ReliableFragmentedSequenced);
                case NetworkDelivery.UnreliableSequenced:
                    return config.AddChannel(QosType.UnreliableSequenced);
            }

            return 0;
        }

        public int AddUNETChannel(QosType type, ConnectionConfig config)
        {
            switch (type)
            {
                case QosType.Unreliable:
                    return config.AddChannel(QosType.Unreliable);
                case QosType.UnreliableFragmented:
                    return config.AddChannel(QosType.UnreliableFragmented);
                case QosType.UnreliableSequenced:
                    return config.AddChannel(QosType.UnreliableSequenced);
                case QosType.Reliable:
                    return config.AddChannel(QosType.Reliable);
                case QosType.ReliableFragmented:
                    return config.AddChannel(QosType.ReliableFragmented);
                case QosType.ReliableSequenced:
                    return config.AddChannel(QosType.ReliableSequenced);
                case QosType.StateUpdate:
                    return config.AddChannel(QosType.StateUpdate);
                case QosType.ReliableStateUpdate:
                    return config.AddChannel(QosType.ReliableStateUpdate);
                case QosType.AllCostDelivery:
                    return config.AddChannel(QosType.AllCostDelivery);
                case QosType.UnreliableFragmentedSequenced:
                    return config.AddChannel(QosType.UnreliableFragmentedSequenced);
                case QosType.ReliableFragmentedSequenced:
                    return config.AddChannel(QosType.ReliableFragmentedSequenced);
            }

            return 0;
        }

        private void UpdateRelay()
        {
            RelayTransport.Enabled = UseNetcodeRelay;
            RelayTransport.RelayAddress = NetcodeRelayAddress;
            RelayTransport.RelayPort = (ushort)NetcodeRelayPort;
        }

        public void BeginNewTick()
        {
            s_TransportProfilerData.Clear();
        }

        public IReadOnlyDictionary<string, int> GetTransportProfilerData()
        {
            return s_TransportProfilerData.GetReadonly();
        }
    }
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
#pragma warning restore 618 // restore is obsolete
#endif
