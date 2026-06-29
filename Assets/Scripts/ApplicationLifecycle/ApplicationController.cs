using System;
using System.Collections;
using Unity.BossRoom.ApplicationLifecycle.Messages;
using Unity.BossRoom.ConnectionManagement;
using Unity.BossRoom.Gameplay.GameState;
using Unity.BossRoom.Gameplay.Messages;
using Unity.BossRoom.Infrastructure;
using Unity.BossRoom.UnityServices;
using Unity.BossRoom.UnityServices.Auth;
using Unity.BossRoom.UnityServices.Sessions;
using Unity.BossRoom.Utils;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer;
using VContainer.Unity;

namespace Unity.BossRoom.ApplicationLifecycle
{
    /// <summary>
    /// 【译】应用程序的入口点，在这里把所有通用依赖绑定到根 DI 容器中。
    /// </summary>
    public class ApplicationController : LifetimeScope
    {
        [SerializeField]
        UpdateRunner m_UpdateRunner;
        [SerializeField]
        ConnectionManager m_ConnectionManager;
        [SerializeField]
        NetworkManager m_NetworkManager;

        LocalSession m_LocalSession;
        MultiplayerServicesFacade m_MultiplayerServicesFacade;

        IDisposable m_Subscriptions;

        protected override void Configure(IContainerBuilder builder)
        {
            base.Configure(builder);
            // 这里是整个应用的“根 DI 容器”，负责注册跨场景都要用到的核心对象。
            builder.RegisterComponent(m_UpdateRunner);
            builder.RegisterComponent(m_ConnectionManager);
            builder.RegisterComponent(m_NetworkManager);

            // 这些单例代表当前登录用户和当前会话，它们要比某个具体 UI 场景活得更久。
            builder.Register<LocalSessionUser>(Lifetime.Singleton);
            builder.Register<LocalSession>(Lifetime.Singleton);

            builder.Register<ProfileManager>(Lifetime.Singleton);

            builder.Register<PersistentGameState>(Lifetime.Singleton);

            // 这些消息通道会贯穿整个会话生命周期，因此直接注册为实例。
            builder.RegisterInstance(new MessageChannel<QuitApplicationMessage>()).AsImplementedInterfaces();
            builder.RegisterInstance(new MessageChannel<UnityServiceErrorMessage>()).AsImplementedInterfaces();
            builder.RegisterInstance(new MessageChannel<ConnectStatus>()).AsImplementedInterfaces();
            builder.RegisterInstance(new MessageChannel<DoorStateChangedEventMessage>()).AsImplementedInterfaces();

            // 网络消息通道会把服务器发布的消息同步到客户端，方便双方都能订阅同一类事件。
            builder.RegisterComponent(new NetworkedMessageChannel<LifeStateChangedEventMessage>()).AsImplementedInterfaces();
            builder.RegisterComponent(new NetworkedMessageChannel<ConnectionEventMessage>()).AsImplementedInterfaces();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            builder.RegisterComponent(new NetworkedMessageChannel<CheatUsedMessage>()).AsImplementedInterfaces();
#endif

            // 断线重连用的消息通道
            builder.RegisterInstance(new MessageChannel<ReconnectMessage>()).AsImplementedInterfaces();

            // 带缓存的消息通道会记住最新一条消息，后来的订阅者也能立刻拿到当前状态
            builder.RegisterInstance(new BufferedMessageChannel<SessionListFetchedMessage>()).AsImplementedInterfaces();

            // 会话/认证相关服务也放在根容器里，这样切场景后还在
            builder.Register<AuthenticationServiceFacade>(Lifetime.Singleton); // 用于匿名登录 Unity Services

            // 【译】把 MultiplayerServicesFacade 注册为入口点，因为它需要在容器构建完成后再执行初始化回调。
            builder.RegisterEntryPoint<MultiplayerServicesFacade>(Lifetime.Singleton).AsSelf();
        }

        private void Start()
        {
            // 进入游戏后，先拿到一些全局服务，再订阅退出事件
            m_LocalSession = Container.Resolve<LocalSession>();
            m_MultiplayerServicesFacade = Container.Resolve<MultiplayerServicesFacade>();

            var quitApplicationSub = Container.Resolve<ISubscriber<QuitApplicationMessage>>();

            var subHandles = new DisposableGroup();
            subHandles.Add(quitApplicationSub.Subscribe(QuitGame));
            m_Subscriptions = subHandles;

            Application.wantsToQuit += OnWantToQuit;
            DontDestroyOnLoad(gameObject);
            DontDestroyOnLoad(m_UpdateRunner.gameObject);
            // 这里把目标帧率固定到 120，避免不同机器上表现差异过大
            Application.targetFrameRate = 120;
            // 启动后直接进入主菜单场景
            SceneManager.LoadScene("MainMenu");
        }

        protected override void OnDestroy()
        {
            if (m_Subscriptions != null)
            {
                m_Subscriptions.Dispose();
            }

            if (m_MultiplayerServicesFacade != null)
            {
                m_MultiplayerServicesFacade.EndTracking();
            }

            base.OnDestroy();
        }

        /// <summary>
        ///     In builds, if we are in a Session and try to send a Leave request on application quit, it won't go through if we're quitting on the same frame.
        ///     So, we need to delay just briefly to let the request happen (though we don't need to wait for the result).
        /// </summary>
        private IEnumerator LeaveBeforeQuit()
        {
            // 退出前先尝试离开会话；即使失败，也不要阻止应用继续退出
            try
            {
                m_MultiplayerServicesFacade.EndTracking();
            }
            catch (Exception e)
            {
                Debug.LogError(e.Message);
            }

            yield return null;
            Application.Quit();
        }

        private bool OnWantToQuit()
        {
            Application.wantsToQuit -= OnWantToQuit;

            var canQuit = m_LocalSession != null && string.IsNullOrEmpty(m_LocalSession.SessionID);
            if (!canQuit)
            {
                // 如果还在会话中，先异步发送 Leave 请求，再真正退出
                StartCoroutine(LeaveBeforeQuit());
            }

            return canQuit;
        }

        private void QuitGame(QuitApplicationMessage msg)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
