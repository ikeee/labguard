using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Server
{
    /// <summary>
    /// 稳定设备身份（协议 §3 设备状态模型）。
    ///
    /// 为什么需要：只有"机器名 + IP"时，DHCP 换 IP、老师改机器名都会让教师端认不出"这还是同一台"，
    /// 于是没法回答"这台到底应用没应用新策略"。行业里（MeshCentral/Fleet/Uptane）都以稳定设备 ID 为核心。
    ///
    /// 身份规则：首次调用生成 `LG-` + 16 位十六进制（RNG），落盘 `%ProgramData%\LabGuard\device-id.txt`
    /// （ACL 加固），之后**只读不再改**——重启、改机器名、换 IP 都不变。
    ///
    /// 克隆镜像的边界（诚实说明）：整盘克隆会把该文件一起复制走，客户端**无法自行分辨**"我是被克隆的"；
    /// 检测放在服务端——同一 deviceId 来自多台机器名/IP 就在列表里标"疑似克隆（重复ID）"。
    /// </summary>
    public static class DeviceId
    {
        private static string _dataDir = ConfigStore.DataDir;

        /// <summary>落盘目录（自检注入临时目录用）。改目录会把内存缓存一并清掉，避免"换目录还拿旧 ID"。</summary>
        public static string DataDir
        {
            get { return _dataDir; }
            set { _dataDir = value; _cached = null; }
        }

        public static string IdPath { get { return Path.Combine(DataDir, "device-id.txt"); } }

        private static readonly object Gate = new object();
        private static string _cached;

        /// <summary>取本机设备 ID（不存在则生成并落盘）。任何异常都退回"内存内临时 ID"（不阻断管控）。</summary>
        public static string Get()
        {
            lock (Gate)
            {
                if (!string.IsNullOrEmpty(_cached)) return _cached;
                try
                {
                    if (File.Exists(IdPath))
                    {
                        string existing = File.ReadAllText(IdPath, Encoding.UTF8).Trim();
                        if (IsValid(existing)) { _cached = existing; return _cached; }
                        Log.Warn("设备 ID 文件内容不合法（疑似被改），已重新生成：" + IdPath);
                    }
                    string id = NewId();
                    Directory.CreateDirectory(DataDir);
                    File.WriteAllText(IdPath, id, new UTF8Encoding(false));
                    Harden(IdPath);
                    Log.Info("已生成设备 ID：" + id);
                    _cached = id;
                    return _cached;
                }
                catch (Exception ex)
                {
                    Log.Warn("设备 ID 落盘失败（本次运行用临时 ID）：" + ex.Message);
                    _cached = NewId();
                    return _cached;
                }
            }
        }

        /// <summary>合法格式：`LG-` + 16 位十六进制小写。</summary>
        public static bool IsValid(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 19 || !id.StartsWith("LG-", StringComparison.Ordinal)) return false;
            for (int i = 3; i < id.Length; i++)
            {
                char c = id[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>生成新 ID（CSPRNG，避免克隆/批量部署时撞号）。</summary>
        public static string NewId()
        {
            byte[] buf = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buf);
            var sb = new StringBuilder("LG-", 19);
            foreach (byte b in buf) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>ACL：身份文件只允许管理员/SYSTEM 改写（学生改 ID 会让教师端认错设备）。</summary>
        private static void Harden(string path)
        {
            try
            {
                var info = new FileInfo(path);
                FileSecurity sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    FileSystemRights.Read | FileSystemRights.ExecuteFile,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(sec);
            }
            catch (Exception ex)
            {
                Log.Warn("加固设备 ID 文件权限失败：" + ex.Message);
            }
        }
    }
}
