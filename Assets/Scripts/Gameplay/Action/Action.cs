using System;
using System.Collections.Generic;
using Unity.BossRoom.Gameplay.GameplayObjects;
using Unity.BossRoom.Gameplay.GameplayObjects.Character;
using Unity.BossRoom.VisualEffects;
using Unity.Netcode;
using UnityEngine;
using BlockingMode = Unity.BossRoom.Gameplay.Actions.BlockingModeType;

namespace Unity.BossRoom.Gameplay.Actions
{
    /// <summary>
    /// 【译】所有 Action 的抽象父类。
    /// </summary>
    /// <remarks>
    /// 【译】Action 系统是一个让角色以网络同步方式“做事情”的通用机制。
    /// 【译】Action 可以是普通攻击、像 Archer 的 Volley Shot 这样的技能，也可以是拉拉杆子这种更普通的交互。
    /// 【译】每个 ActionLogic 枚举值都会对应这个类的一个具体实现。
    /// 【译】角色同一时间只能有一个正在生效的 Action（也叫 blocking action），但队列里可以同时存在多个 Action。
    /// 【译】后续 Action 会排在当前动作后面，同时还可能有“非阻塞”的动作在后台运行。可以去看 ActionPlayer.cs。
    ///
    /// 【译】Action 的生命周期大致是：
    /// 【译】开始时：Start()
    /// 【译】每帧：如果当前是阻塞动作，就先调用 ShouldBecomeNonBlocking()，然后调用 Update()
    /// 【译】结束时：End() 或 Cancel()
    /// 【译】结束后：ChainIntoNewAction()（仅当它是阻塞动作，并且是 End() 结束，不是 Cancel() 结束时才会调用）
    ///
    /// 【译】还要注意，如果 Start() 返回 false，后续任何函数都不会再被调用，连 End() 也不会。
    ///
    /// 【译】这个 Action 系统并不是为复用到其他项目而设计的通用框架，阅读时要注意这一点。
    /// 【译】更好的动作系统应该更方便设计师使用和扩展，能定义更小的原子动作步骤，并且能更通用地定义和读取角色数据。
    /// 【译】它也应该更高性能，因为动作数量会随着角色数量和并发动作数一起增长。
    /// </remarks>
    public abstract class Action : ScriptableObject
    {
        /// <summary>
        /// 【译】指向 GameDataSource 中动作原型数组的索引，由 GameDataSource 在运行时设置。
        /// 【译】如果这个 Action 本身不是原型，那么这里会保存它所引用的原型 id。
        /// 【译】这个字段会被用来以可网络传输的方式标识动作。
        /// </summary>
        [NonSerialized]
        public ActionID ActionID;

        /// <summary>
        /// 【译】默认的受击反应动画，多个不同的 ActionFX 都会用到它。
        /// </summary>
        public const string k_DefaultHitReact = "HitReact1";


        protected ActionRequestData m_Data;

        /// <summary>
        /// 【译】这个 Action 开始的时间点（Time.time，单位秒），由 ActionPlayer 或 ActionVisualization 设定。
        /// </summary>
        public float TimeStarted { get; set; }

        /// <summary>
        /// 【译】这个 Action 已经运行了多久（从调用 Start 开始计时），单位秒，使用 Time.time 计算。
        /// </summary>
        public float TimeRunning { get { return (Time.time - TimeStarted); } }

        /// <summary>
        /// 【译】创建这个 Action 时传入的请求数据，应该当作只读使用。
        /// </summary>
        // 【译】动作启动后会把这份请求数据保存起来，后续运行时只读使用，避免中途被外部改掉。
        public ref ActionRequestData Data => ref m_Data;

        /// <summary>
        /// 【译】这个动作的数据描述。
        /// </summary>
        public ActionConfig Config;

        public bool IsChaseAction => ActionID == GameDataSource.Instance.GeneralChaseActionPrototype.ActionID;
        public bool IsStunAction => ActionID == GameDataSource.Instance.StunnedActionPrototype.ActionID;
        public bool IsGeneralTargetAction => ActionID == GameDataSource.Instance.GeneralTargetActionPrototype.ActionID;

