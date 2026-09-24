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

        public static string DataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabGuard");

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
                byte[] blob = File.ReadAllBytes(ConfigPath);
                string json = Decrypt(blob);
                var cfg = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }
                    .Deserialize<GuardConfig>(json) ?? new GuardConfig();
                Normalize(cfg);
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

        /// <summary>本次读取是否发生"从备份恢复"（说明可能有人动了配置文件）。</summary>
        public static bool RestoredFromMirror { get; private set; }
        /// <summary>已安装、但既没有配置文件也没有备份 → 高度怀疑被人破坏。</summary>
        public static bool TamperSuspected { get; private set; }

        public static void ClearTamperFlags()
        {
            RestoredFromMirror = false;
            TamperSuspected = false;
        }

        /// <summary>从 HKLM\SOFTWARE\LabGuard 的 CfgData（DPAPI 加密 JSON）恢复配置。</summary>
        private static GuardConfig LoadFromRegistryMirror()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegistryRoot))
                {
                    byte[] blob = k?.GetValue("CfgData") as byte[];
                    if (blob == null || blob.Length == 0) return null;
                    var cfg = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }
                        .Deserialize<GuardConfig>(Decrypt(blob));
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
                File.WriteAllBytes(ConfigPath, Encrypt(json));
                HardenDirectory(DataDir);
                WriteRegistryMirror(cfg);
                // 注册表加密镜像：配置文件被删/被破坏时自动恢复（HKLM\SOFTWARE\LabGuard 已被加固，普通用户改不了）
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RegistryRoot))
                {
                    k?.SetValue("CfgData", Encrypt(json), RegistryValueKind.Binary);
                }
                ClearTamperFlags();
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
                throw;
            }
        }

        public static bool IsInstalled()
        {
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
            if (c.SafeMode == null) c.SafeMode = new SafeModeSettings();
            if (c.Wallpaper == null) c.Wallpaper = new WallpaperSettings();
            if (c.RegistryAcl == null) c.RegistryAcl = new RegistryAclSettings();
            if (c.Watchdog == null) c.Watchdog = new WatchdogSettings();
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
