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