        /// <summary>
        /// 【译】构造初始化。
        /// 【译】传入的 data 参数在进入这个方法后不应再被外部保留，因为这里会接管它内部的内存。
        /// 【译】这个方法需要由 ActionFactory 调用。
        /// </summary>
        public void Initialize(ref ActionRequestData data)
        {
            m_Data = data;
            ActionID = data.ActionID;
        }

        /// <summary>
        /// 【译】在把动作对象放回对象池之前，先重置它的状态。
        /// </summary>
        public virtual void Reset()
        {
            m_Data = default;
            ActionID = default;
            TimeStarted = 0;
        }

        /// <summary>
        /// 【译】当 Action 真正开始播放时调用（由于排队，它可能晚于创建时刻）。
        /// </summary>
        /// <returns>【译】如果这个动作最终决定不执行了就返回 false，否则返回 true。</returns>
        public abstract bool OnStart(ServerCharacter serverCharacter);


        /// <summary>
        /// 【译】动作运行期间每帧都会调用。
        /// </summary>
        /// <returns>【译】返回 true 表示继续运行，返回 false 表示停止。如果动作设置了持续时间，到期后会默认停止。</returns>
        public abstract bool OnUpdate(ServerCharacter clientCharacter);

        /// <summary>
        /// 【译】对当前活跃的“阻塞动作”来说，每帧会先调用这个函数，询问它是否应该转入后台。
        /// </summary>
        /// <returns>【译】返回 true 表示转为非阻塞动作，返回 false 表示继续保持阻塞状态。</returns>
        public virtual bool ShouldBecomeNonBlocking()
        {
            // 某些动作只在“准备阶段”阻塞，执行完关键动作后就可以转入后台继续播放效果。
            return Config.BlockingMode == BlockingModeType.OnlyDuringExecTime ? TimeRunning >= Config.ExecTimeSeconds : false;
        }

        /// <summary>
        /// 【译】当 Action 自然结束时调用。默认实现只是调用 Cancel()。
        /// </summary>
        public virtual void End(ServerCharacter serverCharacter)
        {
            Cancel(serverCharacter);
        }

        /// <summary>
        /// 【译】当 Action 被取消时会调用这里。
        /// 【译】动作应该在这里清理所有仍在进行中的效果。
        /// 【译】例如，涉及移动的动作应该取消当前正在进行的移动。
        /// </summary>
        public virtual void Cancel(ServerCharacter serverCharacter) { }

        /// <summary>
        /// 【译】在 End() 之后调用。
        /// 【译】此时 Action 已经结束，意味着它的 Update() 等函数将不再被调用。
        /// 【译】如果这个 Action 想立刻切换到另一个 Action，可以在这里做。
        /// 【译】新的 Action 会在下一次 Update() 中生效。
        ///
        /// 【译】注意：这个方法不会在“提前被取消”的 Action 上调用，只有 End() 正常结束的动作才会走到这里。
        /// </summary>
        /// <param name="newAction">【译】要立即切换到的新 Action</param>
        /// <returns>【译】如果有新的动作则返回 true，否则返回 false</returns>
        public virtual bool ChainIntoNewAction(ref ActionRequestData newAction) { return false; }

        /// <summary>
        /// 【译】当角色与其他物体发生碰撞时，会调用当前活跃的“阻塞动作”。
        /// </summary>
        /// <param name="serverCharacter"></param>
        /// <param name="collision"></param>
        public virtual void CollisionEntered(ServerCharacter serverCharacter, Collision collision) { }

        public enum BuffableValue
        {
            PercentHealingReceived, // 【译】未加成时是 1.0，0 表示不回血，2 表示治疗翻倍
            PercentDamageReceived,  // 【译】未加成时是 1.0，0 表示免伤，2 表示受到双倍伤害
            ChanceToStunTramplers,  // 【译】未加成时是 0，如果大于 0，则表示被踩踏时有多大概率让踩踏者眩晕
        }

