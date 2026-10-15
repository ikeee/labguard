using System;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Guards
{
    public abstract class GuardBase : IGuard
    {
        protected GuardContext Context;
        private System.Timers.Timer _timer;

        public abstract string Name { get; }
        public virtual bool Enabled => true;
        public string Status { get; protected set; } = "未启动";

        /// <summary>
        /// 本模块**真的改过系统**吗？在落写操作的地方调 <see cref="MarkApplied"/>。
        ///
        /// 为什么必须有这个标记：<see cref="Stop"/> 会跑各模块的"还原"分支，而好几个还原分支是
        /// **无条件写**的 —— 浏览器策略 = 删掉 HKLM 下的策略键、USB = 把 usbstor 的 Start 写回 3、
        /// 安装目录隐藏 = 改隐藏属性。如果这个模块**压根没启动过**（Enabled=false 时 Start 直接早退），
        /// 还原分支照样会被引擎调到 —— 那就会把**别人**设的值改掉（学校组策略、管理员、甚至学生自己设的）。
        /// 真实事故同族：全关配置也会起引擎，随后 Stop() 的"逐项还原"把老师自己设的系统项一起还原了。
        ///
        /// 规则：**没动过就不许还原**。卸载 / 退出程序这种"必须彻底还原"的场景走 <see cref="ForceStop"/>。
        /// </summary>
        private bool _applied;
        public bool Applied => _applied;

        /// <summary>标记"本模块确实改过系统"（在真正落写的地方调用，别在 OnStart 开头无脑调）。</summary>
        protected void MarkApplied() { _applied = true; }

        /// <summary>用于"只还原、不启动"的场景（卸载 / 退出程序）：注入上下文后可直接调用 Stop()。</summary>
        public void AttachContext(GuardContext context)
        {
            Context = context;
        }

        /// <summary>轮询间隔（毫秒）。</summary>
        protected abstract int IntervalMs { get; }

        public virtual void Start(GuardContext context)
        {
            Context = context;
            if (!Enabled)
            {
                Status = "已禁用";
                PublishStatus();
                return;
            }
            try
            {
                OnStart();
                _timer = new System.Timers.Timer(IntervalMs) { AutoReset = true, Enabled = true };
                _timer.Elapsed += (s, e) => Tick();
                Status = "运行中";
                Log.Info(Name + " 已启动（轮询 " + IntervalMs + "ms）");
            }
            catch (Exception ex)
            {
                Status = "启动失败";
                Log.Error(Name + " 启动失败", ex);
            }
            PublishStatus();
        }

        public virtual void Stop()
        {
            StopCore(false);
        }

        /// <summary>无条件走还原分支（卸载 / "退出程序"用：那时不管开没开过，都要把系统还原干净）。</summary>
        public void ForceStop()
        {
            StopCore(true);
        }

        private void StopCore(bool force)
        {
            if (!force && !_applied)
            {
                // 从没改过系统 → 绝不"顺手还原"（还原分支多数是无条件写，会把别人的设置改掉）
                Status = "未启用（没改过系统，无需还原）";
                PublishStatus();
                return;
            }
            try
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Dispose();
                    _timer = null;
                }
                OnStop();
                Status = "已停止";
                Log.Info(Name + " 已停止并还原");
            }
            catch (Exception ex)
            {
                Log.Error(Name + " 停止失败", ex);
            }
            finally
            {
                _applied = false;
            }
            PublishStatus();
        }

        private void Tick()
        {
            if (Log.DryRun)
            {
                // 干跑模式：不真正改动系统，但仍走一遍检测逻辑以验证判定条件
            }
            try
            {
                OnTick();
                if (Status == "启动失败") { Status = "运行中"; PublishStatus(); }
            }
            catch (Exception ex)
            {
                Log.Error(Name + " 检查异常", ex);
                Status = "异常";
                PublishStatus();
            }
        }

        protected void SetStatus(string s)
        {
            Status = s;
            PublishStatus();
        }

        private void PublishStatus()
        {
            Context?.SetStatus(Name, Status);
        }

        protected abstract void OnStart();
        protected abstract void OnTick();
        protected virtual void OnStop() { }

        public virtual void Dispose() => Stop();
    }
}
