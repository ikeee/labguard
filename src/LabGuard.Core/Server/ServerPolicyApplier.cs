using System;
using System.Collections.Generic;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Server
{
    /// <summary>
    /// 把验签通过的服务器策略**合并进本机配置**。
    ///
    /// 关键约束（不做会出事的地方，都写在这里）：
    ///
    /// ① **本机专属字段不能被服务器覆盖**——服务器策略是一份 <c>GuardConfig</c> 序列化，
    ///    但其中有三类东西属于"这台机器自己"：
    ///      - <c>PasswordHash</c>：本机口令散列。服务器面板不渲染它（值恒为空），
    ///        若照抄就会**清空口令 = 门禁失效**（谁都能开设置面板）；
    ///      - <c>Server.*</c>：本机与服务器的连接配置（启用开关 / 地址）。
    ///        若照抄，服务器策略里那份默认值（Enabled=false）会**把服务器管控自己关掉**，自锁；
    ///      - <c>Version</c>：内部版本号。
    ///
    /// ② 策略优先级：**验签通过的服务器信封 &gt; 本地 config.json**（协议 §4）。
    ///    所以除上面三类外，其余开关一律以服务器为准。
    ///
    /// ③ 写盘与"重新生效"不在这里做——由宿主注入 saver（服务侧 = `ConfigStore.Save` + 重启引擎），
    ///    这样自检能只验证"合并结果"，不碰真实 %ProgramData%。
    /// </summary>
    public static class ServerPolicyApplier
    {
        /// <summary>服务器策略不得覆盖的本机专属字段（与 SettingsCatalog.SpecialPaths 同口径 + Server 组）。</summary>
        public static readonly string[] LocalOnlyPaths = { "Version", "PasswordHash", "Server" };

        /// <summary>
        /// 服务器策略 → 本机配置。返回**新的配置对象**（不修改传入的 local），保留本机专属字段。
        /// <paramref name="policyJson"/> 不合法时抛异常（调用方按"拒绝本次策略"处理）。
        /// </summary>
        public static GuardConfig Merge(GuardConfig local, string policyJson)
        {
            if (local == null) throw new ArgumentNullException(nameof(local));
            GuardConfig fromServer = ConfigStore.Deserialize(policyJson);   // 内含 Normalize：缺项补默认

            // 还原本机专属字段
            fromServer.Version = local.Version;
            fromServer.PasswordHash = local.PasswordHash;
            fromServer.Server = local.Server;
            return fromServer;
        }

        /// <summary>合并 + 落盘（宿主注入 saver）。返回实际写入的配置，便于日志 / 自检断言。</summary>
        public static GuardConfig Apply(string policyJson, GuardConfig local, Action<GuardConfig> save)
        {
            GuardConfig merged = Merge(local, policyJson);
            if (save == null) throw new ArgumentNullException(nameof(save));
            save(merged);
            Log.Info("已按服务器策略更新本机配置（口令 / 服务器连接配置 / 版本号保持本机值）");
            return merged;
        }

        /// <summary>诊断用：列出本次合并中被"保护"回本机值的字段。</summary>
        public static IEnumerable<string> ProtectedPaths()
        {
            foreach (string p in LocalOnlyPaths) yield return p;
        }
    }
}