        /// <summary>
        /// 【译】让所有活跃中的 Action 都有机会改变某个游戏计算结果。
        /// 【译】这里既会处理正面效果（buff），也会处理负面效果（debuff）。
        /// </summary>
        /// <remarks>
        /// 【译】如果游戏更复杂、buff/debuff 更多，这个函数可能会被单独的 BuffRegistry 组件取代。
        /// 【译】那样可以加入更高级的特性，比如定义哪些效果可以“叠加”，并显示每个角色当前受哪些效果影响以及持续多久。
        /// </remarks>
        /// <param name="buffType">【译】当前正在计算哪一种游戏变量</param>
        /// <param name="orgValue">【译】原始的（未加成的）数值</param>
        /// <param name="buffedValue">【译】最终的（已加成的）数值</param>
        public virtual void BuffValue(BuffableValue buffType, ref float buffedValue) { }

        /// <summary>
        /// 【译】返回某个 BuffableValue 的默认（未加成）值的静态工具函数。
        /// 【译】这样做只是为了让这些常量集中在一个地方。
        /// </summary>
        public static float GetUnbuffedValue(Action.BuffableValue buffType)
        {
            switch (buffType)
            {
                case BuffableValue.PercentDamageReceived: return 1;
                case BuffableValue.PercentHealingReceived: return 1;
                case BuffableValue.ChanceToStunTramplers: return 0;
                default: throw new System.Exception($"Unknown buff type {buffType}");
            }
        }

        public enum GameplayActivity
        {
            AttackedByEnemy,
            Healed,
            StoppedChargingUp,
            UsingAttackAction, // 【译】在真正执行攻击 Action 之前立即调用
        }

        /// <summary>
        /// 【译】当有重要的游戏事件发生时，会通知当前活跃的 Action。
        /// </summary>
        /// <remarks>
        /// 【译】当 GameplayActivity 为 AttackedByEnemy 或 Healed 时，OnGameplayAction() 会在 BuffValue() 之前被调用。
        /// </remarks>
        /// <param name="serverCharacter"></param>
        /// <param name="activityType"></param>
        public virtual void OnGameplayActivity(ServerCharacter serverCharacter, GameplayActivity activityType) { }



        /// <summary>
        /// 【译】如果这个 actionFX 在服务器确认之前就已经立刻开始运行，则为 true。
        /// </summary>
        public bool AnticipatedClient { get; protected set; }

        /// <summary>
        /// 【译】开始播放 ActionFX。派生类如果想立刻结束而不进入 Update，可以返回 false。
        /// </summary>
        /// <remarks>
        /// 【译】派生类在实现时应该记得调用 base.OnStart()，但要注意这会把“预判状态”重置为 false。
        /// </remarks>
        /// <returns>【译】返回 true 表示继续播放，返回 false 表示立刻清理。</returns>
        public virtual bool OnStartClient(ClientCharacter clientCharacter)
        {
            // 客户端真正开始播放动作时，就不再把它当成“预判中的动作”了。
            AnticipatedClient = false; //once you start for real you are no longer an anticipated action.
            TimeStarted = UnityEngine.Time.time;
            return true;
        }

        public virtual bool OnUpdateClient(ClientCharacter clientCharacter)
        {
            return ActionConclusion.Continue;
        }
        /// <summary>
        /// 【译】当 ActionFX 播放完毕时总会调用 End。
        /// 【译】这很适合派生类放收尾逻辑，比如持续火焰 AOE 消失时播放一团烟雾的效果。
        /// 【译】派生类不一定要调用 base.End()；默认实现只是调用 Cancel，用来处理 Cancel 和 End 行为相同的常见情况。
        /// </summary>
        public virtual void EndClient(ClientCharacter clientCharacter)
        {
            CancelClient(clientCharacter);
        }

        /// <summary>
        /// 【译】当 ActionFX 被提前打断时会调用 Cancel。
        /// 【译】它和 End 在逻辑上是分开的，因为某些动作在“中断”和“正常完成”时可能想播放不同内容。
        /// 【译】例如，ChargeShot 可能在 End 时发射投射物，但在 Cancel 时改播一个“踉跄”动画。
        /// </summary>
        public virtual void CancelClient(ClientCharacter clientCharacter) { }

