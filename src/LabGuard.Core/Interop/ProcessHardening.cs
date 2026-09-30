using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace LabGuard.Core.Interop
{
    /// <summary>
    /// **进程防拆加固**：给自己的进程套一层"只有 SYSTEM / 管理员能终止"的安全描述符（DACL）。
    ///
    /// 为什么要它：任务管理器 / taskkill 杀进程走的是 <c>OpenProcess(PROCESS_TERMINATE)</c>，
    /// 而进程默认的 DACL 里「所有者（= 当前登录的学生）」是完全控制 —— 学生一键就能把自己结束掉。
    /// 这里把 DACL 换成"SYSTEM 完全控制 + 管理员完全控制 + 当前用户只留查询"，
    /// 于是标准用户在任务管理器点「结束任务」会直接收到「拒绝访问」。
    ///
    /// 说清楚边界（不吹牛）：
    /// · **管理员杀得掉**：Administrators 被显式放行，且持有 SeDebugPrivilege 时会绕过 DACL 检查。
    ///   所以"学生本来就是管理员"的机房，这一层**挡不住**，要靠「秒级复活 + 兜底计划任务」兜住（见 GuardService）。
    /// · **不是驱动级保护**：不做内核回调、不做进程隐藏、不挂钩系统 API。纯用户态 DACL，
    ///   老师随时可以用管理员身份正常杀/卸载，卸载脚本不受影响。
    /// · **自己能还原**：对象的所有者始终被隐式授予 WRITE_DAC（与 DACL 内容无关），
    ///   所以本进程随时可以 <see cref="RestoreSelf"/> 把 DACL 恢复成默认，老师退出/暂停不受影响。
    /// </summary>
    public static class ProcessHardening
    {
        // ------------------------------------------------------------------ 进程访问权
        public const uint Terminate = 0x0001;
        public const uint VmWrite = 0x0020;
        public const uint VmOperation = 0x0008;
        public const uint SuspendResume = 0x0800;
        public const uint SetInformation = 0x0200;
        /// <summary>
        /// 留给「当前用户自己」的权限：只能看（查询信息）+ 等（同步句柄），
        /// **不含** Terminate / VM_WRITE / SUSPEND_RESUME —— 也就是"杀不掉、写不了、挂不起"。
        /// = PROCESS_QUERY_INFORMATION(0x0400) | PROCESS_QUERY_LIMITED_INFORMATION(0x1000) | SYNCHRONIZE(0x100000)
        /// </summary>
        public const uint UserReadOnly = 0x101400;

        private const int SddlRevision = 1;
        private const int DaclSecurityInformation = 4;      // DACL_SECURITY_INFORMATION
        private const int SeKernelObject = 6;               // SE_KERNEL_OBJECT

        /// <summary>当前登录用户的 SID（用于"只给他查询权"的那条 ACE）。</summary>
        public static string CurrentUserSid()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return id.User == null ? null : id.User.Value;
            }
            catch { return null; }
        }

        /// <summary>
        /// 加固用的 SDDL：SYSTEM 完全控制 + 内置管理员完全控制 + 当前用户只读。
        /// 没有其它 ACE —— 于是"不在名单里的人"（标准用户拿不到管理员令牌时）一律拒绝访问。
        /// </summary>
        public static string BuildSddl(string userSid)
        {
            // D:P —— DACL 受保护（不继承父对象的 ACE）
            string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";
            // 服务进程本身就是 SYSTEM：再追加一条"SYSTEM 只读"毫无意义（还跟前面的 GA 自相矛盾）
            if (!string.IsNullOrEmpty(userSid) && !string.Equals(userSid, SystemSid, StringComparison.OrdinalIgnoreCase))
                sddl += "(A;;0x" + UserReadOnly.ToString("x") + ";;;" + userSid + ")";
            return sddl;
        }

        /// <summary>还原成默认（所有者完全控制）——老师退出/暂停、卸载前用，避免"自己杀不掉自己"。</summary>
        public static string RestoreSddl()
        {
            return "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)";
        }

        /// <summary>
        /// 给当前进程加固。返回是否成功；<paramref name="detail"/> 给出成功时的 DACL 摘要或失败原因（写日志/演练用）。
        /// </summary>
        public static bool HardenSelf(out string detail)
        {
            return ApplyToPid(Process.GetCurrentProcess().Id, BuildSddl(CurrentUserSid()), out detail);
        }

        /// <summary>恢复当前进程的默认 DACL（所有者仍被隐式授予 WRITE_DAC，所以自己一定改得回来）。</summary>
        public static bool RestoreSelf(out string detail)
        {
            return ApplyToPid(Process.GetCurrentProcess().Id, RestoreSddl(), out detail);
        }

        /// <summary>读回当前进程的安全描述符（演练/日志用）。</summary>
        public static string ReadOwnSddl()
        {
            IntPtr h = OpenProcess(0x00020000 /*READ_CONTROL*/, false, Process.GetCurrentProcess().Id);
            if (h == IntPtr.Zero) return null;
            try { return ReadSddl(h); }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// 对指定 pid 加固。**调用者必须是 SYSTEM 或管理员**（否则连 WRITE_DAC 都拿不到），
        /// 这条路径给服务进程用（服务在 SYSTEM 会话里）。
        /// </summary>
        public static bool HardenProcess(int pid, out string detail)
        {
            return ApplyToPid(pid, BuildSddl(CurrentUserSid()), out detail);
        }

        /// <summary>把指定 pid 的进程还原成"可被所有者结束"（老师暂停/卸载前调用，避免 Kill 失败）。</summary>
        public static bool RestoreProcess(int pid, out string detail)
        {
            return ApplyToPid(pid, RestoreSddl(), out detail);
        }

        /// <summary>读回进程的安全描述符（SDDL 文本），用于验证"到底加固上没有"。</summary>
        public static string ReadSddl(IntPtr hProcess)
        {
            IntPtr owner, group, dacl, sacl, sd;
            int rc = GetSecurityInfo(hProcess, SeKernelObject, DaclSecurityInformation,
                out owner, out group, out dacl, out sacl, out sd);
            if (rc != 0 || sd == IntPtr.Zero) return null;
            try
            {
                IntPtr str;
                uint len;
                if (!ConvertSecurityDescriptorToStringSecurityDescriptor(sd, SddlRevision,
                        DaclSecurityInformation, out str, out len))
                    return null;
                try { return Marshal.PtrToStringUni(str); }
                finally { if (str != IntPtr.Zero) LocalFree(str); }
            }
            finally { if (sd != IntPtr.Zero) LocalFree(sd); }
        }

        /// <summary>SYSTEM 的 SID（S-1-5-18）。</summary>
        public static readonly string SystemSid = "S-1-5-18";
        /// <summary>内置 Administrators 的 SID（S-1-5-32-544）。</summary>
        public static readonly string AdminsSid = "S-1-5-32-544";
        private const uint FullAccess = 0x001FFFFF;                  // PROCESS_ALL_ACCESS
        private const uint GenericAll = 0x10000000;                  // SDDL 里的 "GA"
        /// <summary>
        /// "GA"（GENERIC_ALL，0x10000000）和系统读回时展开成的 PROCESS_ALL_ACCESS（0x1fffff）是同一件事，
        /// 两种写法都要认 —— 否则自己刚构造的 SDDL 反而判不出"已加固"。
        /// </summary>
        private static bool IsFullControl(uint m)
        {
            return (m & FullAccess) == FullAccess || (m & 0xF0000000) == GenericAll;
        }

        /// <summary>
        /// 判断这条 SDDL 是不是"已加固"的形态 —— **按语义解析，不比对文本**。
        /// 为什么要解析：读回来的 SDDL 里 <c>GA</c> 会被展开成 <c>0x1fffff</c>，
        /// 用户 SID 也会被简写成 <c>LA</c> 之类别名，纯 Contains 比对必然失配（真机演练实测）。
        /// 独立成函数就是为了**自检能断言**——不用真的改动进程也能验规则。
        /// </summary>
        public static bool LooksHardened(string sddl, string userSid)
        {
            if (string.IsNullOrEmpty(sddl)) return false;
            try
            {
                var sd = new System.Security.AccessControl.CommonSecurityDescriptor(false, false, sddl);
                if (sd.DiscretionaryAcl == null) return false;

                bool sysFull = false, adminFull = false;
                bool userSeen = false, userDangerous = false, userRead = false;
                foreach (System.Security.AccessControl.CommonAce ace in sd.DiscretionaryAcl)
                {
                    if (ace.AceType != System.Security.AccessControl.AceType.AccessAllowed) continue;
                    if (ace.SecurityIdentifier == null) continue;
                    string v = ace.SecurityIdentifier.Value;
                    uint m = (uint)ace.AccessMask;
                    if (string.Equals(v, SystemSid, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsFullControl(m)) sysFull = true;
                    }
                    else if (string.Equals(v, AdminsSid, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsFullControl(m)) adminFull = true;
                    }
                    else if (!string.IsNullOrEmpty(userSid) &&
                             string.Equals(v, userSid, StringComparison.OrdinalIgnoreCase))
                    {
                        userSeen = true;
                        // 用户绝不能留下"杀/写/挂起/改"这类权限（GA 也等于全都留下了）
                        if (IsFullControl(m) ||
                            (m & (Terminate | VmWrite | VmOperation | SuspendResume | SetInformation | 0x0100)) != 0)
                            userDangerous = true;
                        // 但必须还能看（查询权限），否则任务管理器连进程名都显示不出来
                        if ((m & UserReadOnly) == UserReadOnly) userRead = true;
                    }
                }
                if (!sysFull || !adminFull) return false;            // 系统与管理员必须完全控制，否则老师/系统收尾会出问题
                if (string.IsNullOrEmpty(userSid)) return true;      // 服务进程（本身就是 SYSTEM）：没有"用户"这一条
                if (string.Equals(userSid, SystemSid, StringComparison.OrdinalIgnoreCase)) return true;
                return userSeen && !userDangerous && userRead;
            }
            catch { return false; }                                  // 畸形 SDDL 一律按"没加固"处理，不误判
        }

        /// <summary>
        /// 探针：用指定权限尝试打开进程。成功 = 这个权限还在（没加固住，或调用者是管理员）。
        /// 演练（--harden-drill）用它给出可断言的结论。
        /// </summary>
        public static bool CanOpen(int pid, uint access)
        {
            IntPtr h = OpenProcess(access, false, pid);
            if (h == IntPtr.Zero) return false;
            CloseHandle(h);
            return true;
        }

        // ------------------------------------------------------------------ 进程等待（服务"秒级复活"用）
        /// <summary>以"只等它退出"的最小权限打开进程（SYNCHRONIZE）。失败返回 Zero。</summary>
        public static IntPtr OpenForWait(int pid)
        {
            return OpenProcess(0x00100000 /*SYNCHRONIZE*/, false, pid);
        }

        /// <summary>等进程退出；true = 已退出，false = 超时（进程还活着）。</summary>
        public static bool WaitForExit(IntPtr h, int timeoutMs)
        {
            if (h == IntPtr.Zero) return true;
            return WaitForSingleObject(h, (uint)timeoutMs) == 0 /*WAIT_OBJECT_0*/;
        }

        public static void Close(IntPtr h)
        {
            if (h != IntPtr.Zero) CloseHandle(h);
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ 内部
        private static bool ApplyToPid(int pid, string sddl, out string detail)
        {
            detail = "";
            IntPtr h = OpenProcess(0x00040000 /*WRITE_DAC*/ | 0x00020000 /*READ_CONTROL*/, false, pid);
            if (h == IntPtr.Zero)
            {
                detail = "无法打开进程 " + pid + "（Win32=" + Marshal.GetLastWin32Error() + "）";
                return false;
            }
            try { return Apply(h, sddl, out detail); }
            finally { CloseHandle(h); }
        }

        private static bool Apply(IntPtr hProcess, string sddl, out string detail)
        {
            detail = "";
            IntPtr psd;
            uint len;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision, out psd, out len))
            {
                detail = "SDDL 转换失败（Win32=" + Marshal.GetLastWin32Error() + "）：" + sddl;
                return false;
            }
            try
            {
                if (!SetKernelObjectSecurity(hProcess, DaclSecurityInformation, psd))
                {
                    detail = "设置进程 DACL 失败（Win32=" + Marshal.GetLastWin32Error() + "）";
                    return false;
                }
            }
            finally { if (psd != IntPtr.Zero) LocalFree(psd); }

            string now = ReadSddl(hProcess);
            detail = (now ?? sddl);
            return true;
        }

        // ------------------------------------------------------------------ P/Invoke
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor, int revision, out IntPtr securityDescriptor, out uint size);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetKernelObjectSecurity(IntPtr handle, int securityInformation, IntPtr securityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int GetSecurityInfo(IntPtr handle, int objectType, int securityInfo,
            out IntPtr sidOwner, out IntPtr sidGroup, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
            IntPtr securityDescriptor, int revision, int securityInfo, out IntPtr str, out uint len);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);
    }
}
