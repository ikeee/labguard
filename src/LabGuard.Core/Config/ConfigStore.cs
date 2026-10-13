using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Config
{
    /// <summary>
    /// 配置存取：%ProgramData%\LabGuard\config.json（DPAPI 机器密钥加密），
    /// 并在 HKLM\SOFTWARE\LabGuard 留最小必要镜像（Enabled / Installed / InstallDir），便于卸载与服务读取。
    /// </summary>
    public static class ConfigStore
    {
        public const string RegistryRoot = @"SOFTWARE\LabGuard";

        public static string DataDir { get; private set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabGuard");

        /// <summary>
        /// 是否处于**沙箱/便携模式**（由 <see cref="OverrideDataDir"/> 置位）。
        ///
        /// 这个标志存在的唯一目的：**沙箱进程绝不能碰真实机器的注册表镜像**。
        /// 血泪教训（2026-10-13 测试台首跑）：沙箱目录里没有 config.json 时，旧逻辑会"从 HKLM 注册表镜像恢复"
        /// —— 恢复出来的是真机旧配置（Enabled=true、各 Guard 全开），于是沙箱客户端把**真机**的
        /// U 盘（usbstor）、浏览器策略、启动菜单全改了。沙箱模式下必须：不读镜像、不写镜像、强制干跑。
        /// </summary>
        public static bool IsSandbox { get; private set; }

        /// <summary>
        /// **测试/便携模式**：把配置目录整体改到别处（只影响当前进程）。
        ///
        /// 用途：在一台机器上并行跑多个"学生端沙箱"做端到端测试——各自独立的数据目录、独立的设备身份，
        /// 互不干扰，也不碰真实安装（`%ProgramData%\LabGuard` 与 HKLM 完全不动）。
        ///
        /// 安全边界：**只有命令行参数能触发**（`--data-dir &lt;目录&gt;`）。服务由 SCM 启动、兜底任务由
        /// 计划任务启动，它们的命令行里都不含这个参数，所以学生无法用它把真管控的配置指到别处。
        /// </summary>
        public static void OverrideDataDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string full = Path.GetFullPath(dir);
            // 防呆：别把沙箱指到真实安装目录——那就等于"测试台改真机"，2026-10-13 的事故就是这条路
            string real = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabGuard"));
            if (string.Equals(full.TrimEnd('\\'), real.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝把测试数据目录指向真实安装目录：" + real);
            DataDir = full;
            Directory.CreateDirectory(DataDir);
            Log.Directory = Path.Combine(DataDir, "logs");
            // 这几个模块的默认目录是"类型初始化时"从 DataDir 取的，必须一起改，否则会读到真实安装的目录
            Server.ServerPairing.DataDir = DataDir;
            Server.ServerPolicyCache.DataDir = DataDir;
            Server.ServerSyncState.DataDir = DataDir;
            Server.DeviceId.DataDir = DataDir;
            IsSandbox = true;
            // 沙箱里**一律干跑**，而且要用"谁也改不掉"的那把锁：
            // GuardEngine.Start 会按调用方传的 dryRun 覆盖 Log.DryRun，普通赋值会被掀掉
            Log.ForceDryRunOn();
            Log.Warn("测试/便携（沙箱）模式：配置目录 = " + DataDir +
                     "；不读/不写注册表镜像，且强制干跑（不修改系统）");
        }

        public static string ConfigPath => Path.Combine(DataDir, "config.json");

        public static GuardConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    // 配置文件不见了：如果"已安装标记"还在，说明是被删/被破坏 →
                    // 从注册表里的加密镜像恢复（普通用户改不了 HKLM，所以删 config.json 也白删）
                    GuardConfig fromMirror = LoadFromRegistryMirror();
                    if (fromMirror != null)
                    {
                        Log.Warn("配置文件缺失，已从注册表备份恢复（可能被删除/破坏）");
                        try { Save(fromMirror); } catch { }
                        RestoredFromMirror = true;   // 注意：Save() 会清标记，这里要再置回
                        return fromMirror;
                    }
                    if (IsInstalled()) TamperSuspected = true;
                    return new GuardConfig();
                }
                byte[] key = ConfigIntegrity.TryGetKey();
                string json;
                DateTime fileSavedAt;
                string rawContent;
                try { rawContent = Decrypt(File.ReadAllBytes(ConfigPath)); }
                catch { rawContent = null; }
                bool fileIsLegacy = rawContent != null && !ConfigIntegrity.IsEnvelope(rawContent);
                if (rawContent == null || !ConfigIntegrity.TryUnwrap(key, rawContent, out json, out fileSavedAt))
                {
                    // 红队 B4：签名不对 = 文件被篡改（DPAPI 对本机管理员无机密性，防不住就检出）
                    Log.Error("配置文件签名验证失败（疑似被篡改），尝试从注册表镜像恢复");
                    TamperSuspected = true;
                    GuardConfig mirrorCfg = LoadFromRegistryMirror();
                    if (mirrorCfg != null)
                    {
                        try { Save(mirrorCfg); } catch { }
                        TamperSuspected = true;   // Save 会清标记，再置回
                        return mirrorCfg;
                    }
                    if (IsInstalled()) return new GuardConfig();   // 镜像也没了：受损默认（fail-closed 由消费方判）
                    return new GuardConfig();
                }
                var cfg = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }
                    .Deserialize<GuardConfig>(json) ?? new GuardConfig();
                Normalize(cfg);

                // 交叉校验镜像：正常流程两边同时写、时间戳必然一致；不一致 = 有一边被单独改写/回滚
                DateTime mirrorSavedAt;
                GuardConfig mirrorCfg2 = LoadFromRegistryMirror(out mirrorSavedAt);
                if (fileIsLegacy)
                {
                    // 升级竞速（2026-10-08 部署复盘）：老配置遇上新二进制。
                    // 镜像是签名新版 → 文件被人退成无签名（单点篡改/回滚）→ 镜像赢；
                    // 镜像也是 legacy（或没有）→ 真·一次性升级：接受并立刻 Save 改签，
                    // 绝不能直接判篡改（否则第一次 MarkPaused 建密钥后全机配置被误判清空）。
                    if (mirrorCfg2 != null && mirrorSavedAt != DateTime.MinValue)
                    {
                        Log.Warn("配置文件无签名但注册表镜像是签名新版（疑似单点篡改/回滚），已按镜像恢复");
                        TamperSuspected = true;
                        try { Save(mirrorCfg2); } catch { }
                        TamperSuspected = true;
                        return mirrorCfg2;
                    }
                    try { Save(cfg); } catch { }   // 一次性升级：立刻改签
                    return cfg;
                }
                if (mirrorCfg2 != null && mirrorSavedAt != fileSavedAt)
                {
                    Log.Warn("配置文件与注册表镜像不一致（疑似单点篡改/回滚），取较新的一份");
                    TamperSuspected = true;
                    if (mirrorSavedAt > fileSavedAt) { try { Save(mirrorCfg2); } catch { } TamperSuspected = true; return mirrorCfg2; }
                    try { Save(cfg); } catch { }
                    TamperSuspected = true;
                }
                return cfg;
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败，尝试从注册表备份恢复", ex);
                GuardConfig fromMirror = LoadFromRegistryMirror();
                if (fromMirror != null)
                {
                    RestoredFromMirror = true;
                    return fromMirror;
                }
                if (IsInstalled()) TamperSuspected = true;
                return new GuardConfig();
            }
        }

        /// <summary>解 DPAPI + 验签名信封。legacy（升级前老配置）仅在还没有密钥时接受一次。</summary>
        private static bool TryDecodeBlob(byte[] blob, byte[] key, out string json, out DateTime savedAtUtc)
        {
            json = null;
            savedAtUtc = DateTime.MinValue;
            string content;
            try { content = Decrypt(blob); }
            catch { return false; }
            return ConfigIntegrity.TryUnwrap(key, content, out json, out savedAtUtc);
        }

        /// <summary>本次读取是否发生"从备份恢复"（说明可能有人动了配置文件）。</summary>
        public static bool RestoredFromMirror { get; private set; }
        /// <summary>已安装、但既没有配置文件也没有备份 → 高度怀疑被人破坏。</summary>
        public static bool TamperSuspected { get; private set; }

        /// <summary>
        /// 防拆告警：小助手在短时间内被反复结束（有人在杀进程）时置位。
        /// 与 <see cref="TamperSuspected"/>（配置文件被破坏）是两回事，分开记，老师看心跳时能分清。
        /// </summary>
        public static bool KillAlert { get; set; }

        public static void ClearTamperFlags()
        {
            RestoredFromMirror = false;
            TamperSuspected = false;
        }

        /// <summary>消费方发现"配置语义级异常"（如已安装但密码散列为空，B4c）时主动打标记。</summary>
        public static void MarkTamperSuspected()
        {
            TamperSuspected = true;
        }

        /// <summary>从 HKLM\SOFTWARE\LabGuard 的 CfgData（DPAPI 加密 + 签名 JSON）恢复配置。</summary>
        private static GuardConfig LoadFromRegistryMirror()
        {
            DateTime savedAt;
            return LoadFromRegistryMirror(out savedAt);
        }

        private static GuardConfig LoadFromRegistryMirror(out DateTime savedAtUtc)
        {
            // 沙箱模式：绝不读真机注册表镜像（否则会加载真机旧配置 → 沙箱客户端去改真机，2026-10-13 实测事故）
            if (IsSandbox) { savedAtUtc = DateTime.MinValue; return null; }
            savedAtUtc = DateTime.MinValue;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegistryRoot))
                {
                    byte[] blob = k?.GetValue("CfgData") as byte[];
                    if (blob == null || blob.Length == 0) return null;
                    string json;
                    if (!TryDecodeBlob(blob, ConfigIntegrity.TryGetKey(), out json, out savedAtUtc))
                    {
                        Log.Warn("注册表镜像签名验证失败（疑似被篡改）");
                        return null;
                    }
                    var cfg = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }
                        .Deserialize<GuardConfig>(json);
                    if (cfg == null) return null;
                    Normalize(cfg);
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("从注册表备份恢复配置失败：" + ex.Message);
                return null;
            }
        }

        public static void Save(GuardConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(cfg);
                // 签名信封（红队 B4）：密钥不可用时降级为无签名明文 JSON（维持旧行为）
                byte[] key = ConfigIntegrity.GetOrCreateKey();
                string content = key != null ? ConfigIntegrity.Wrap(key, json, DateTime.UtcNow) : json;
                File.WriteAllBytes(ConfigPath, Encrypt(content));
                HardenDirectory(DataDir);
                WriteRegistryMirror(cfg);
                // 注册表加密镜像：配置文件被删/被破坏时自动恢复（HKLM\SOFTWARE\LabGuard 已被加固，普通用户改不了）
                // 沙箱模式**必须跳过**：这是真机注册表。2026-10-13 隔离验真抓到过这一条——
                // 上面 WriteRegistryMirror 已经挡了，这里还有一份重复的写入没挡。
                if (!IsSandbox)
                {
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RegistryRoot))
                    {
                        k?.SetValue("CfgData", Encrypt(content), RegistryValueKind.Binary);
                    }
                }
                ClearTamperFlags();
                // 服务器管控状态联动：老师把 Server.Enabled 关掉时，必须同时清掉"服务器暂停"态，
                // 否则机器会停在"服务器暂停"里而服务器已不再管它（永远解不开）——详见 ServerSyncState。
                Server.ServerSyncState.NoteConfigSaved(cfg);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
                throw;
            }
        }

        public static bool IsInstalled()
        {
            // 沙箱模式：不把真机的「已安装」标记当自己的（否则会走受损/篡改判定分支）
            if (IsSandbox) return false;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegistryRoot))
                {
                    return k != null && Convert.ToInt32(k.GetValue("Installed", 0)) == 1;
                }
            }
            catch { return false; }
        }

        /// <summary>导出为明文 JSON（便于在机房多台机器间复制同一套设置，含口令散列）。</summary>
        public static void ExportTo(string path)
        {
            GuardConfig cfg = Load();
            File.WriteAllText(path, Serialize(cfg), new UTF8Encoding(false));
        }

        /// <summary>从明文 JSON 导入设置。</summary>
        public static GuardConfig ImportFrom(string path)
        {
            return Deserialize(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>配置 → JSON 文本。</summary>
        public static string Serialize(GuardConfig cfg)
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(cfg);
        }

        /// <summary>JSON 文本 → 配置（自动补全缺省子对象）。</summary>
        public static GuardConfig Deserialize(string json)
        {
            var cfg = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }
                .Deserialize<GuardConfig>(json);
            if (cfg == null) throw new InvalidDataException("配置文件解析失败");
            Normalize(cfg);
            return cfg;
        }

        public static string InstallDir
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegistryRoot))
                    {
                        return k?.GetValue("InstallDir") as string ?? AppDomain.CurrentDomain.BaseDirectory;
                    }
                }
                catch { return AppDomain.CurrentDomain.BaseDirectory; }
            }
        }

        private static void WriteRegistryMirror(GuardConfig cfg)
        {
            // 沙箱模式：绝不写真机注册表（含 CfgData 加密镜像）
            if (IsSandbox) return;
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RegistryRoot))
            {
                if (k == null) return;
                k.SetValue("Installed", 1, RegistryValueKind.DWord);
                k.SetValue("Enabled", cfg.Enabled ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("Version", cfg.Version, RegistryValueKind.DWord);
                // InstallDir 由安装脚本写入（安装脚本是唯一权威），避免在别处运行设置程序时把路径写歪
                if (k.GetValue("InstallDir") == null)
                {
                    k.SetValue("InstallDir", AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), RegistryValueKind.String);
                }
            }
        }

        private static void Normalize(GuardConfig c)
        {
            if (c.Classroom == null) c.Classroom = new ClassroomSettings();
            if (c.Network == null) c.Network = new NetworkSettings();
            if (c.ProcessBlock == null) c.ProcessBlock = new ProcessBlockSettings();
            if (c.FileCreation == null) c.FileCreation = new FileCreationSettings();
            if (c.Usb == null) c.Usb = new UsbSettings();
            if (c.Hosts == null) c.Hosts = new HostsSettings();
            if (c.Browser == null) c.Browser = new BrowserSettings();
            if (c.Shell == null) c.Shell = new ShellSettings();
            if (c.Session == null) c.Session = new SessionSettings();
            if (c.SafeMode == null) c.SafeMode = new SafeModeSettings();
            if (c.Site == null) c.Site = new SiteSettings();
            if (c.RegistryAcl == null) c.RegistryAcl = new RegistryAclSettings();
            if (c.Watchdog == null) c.Watchdog = new WatchdogSettings();
            if (c.AntiTamper == null) c.AntiTamper = new AntiTamperSettings();
            if (c.Server == null) c.Server = new ServerSettings();
        }

        private static byte[] Encrypt(string plain)
        {
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.LocalMachine);
        }

        private static string Decrypt(byte[] blob)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.LocalMachine));
        }

        /// <summary>限制配置目录只能由 Administrators / SYSTEM / TrustedInstaller 访问。</summary>
        private static void HardenDirectory(string dir)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                DirectorySecurity sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    FileSystemRights.Read | FileSystemRights.ExecuteFile, inherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(sec);
            }
            catch (Exception ex)
            {
                Log.Warn("加固配置目录权限失败：" + ex.Message);
            }
        }
    }
}