        /// <summary>
        /// 【译】这个 ActionFX 是否应该在拥有者客户端上提前创建？
        /// </summary>
        /// <param name="clientCharacter">【译】将要播放这个 ActionFX 的 ActionVisualization。</param>
        /// <param name="data">【译】发送给服务器的请求</param>
        /// <returns>【译】如果返回 true，ActionVisualization 就应该在收到服务器回复前先预创建这个 ActionFX。</returns>
        public static bool ShouldClientAnticipate(ClientCharacter clientCharacter, ref ActionRequestData data)
        {
            if (!clientCharacter.CanPerformActions) { return false; }

            var actionDescription = GameDataSource.Instance.GetActionPrototypeByID(data.ActionID).Config;

            // 对于需要先靠近目标的动作，客户端先本地判断距离。
            // 如果距离不够，就不要提前播放，因为服务器会先合成一个 ChaseAction 去追过去。
            bool isTargetEligible = true;
            if (data.ShouldClose == true)
            {
                ulong targetId = (data.TargetIds != null && data.TargetIds.Length > 0) ? data.TargetIds[0] : 0;
                if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(targetId, out NetworkObject networkObject))
                {
                    float rangeSquared = actionDescription.Range * actionDescription.Range;
                    isTargetEligible = (networkObject.transform.position - clientCharacter.transform.position).sqrMagnitude < rangeSquared;
                }
            }

            // 目前大部分动作都允许客户端先预演，只有 Target 动作自己负责预判逻辑。
            return isTargetEligible && actionDescription.Logic != ActionLogic.Target;
        }

        /// <summary>
        /// 【译】当可视化对象收到动画事件时调用。
        /// </summary>
        public virtual void OnAnimEventClient(ClientCharacter clientCharacter, string id) { }

        /// <summary>
        /// 【译】当这个动作完成“蓄力”时调用。
        /// 【译】这只对少数几种动作有意义，其他动作不会调用这个方法。
        /// </summary>
        /// <param name="finalChargeUpPercentage">【译】最终蓄力百分比</param>
        public virtual void OnStoppedChargingUpClient(ClientCharacter clientCharacter, float finalChargeUpPercentage) { }

        /// <summary>
        /// 【译】实例化 Spawns 列表中的所有图形对象的工具函数。
        /// 【译】如果 parentToOrigin 为 true，新生成的图形会挂到 origin Transform 下面。
        /// 【译】如果为 false，它们会保持相同的位置和朝向，但不会成为子物体。
        /// </summary>
        protected List<SpecialFXGraphic> InstantiateSpecialFXGraphics(Transform origin, bool parentToOrigin)
        {
            var returnList = new List<SpecialFXGraphic>();
            foreach (var prefab in Config.Spawns)
            {
                if (!prefab) { continue; } // 【译】跳过 prefab 列表中的空项
                returnList.Add(InstantiateSpecialFXGraphic(prefab, origin, parentToOrigin));
            }
            return returnList;
        }

        /// <summary>
        /// 【译】实例化 Spawns 列表中某一个图形对象的工具函数。
        /// 【译】如果 parentToOrigin 为 true，新图形会挂到 origin Transform 下面。
        /// 【译】如果为 false，它们会保持相同的位置和朝向，但不会成为子物体。
        /// </summary>
        protected SpecialFXGraphic InstantiateSpecialFXGraphic(GameObject prefab, Transform origin, bool parentToOrigin)
        {
            if (prefab.GetComponent<SpecialFXGraphic>() == null)
            {
                throw new System.Exception($"One of the Spawns on action {this.name} does not have a SpecialFXGraphic component and can't be instantiated!");
            }
            var graphicsGO = GameObject.Instantiate(prefab, origin.transform.position, origin.transform.rotation, (parentToOrigin ? origin.transform : null));
            return graphicsGO.GetComponent<SpecialFXGraphic>();
        }

        /// <summary>
        /// 【译】当动作在客户端处于“预判”状态时调用。
        /// 【译】例如，如果你是坦克玩家并挥动锤子，这个调用会在客户端立刻触发，早于服务器往返确认。
        /// 【译】重写这个方法时应该始终调用基类实现。
        /// </summary>
        public virtual void AnticipateActionClient(ClientCharacter clientCharacter)
        {
            AnticipatedClient = true;
            TimeStarted = UnityEngine.Time.time;

            if (!string.IsNullOrEmpty(Config.AnimAnticipation))
            {
                clientCharacter.OurAnimator.SetTrigger(Config.AnimAnticipation);
            }
        }

    }
}
