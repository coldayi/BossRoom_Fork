using System;
using UnityEngine;
using VContainer.Unity;

namespace Unity.BossRoom.Gameplay.GameState
{
    public enum GameState
    {
        MainMenu,
        CharSelect,
        BossRoom,
        PostGame
    }

    /// <summary>
    /// 【译】一个表示离散游戏状态及其依赖项的特殊组件。
    /// 【译】它提供的一个关键保障是：同一时间只会有一个这样的 GameState 在运行。
    /// </summary>
    /// <remarks>
    /// 【译】Q：GameState 和 Scene 之间是什么关系？
    /// 【译】A：它们是 1 对多关系。也就是说，每个场景只对应一个状态，但同一个状态可以存在于多个场景中。
    /// 【译】Q：状态切换是怎么发生的？
    /// 【译】A：它们由服务器代码中调用 NetworkManager.SceneManager.LoadScene 间接驱动。
    /// 【译】这样做很重要，因为如果状态切换和场景切换分开驱动，那么关心场景的状态就需要非常小心地和场景加载同步逻辑。
    /// 【译】Q：GameStateBehaviours 一共有几个？
    /// 【译】A：服务器上一个，客户端上一个（在 Host 模式下，服务器和客户端的 GameStateBehaviour 会像其他网络预制体一样同时运行）。
    /// 【译】Q：既然它是 MonoBehaviour，怎么做到跨多个场景只保留一个状态？
    /// 【译】A：把 Persists 属性设为 true。这样当切换到另一个拥有相同游戏状态的场景时，当前的 GameState 对象会被保留，新场景里的那个版本会自动销毁来让位。
    ///
    /// 【译】重要说明：我们假设每个场景都包含一个 GameState 对象。
    /// 【译】如果不是这样，那么一个持续存在的游戏状态可能会超出它的生命周期，因为没有后继状态去清理它。
    /// </remarks>
    public abstract class GameStateBehaviour : LifetimeScope
    {
        /// <summary>
        /// 【译】这个 GameState 是否会跨多个场景持续存在？
        /// </summary>
        public virtual bool Persists
        {
            get { return false; }
        }

        /// <summary>
        /// 【译】这个对象代表哪一种 GameState。服务器和客户端版的同一状态应当返回相同的枚举值。
        /// </summary>
        public abstract GameState ActiveState { get; }

        /// <summary>
        /// 当前场景里真正“生效”的 GameState。用静态引用保证同一时间只有一个在运行。
        /// </summary>
        private static GameObject s_ActiveStateGO;

        protected override void Awake()
        {
            base.Awake();

            if (Parent != null)
            {
                Parent.Container.Inject(this);
            }
        }

        // 【译】Start 会在第一帧更新前调用。
        protected virtual void Start()
        {
            if (s_ActiveStateGO != null)
            {
                if (s_ActiveStateGO == gameObject)
                {
                    // 【译】如果我们已经是当前活跃状态对象，就什么都不用做。
                    return;
                }

                // 在 Host 上，这里可能拿到客户端版或服务端版，但我们只关心它是什么类型，以及它是否需要常驻。
                var previousState = s_ActiveStateGO.GetComponent<GameStateBehaviour>();

                if (previousState.Persists && previousState.ActiveState == ActiveState)
                {
                    // 【译】如果已经有一个“常驻”的同类状态存在，就销毁当前这个新实例，避免重复。
                    Destroy(gameObject);
                    return;
                }

                // 【译】否则旧状态要被新状态替换掉：要么旧状态不常驻，要么它属于别的游戏阶段。
                Destroy(s_ActiveStateGO);
            }

            s_ActiveStateGO = gameObject;
            if (Persists)
            {
                // 【译】某些状态要跨场景保留，例如主菜单或角色选择状态。
                DontDestroyOnLoad(gameObject);
            }
        }

        protected override void OnDestroy()
        {
            if (!Persists)
            {
                s_ActiveStateGO = null;
            }
        }
    }
}
