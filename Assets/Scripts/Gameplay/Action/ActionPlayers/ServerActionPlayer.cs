using System.Collections.Generic;
using Unity.BossRoom.Gameplay.GameplayObjects;
using Unity.BossRoom.Gameplay.GameplayObjects.Character;
using UnityEngine;
using UnityEngine.Pool;

namespace Unity.BossRoom.Gameplay.Actions
{
    /// <summary>
    /// 【译】负责把玩家输入转换并播放成一连串动作的类。
    /// </summary>
    public class ServerActionPlayer
    {
        private ServerCharacter m_ServerCharacter;

        private ServerCharacterMovement m_Movement;

        // 【译】blocking queue：当前正在执行、会占用角色控制权的动作。
        private List<Action> m_Queue;

        // 【译】background actions：已经进入后台播放，但还在持续更新的动作。
        private List<Action> m_NonBlockingActions;

        // 记录每个动作上次使用的时间，防止玩家无限连发同一个技能
        private Dictionary<ActionID, float> m_LastUsedTimestamps;

        /// <summary>
        /// 【译】为了防止动作队列无限增长，我们把它的预计播放时间限制在这个秒数内。
        /// 【译】由于动作可能会无限期阻塞，所以这里只能估算队列长度。
        /// 【译】但这个估算依然很有用，可以避免堆积太多小动作。
        /// </summary>
        private const float k_MaxQueueTimeDepth = 1.6f;

        private ActionRequestData m_PendingSynthesizedAction = new ActionRequestData();
        private bool m_HasPendingSynthesizedAction;

        public ServerActionPlayer(ServerCharacter serverCharacter)
        {
            m_ServerCharacter = serverCharacter;
            m_Movement = serverCharacter.Movement;
            m_Queue = new List<Action>();
            m_NonBlockingActions = new List<Action>();
            m_LastUsedTimestamps = new Dictionary<ActionID, float>();
        }

        /// <summary>
        /// 【译】执行一串动作。
        /// </summary>
        public void PlayAction(ref ActionRequestData action)
        {
            if (!action.ShouldQueue && m_Queue.Count > 0 &&
                (m_Queue[0].Config.ActionInterruptible ||
                    m_Queue[0].Config.CanBeInterruptedBy(action.ActionID)))
            {
                ClearActions(false);
            }

            if (GetQueueTimeDepth() >= k_MaxQueueTimeDepth)
            {
                // 队列预计耗时太长了，继续塞动作会让响应变得很差，所以直接丢弃
                return;
            }

            var newAction = ActionFactory.CreateActionFromData(ref action);
            m_Queue.Add(newAction);
            if (m_Queue.Count == 1) { StartAction(); }
        }

        public void ClearActions(bool cancelNonBlocking)
        {
            if (m_Queue.Count > 0)
            {
                // 动作被中断时，要清掉冷却记录，这样玩家可以立刻重新尝试
                m_LastUsedTimestamps.Remove(m_Queue[0].ActionID);
                m_Queue[0].Cancel(m_ServerCharacter);
            }

            // 清空阻塞队列里的动作，并把对象归还到工厂池里
            {
                var removedActions = ListPool<Action>.Get();

                foreach (var action in m_Queue)
                {
                    removedActions.Add(action);
                }

                m_Queue.Clear();

                foreach (var action in removedActions)
                {
                    TryReturnAction(action);
                }

                ListPool<Action>.Release(removedActions);
            }


            if (cancelNonBlocking)
            {
                // 有些后台动作也需要一起取消，比如持续特效或飞行中的投射物
                var removedActions = ListPool<Action>.Get();

                foreach (var action in m_NonBlockingActions)
                {
                    action.Cancel(m_ServerCharacter);
                    removedActions.Add(action);
                }
                m_NonBlockingActions.Clear();

                foreach (var action in removedActions)
                {
                    TryReturnAction(action);
                }

                ListPool<Action>.Release(removedActions);
            }
        }

        /// <summary>
        /// 【译】如果当前有一个 Action 正在执行，就把它的信息填到 data 中并返回 true。
        /// 【译】如果没有动作在执行，则返回 false。
        /// 【译】这里仅指“阻塞动作”；后台可能还有多个非阻塞动作在运行，但这仍然会返回 false。
        /// </summary>
        public bool GetActiveActionInfo(out ActionRequestData data)
        {
            if (m_Queue.Count > 0)
            {
                data = m_Queue[0].Data;
                return true;
            }
            else
            {
                data = new ActionRequestData();
                return false;
            }
        }

