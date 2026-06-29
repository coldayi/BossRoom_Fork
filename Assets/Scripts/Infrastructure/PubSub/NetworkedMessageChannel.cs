using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using VContainer;

namespace Unity.BossRoom.Infrastructure
{
    /// <summary>
    /// 【译】这种消息通道允许服务器发布消息时，同时把消息发送给客户端并在本地也发布一份。
    /// 【译】客户端和服务器都可以订阅它。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class NetworkedMessageChannel<T> : MessageChannel<T> where T : unmanaged, INetworkSerializeByMemcpy
    {
        NetworkManager m_NetworkManager;

        string m_Name;

        public NetworkedMessageChannel()
        {
            m_Name = $"{typeof(T).FullName}NetworkMessageChannel";
        }

        [Inject]
        void InjectDependencies(NetworkManager networkManager)
        {
            m_NetworkManager = networkManager;
            // 【译】监听连接事件，这样新客户端进来后也能及时注册接收消息的处理器。
            m_NetworkManager.OnConnectionEvent += OnConnectionEvent;
            if (m_NetworkManager.IsListening)
            {
                RegisterHandler();
            }
        }

        public override void Dispose()
        {
            if (!IsDisposed)
            {
                if (m_NetworkManager != null && m_NetworkManager.CustomMessagingManager != null)
                {
                    m_NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(m_Name);
                    m_NetworkManager.OnConnectionEvent -= OnConnectionEvent;
                }
            }
            base.Dispose();
        }

        void OnConnectionEvent(NetworkManager networkManager, ConnectionEventData connectionEventData)
        {
            if (connectionEventData.EventType == ConnectionEvent.ClientConnected)
            {
                RegisterHandler();
            }
        }

        void RegisterHandler()
        {
            // 【译】服务器负责发送消息，客户端负责接收消息，所以这里只在客户端注册网络回调。
            if (!m_NetworkManager.IsServer)
            {
                m_NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(m_Name, ReceiveMessageThroughNetwork);
            }
        }

        public override void Publish(T message)
        {
            if (m_NetworkManager.IsServer)
            {
                // 【译】服务器先把消息广播给客户端，再在本地也发布一份，保证服务器逻辑能直接订阅到。
                SendMessageThroughNetwork(message);
                base.Publish(message);
            }
            else
            {
                Debug.LogError("Only a server can publish in a NetworkedMessageChannel");
            }
        }

        void SendMessageThroughNetwork(T message)
        {
            // 【译】关服/断线过程中，NetworkManager 可能已经销毁了，所以这里先做空检查，避免退出时报错。
            if (m_NetworkManager == null || m_NetworkManager.CustomMessagingManager == null)
            {
                return;
            }
            var writer = new FastBufferWriter(FastBufferWriter.GetWriteSize<T>(), Allocator.Temp);
            writer.WriteValueSafe(message);
            m_NetworkManager.CustomMessagingManager.SendNamedMessageToAll(m_Name, writer);
        }

        void ReceiveMessageThroughNetwork(ulong clientID, FastBufferReader reader)
        {
            reader.ReadValueSafe(out T message);
            base.Publish(message);
        }
    }
}
