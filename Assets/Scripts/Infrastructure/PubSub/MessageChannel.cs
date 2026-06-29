using System;
using System.Collections.Generic;
using UnityEngine.Assertions;

namespace Unity.BossRoom.Infrastructure
{
    public class MessageChannel<T> : IMessageChannel<T>
    {
        readonly List<Action<T>> m_MessageHandlers = new List<Action<T>>();

        /// 【译】这里先不直接改订阅列表，而是记录“待添加/待移除”的操作。
        /// 【译】这样可以避免在发布消息的过程中，某个回调又把自己从列表里删掉，导致遍历出错。
        readonly Dictionary<Action<T>, bool> m_PendingHandlers = new Dictionary<Action<T>, bool>();

        public bool IsDisposed { get; private set; } = false;

        public virtual void Dispose()
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                m_MessageHandlers.Clear();
                m_PendingHandlers.Clear();
            }
        }

        public virtual void Publish(T message)
        {
            // 【译】先把挂起的订阅变更真正应用到列表里，再开始分发消息。
            foreach (var handler in m_PendingHandlers.Keys)
            {
                if (m_PendingHandlers[handler])
                {
                    m_MessageHandlers.Add(handler);
                }
                else
                {
                    m_MessageHandlers.Remove(handler);
                }
            }
            m_PendingHandlers.Clear();

            foreach (var messageHandler in m_MessageHandlers)
            {
                if (messageHandler != null)
                {
                    messageHandler.Invoke(message);
                }
            }
        }

        public virtual IDisposable Subscribe(Action<T> handler)
        {
            Assert.IsTrue(!IsSubscribed(handler), "Attempting to subscribe with the same handler more than once");

            // 【译】如果这个订阅请求和已有的“待移除”操作冲突，就把它抵消掉。
            if (m_PendingHandlers.ContainsKey(handler))
            {
                if (!m_PendingHandlers[handler])
                {
                    m_PendingHandlers.Remove(handler);
                }
            }
            else
            {
                m_PendingHandlers[handler] = true;
            }

            var subscription = new DisposableSubscription<T>(this, handler);
            return subscription;
        }

        public void Unsubscribe(Action<T> handler)
        {
            if (IsSubscribed(handler))
            {
                // 【译】同样不立刻改列表，而是放进“待处理”队列，等下一次 Publish 再统一生效。
                if (m_PendingHandlers.ContainsKey(handler))
                {
                    if (m_PendingHandlers[handler])
                    {
                        m_PendingHandlers.Remove(handler);
                    }
                }
                else
                {
                    m_PendingHandlers[handler] = false;
                }
            }
        }

        bool IsSubscribed(Action<T> handler)
        {
            var isPendingRemoval = m_PendingHandlers.ContainsKey(handler) && !m_PendingHandlers[handler];
            var isPendingAdding = m_PendingHandlers.ContainsKey(handler) && m_PendingHandlers[handler];
            return m_MessageHandlers.Contains(handler) && !isPendingRemoval || isPendingAdding;
        }
    }
}