        /// <summary>
        /// 【译】判断某个动作现在能不能释放，或者是否因为刚用过而会自动失败。
        /// 【译】也就是距离上次使用的时间是否已经超过 ReuseTimeSeconds。
        /// </summary>
        /// <param name="actionID">【译】我们想要执行的动作</param>
        /// <returns>【译】如果现在可以执行则返回 true；如果还需要等待一段时间则返回 false。</returns>
        public bool IsReuseTimeElapsed(ActionID actionID)
        {
            if (m_LastUsedTimestamps.TryGetValue(actionID, out float lastTimeUsed))
            {
                var abilityConfig = GameDataSource.Instance.GetActionPrototypeByID(actionID).Config;

                float reuseTime = abilityConfig.ReuseTimeSeconds;
                if (reuseTime > 0 && Time.time - lastTimeUsed < reuseTime)
                {
                    // 【译】还需要再等一会儿！
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 【译】返回当前正在运行的动作数量。
        /// 【译】这包括所有非阻塞动作，以及队首那个阻塞动作（如果有的话）。
        /// </summary>
        public int RunningActionCount
        {
            get
            {
                return m_NonBlockingActions.Count + (m_Queue.Count > 0 ? 1 : 0);
            }
        }

        /// <summary>
        /// 【译】启动队列最前面的那个动作（如果有的话）。
        /// </summary>
        private void StartAction()
        {
            if (m_Queue.Count > 0)
            {
                float reuseTime = m_Queue[0].Config.ReuseTimeSeconds;
                if (reuseTime > 0
                    && m_LastUsedTimestamps.TryGetValue(m_Queue[0].ActionID, out float lastTimeUsed)
                    && Time.time - lastTimeUsed < reuseTime)
                {
                    // 【译】我们刚刚已经启动过同类动作了，时间还太近。
                    AdvanceQueue(false); // 【译】如果队列里还有动作，这里会递归调用 StartAction()...
                    return;              // 【译】...所以这里不要再继续做别的事了。
                }

                int index = SynthesizeTargetIfNecessary(0);
                SynthesizeChaseIfNecessary(index);

                m_Queue[0].TimeStarted = Time.time;
                bool play = m_Queue[0].OnStart(m_ServerCharacter);
                if (!play)
                {
                    // 【译】按设计，在 Start 方法里直接退出的动作不会再调用 End。
                    AdvanceQueue(false); // 【译】如果队列里还有动作，这里会递归调用 StartAction()...
                    return;              // 【译】...所以这里不要再继续做别的事了。
                }

                // 【译】如果这个 Action 可被打断，就说明移动应该能打断它... 角色需要先保持静止！
                // 【译】所以在开始前先停掉已经在进行的移动。
                if (m_Queue[0].Config.ActionInterruptible && !m_Movement.IsPerformingForcedMovement())
                {
                    // 既然动作要求角色原地执行，那就先把当前移动停掉
                    m_Movement.CancelMove();
                }

                // 记录这次成功使用动作的时间，后面用来判定冷却
                m_LastUsedTimestamps[m_Queue[0].ActionID] = Time.time;

                if (m_Queue[0].Config.ExecTimeSeconds == 0 && m_Queue[0].Config.BlockingMode == BlockingModeType.OnlyDuringExecTime)
                {
                    // 【译】没有执行时长的“非阻塞动作”不应该停在队首，否则可能被下一帧的新动作打断得不稳定。
                    m_NonBlockingActions.Add(m_Queue[0]);
                    AdvanceQueue(false); // 【译】如果队列里还有动作，这里会递归调用 StartAction()...
                    return;              // 【译】...所以这里不要再继续做别的事了。
                }
            }
        }

        /// <summary>
        /// 【译】如果有需要，就为队首动作合成一个 ChaseAction。
        /// 【译】前提是基础动作必须有目标，并且带有 ShouldClose 标记。
        /// 【译】队列为空时不能调用这个方法。
        /// </summary>
        /// <returns>【译】当前正在处理的动作的新索引。</returns>
        private int SynthesizeChaseIfNecessary(int baseIndex)
        {
            Action baseAction = m_Queue[baseIndex];

            if (baseAction.Data.ShouldClose && baseAction.Data.TargetIds != null)
            {
                ActionRequestData data = new ActionRequestData
                {
                    ActionID = GameDataSource.Instance.GeneralChaseActionPrototype.ActionID,
                    TargetIds = baseAction.Data.TargetIds,
                    Amount = baseAction.Config.Range
                };
                // 【译】这个“靠近目标”的需求只合成一次，避免重复插入 ChaseAction。
                baseAction.Data.ShouldClose = false; // 【译】这个标记只允许使用一次。
                Action chaseAction = ActionFactory.CreateActionFromData(ref data);
                m_Queue.Insert(baseIndex, chaseAction);
                return baseIndex + 1;
            }
            return baseIndex;
        }

        /// <summary>
        /// 【译】如果角色还没有锁定目标，带目标的技能应该自动把当前目标设上。
        /// </summary>
        /// <param name="baseIndex">【译】m_Queue 中基础动作的新索引</param>
        /// <returns>【译】返回处理后的索引</returns>
        private int SynthesizeTargetIfNecessary(int baseIndex)
        {
            Action baseAction = m_Queue[baseIndex];
            var targets = baseAction.Data.TargetIds;

            if (targets != null &&
                targets.Length == 1 &&
                targets[0] != m_ServerCharacter.TargetId.Value)
            {
                // 【译】如果目标和当前锁定目标不同，就先补一个 TargetAction，把角色的“当前目标”切过去。

                ActionRequestData data = new ActionRequestData
                {
                    ActionID = GameDataSource.Instance.GeneralTargetActionPrototype.ActionID,
                    TargetIds = baseAction.Data.TargetIds
                };

                // 【译】下一次轮到原始动作时，目标状态应该已经同步好了，所以不会重复执行这个动作。
                Action targetAction = ActionFactory.CreateActionFromData(ref data);
                m_Queue.Insert(baseIndex, targetAction);
                return baseIndex + 1;
            }

            return baseIndex;
        }

        /// <summary>
        /// 【译】可选择结束当前正在播放的动作，并推进到下一个想要执行的动作。
        /// </summary>
        /// <param name="endRemoved">【译】如果为 true，就会对被移除的元素调用 End。</param>
        private void AdvanceQueue(bool endRemoved)
        {
            if (m_Queue.Count > 0)
            {
                if (endRemoved)
                {
                    m_Queue[0].End(m_ServerCharacter);
                    if (m_Queue[0].ChainIntoNewAction(ref m_PendingSynthesizedAction))
                    {
                        m_HasPendingSynthesizedAction = true;
                    }
                }
                var action = m_Queue[0];
                m_Queue.RemoveAt(0);
                TryReturnAction(action);
            }

            // 【译】继续启动下一条动作，除非前面刚合成了一个新的动作要优先执行。
            if (!m_HasPendingSynthesizedAction || m_PendingSynthesizedAction.ShouldQueue)
            {
                StartAction();
            }
        }

        private void TryReturnAction(Action action)
        {
            if (m_Queue.Contains(action))
            {
                return;
            }

            if (m_NonBlockingActions.Contains(action))
            {
                return;
            }

            ActionFactory.ReturnAction(action);
        }

        public void OnUpdate()
        {
            if (m_HasPendingSynthesizedAction)
            {
                m_HasPendingSynthesizedAction = false;
                PlayAction(ref m_PendingSynthesizedAction);
            }

            if (m_Queue.Count > 0 && m_Queue[0].ShouldBecomeNonBlocking())
            {
                // 【译】当前动作已经过了“阻塞阶段”，把它移到后台，让投射物等后续效果继续跑。
                m_NonBlockingActions.Add(m_Queue[0]);
                AdvanceQueue(false);
            }

            // 【译】先更新队首的阻塞动作。
            if (m_Queue.Count > 0)
            {
                if (!UpdateAction(m_Queue[0]))
                {
                    AdvanceQueue(true);
                }
            }

            // 【译】再更新后台动作；倒序遍历是为了方便边遍历边移除。
            for (int i = m_NonBlockingActions.Count - 1; i >= 0; --i)
            {
                Action runningAction = m_NonBlockingActions[i];
                if (!UpdateAction(runningAction))
                {
                    // 【译】它已经结束了！
                    runningAction.End(m_ServerCharacter);
                    m_NonBlockingActions.RemoveAt(i);
                    TryReturnAction(runningAction);
                }
            }
        }

        /// <summary>
        /// 【译】调用某个 Action 的 Update()，并判断它是否还活着。
        /// </summary>
        /// <returns>【译】如果动作仍然有效则返回 true，否则返回 false。</returns>
        private bool UpdateAction(Action action)
        {
            bool keepGoing = action.OnUpdate(m_ServerCharacter);
            bool expirable = action.Config.DurationSeconds > 0f; // 【译】非正值表示持续时间是无限的。
            var timeElapsed = Time.time - action.TimeStarted;
            bool timeExpired = expirable && timeElapsed >= action.Config.DurationSeconds;
            return keepGoing && !timeExpired;
        }

        /// <summary>
        /// 【译】队列里剩余所有动作大概要花多少时间才能播放完。
        /// 【译】这里统计的是每个动作的“阻塞时间”，不一定等于它自己的持续时间。
        /// 【译】注意这只是一个估算值，因为某些动作可以无限期阻塞队列。
        /// </summary>
        /// <returns>【译】队列的总“时间深度”，也就是如果不再加入新动作，大约还要多少秒才能执行完。</returns>
        private float GetQueueTimeDepth()
        {
            if (m_Queue.Count == 0) { return 0; }

            float totalTime = 0;
            foreach (var action in m_Queue)
            {
                var info = action.Config;
                float actionTime = info.BlockingMode == BlockingModeType.OnlyDuringExecTime ? info.ExecTimeSeconds :
                                    info.BlockingMode == BlockingModeType.EntireDuration ? info.DurationSeconds :
                                    throw new System.Exception($"Unrecognized blocking mode: {info.BlockingMode}");
                totalTime += actionTime;
            }

            return totalTime - m_Queue[0].TimeRunning;
        }

        public void CollisionEntered(Collision collision)
        {
            if (m_Queue.Count > 0)
            {
                m_Queue[0].CollisionEntered(m_ServerCharacter, collision);
            }
        }

        /// <summary>
        /// 【译】让所有活跃中的 Action 都有机会修改某个游戏数值。
        /// </summary>
        /// <remarks>
        /// 【译】这里既会处理正面效果（buff），也会处理负面效果（debuff）。
        /// </remarks>
        /// <param name="buffType">【译】当前正在计算哪一种游戏变量</param>
        /// <returns>【译】最终经过加成后的数值</returns>
        public float GetBuffedValue(Action.BuffableValue buffType)
        {
            float buffedValue = Action.GetUnbuffedValue(buffType);
            if (m_Queue.Count > 0)
            {
                m_Queue[0].BuffValue(buffType, ref buffedValue);
            }
            foreach (var action in m_NonBlockingActions)
            {
                action.BuffValue(buffType, ref buffedValue);
            }
            return buffedValue;
        }

        /// <summary>
        /// 【译】告诉所有活跃的 Action：某个游戏事件发生了，比如被打到、被治疗、死亡等等。
        /// 【译】Action 可以根据这个事件改变自己的行为。
        /// </summary>
        /// <param name="activityThatOccurred">【译】已经发生的事件类型</param>
        public virtual void OnGameplayActivity(Action.GameplayActivity activityThatOccurred)
        {
            if (m_Queue.Count > 0)
            {
                m_Queue[0].OnGameplayActivity(m_ServerCharacter, activityThatOccurred);
            }
            foreach (var action in m_NonBlockingActions)
            {
                action.OnGameplayActivity(m_ServerCharacter, activityThatOccurred);
            }
        }


        /// <summary>
        /// 【译】取消当前正在运行的某个 ActionLogic 的第一个实例；如果 cancelAll 为 true，则取消全部实例。
        /// 【译】会先搜索正在运行的动作，再检查队首动作。
        /// </summary>
        /// <param name="logic">【译】要取消的 ActionLogic</param>
        /// <param name="cancelAll">【译】如果为 true 就取消所有实例；如果为 false 就只取消第一个运行中的实例。</param>
        /// <param name="exceptThis">【译】如果设置了，就跳过这个动作（常用于动作取消自己同类的其他实例）</param>
        public void CancelRunningActionsByLogic(ActionLogic logic, bool cancelAll, Action exceptThis = null)
        {
            for (int i = m_NonBlockingActions.Count - 1; i >= 0; --i)
            {
                var action = m_NonBlockingActions[i];
                if (action.Config.Logic == logic && action != exceptThis)
                {
                    action.Cancel(m_ServerCharacter);
                    m_NonBlockingActions.RemoveAt(i);
                    TryReturnAction(action);
                    if (!cancelAll) { return; }
                }
            }

            if (m_Queue.Count > 0)
            {
                var action = m_Queue[0];
                if (action.Config.Logic == logic && action != exceptThis)
                {
                    action.Cancel(m_ServerCharacter);
                    m_Queue.RemoveAt(0);
                    TryReturnAction(action);
                }
            }
        }
    }
}

