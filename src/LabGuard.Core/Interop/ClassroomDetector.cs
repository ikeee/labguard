using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LabGuard.Core.Data;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Interop
{
    /// <summary>识别到的一个候选课堂软件。</summary>
    public sealed class ClassroomCandidate
    {
        public string Path { get; set; }
        public string ProcessName { get; set; }
        public string ProductName { get; set; }
        public string Source { get; set; }
        public override string ToString()
        {
            return (ProductName ?? "未知产品") + "  ·  " + Path + "   （" + Source + "）";
        }
    }

    /// <summary>
    /// 自动识别学生机上的课堂软件（极域 / 红蜘蛛 / 锐捷云课堂 / 噢易 Os-Easy / 联想云课堂 …）客户端路径。
    /// 识别不到时由界面提示老师**手动填写**（设置程序与安装向导都会给出填写框）。
    ///
    /// 识别的五个来源（按可信度排序）：
    ///   1) 正在运行的进程（能直接拿到完整路径，且是"真的在用"）；
    ///   2) 已知软件目录的常见安装路径；
    ///   3) 控制面板卸载记录里的安装位置（识别厂商/产品名）；
    ///   4) 极域等产品留在注册表里的路径信息；
    ///   5) **关键服务所在目录**（噢易这类"母盘/镜像装机"的产品不写卸载记录，
    ///      但它的服务一定会注册，从服务 ImagePath 反推安装目录最可靠）。
    ///
    /// 注意：噢易（Os-Easy）教学系统 + VOI 云桌面 + 硬件虚拟化是同一厂商的三块，
    /// 本机实测都没有卸载记录，只能靠 1) / 2) / 5) 认出来。
    /// </summary>
    public static class ClassroomDetector
    {
        /// <summary>常见客户端可执行文件名（小写比较）。</summary>
        /// <remarks>
        /// **故意不含 Teacher.exe**：LabGuard 装在学生机上，教师端（噢易 Teacher.exe）与学生端同在
        /// 一个安装目录里。若把它当候选，一旦学生端被杀、目录扫描先撞上 Teacher.exe，
        /// 就会出现"把教师端当学生端拉起来"的荒唐结果。
        /// </remarks>
        private static readonly string[] KnownExeNames =
        {
            "studentmain.exe", "redagent.exe", "classmangerapp.exe", "classmanagerapp.exe",
            "student.exe", "client.exe", "cloudclass.exe", "ruijiestudent.exe",
            // 噢易（Os-Easy）：学生端 / 多媒体客户端 / 云桌面客户端
            "multiclient.exe", "ctsc_multi.exe", "voiclient.exe", "trayclient.exe"
        };

        /// <summary>产品关键字（用于匹配卸载记录 / 目录名）。</summary>
        private static readonly string[] ProductKeywords =
        {
            "极域", "topdomain", "e-learning", "红蜘蛛", "3000soft", "red spider", "锐捷", "ruijie",
            "云课堂", "classmanager", "classmanger", "联想", "lenovo", "还原", "机房",
            // 噢易（Os-Easy）
            "噢易", "os-easy", "oseasy", "voi", "云桌面", "无盘", "diskless", "hardvirtual"
        };

        /// <summary>要探测的进程名（不含 .exe）。</summary>
        private static readonly string[] ProcessProbeNames =
        {
            "StudentMain", "REDAgent", "ClassMangerApp", "ClassManagerApp", "e-Learning",
            // 噢易：Student=学生端（本机实测在跑）
            "Student", "MultiClient", "Ctsc_Multi", "VoiClient", "TrayClient"
        };

        public static List<ClassroomCandidate> DetectAll()
        {
            var found = new List<ClassroomCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string, string, string, string> add = (path, proc, product, source) =>
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full;
                try { full = System.IO.Path.GetFullPath(path); } catch { return; }
                if (!File.Exists(full)) return;
                if (!seen.Add(full)) return;
                found.Add(new ClassroomCandidate { Path = full, ProcessName = proc, ProductName = product, Source = source });
            };

            // 1) 正在运行的进程
            try
            {
                foreach (string name in ProcessProbeNames)
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { add(p.MainModule.FileName, p.ProcessName + ".exe", GuessProduct(p.MainModule.FileName), "正在运行"); }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
            }
            catch { }


            // 2) 已知常见路径
            foreach (var hint in DefaultBlocklists.ClassroomHints)
            {
                add(hint.Value, hint.Key, GuessProduct(hint.Value), "常见安装位置");
            }

            // 3) 卸载记录里的安装位置
            try
            {
                string[] uninstallRoots =
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };
                foreach (string root in uninstallRoots)
                {
                    using (RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (baseKey == null) continue;
                        foreach (string sub in baseKey.GetSubKeyNames())
                        {
                            using (RegistryKey k = baseKey.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string display = Convert.ToString(k.GetValue("DisplayName"));
                                if (string.IsNullOrEmpty(display) || !MatchesProduct(display)) continue;
                                string loc = Convert.ToString(k.GetValue("InstallLocation"));
                                if (string.IsNullOrEmpty(loc)) continue;
                                foreach (ClassroomCandidate c in ScanDirQuiet(loc, "卸载记录：" + display)) found.Add(c);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug("扫描卸载记录失败：" + ex.Message); }

            // 4) 产品注册表里的路径提示
            foreach (string hint in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\StudentMain.exe",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\StudentMain.exe"
            })
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(hint))
                    {
                        if (k != null) add(Convert.ToString(k.GetValue("")), "StudentMain.exe", "极域电子教室", "注册表 App Paths");
                    }
                }
                catch { }
            }

            // 5) 关键服务所在目录（母盘/镜像装机的产品没有卸载记录，但服务一定注册）
            foreach (var svc in DefaultBlocklists.ClassroomServices)
            {
                try
                {
                    string imagePath = SystemActions.QueryWmi(
                        "SELECT PathName FROM Win32_Service WHERE Name='" + svc.Key.Replace("'", "''") + "'", "PathName");
                    string dir = ServiceDirectory(imagePath);
                    if (dir == null) continue;
                    foreach (ClassroomCandidate c in ScanDirQuiet(dir, "服务：" + svc.Key + "（" + svc.Value + "）")) found.Add(c);
                }
                catch { }
            }

            // 去重（按路径）
            var unique = new List<ClassroomCandidate>();
            foreach (ClassroomCandidate c in found)
            {
                if (unique.Any(x => string.Equals(x.Path, c.Path, StringComparison.OrdinalIgnoreCase))) continue;
                unique.Add(c);
            }
            Log.Info("课堂软件识别结果：" + (unique.Count == 0 ? "未识别到（需手动填写）" : string.Join("；", unique.Select(c => c.Path))));
            return unique;
        }

        /// <summary>
        /// 取最可信的一个（找不到返回 null，调用方应提示老师手动填写）。
        /// 排序：**学生端本体优先（Rank）→ 正在运行的优先** → 云桌面客户端 → 其它组件。
        /// 红队 A5 教训：原先"正在运行的优先"会在学生端被杀后把识别漂移到还在跑的
        /// VoiClient 上（云桌面客户端不是"课堂管控"本体）——学生端本体只要在磁盘上找得到，
        /// 哪怕此刻没运行，也必须保住"关键客户端"的位置（守护的职责恰恰是把它拉回来）。
        /// </summary>
        public static ClassroomCandidate DetectBest(out string reason)
        {
            return PickBest(DetectAll(), out reason);
        }

        /// <summary>从候选清单里挑出最可信的一个（纯排序，抽出便于自检用合成候选断言）。</summary>
        public static ClassroomCandidate PickBest(IEnumerable<ClassroomCandidate> all, out string reason)
        {
            ClassroomCandidate best = (all ?? Enumerable.Empty<ClassroomCandidate>())
                .OrderBy(Rank)
                .ThenBy(c => c.Source == "正在运行" ? 0 : 1)
                .FirstOrDefault();
            reason = best == null
                ? "未识别到课堂软件，请在设置里手动填写学生端程序路径"
                : "识别到：" + best.Path + "（" + best.Source + "）";
            return best;
        }

        /// <summary>候选可信度：0 = 学生端本体，1 = 其它组件，2 = 云桌面客户端（不是"课堂管控"本体）。</summary>
        public static int Rank(ClassroomCandidate c)
        {
            string n = (c.ProcessName ?? "").ToLowerInvariant();
            if (n == "studentmain.exe" || n == "student.exe" || n == "redagent.exe" ||
                n == "classmangerapp.exe" || n == "classmanagerapp.exe" || n == "ruijiestudent.exe") return 0;
            if (n == "voiclient.exe" || n == "trayclient.exe" || n.StartsWith("diskless")) return 2;
            return 1;
        }

        private static bool MatchesProduct(string text)
        {
            string t = (text ?? "").ToLowerInvariant();
            return ProductKeywords.Any(k => t.Contains(k.ToLowerInvariant()));
        }

        /// <summary>按路径里的特征关键字猜产品名（公开给自检用）。</summary>
        public static string GuessProduct(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            if (p.Contains("topdomain") || p.Contains("studentmain")) return "极域电子教室";
            if (p.Contains("3000soft") || p.Contains("redagent")) return "红蜘蛛电子教室";
            if (p.Contains("classman")) return "锐捷云课堂 / ClassManager";
            // 噢易（Os-Easy）：云桌面 / 硬件虚拟化 / 教学系统是同一厂商的三块，判定顺序由特殊到一般
            if (p.Contains("voi") || p.Contains("diskless") || p.Contains("trayclient")) return "噢易 VOI 云桌面客户端";
            if (p.Contains("hardvirtual") || p.Contains("runclient")) return "噢易硬件虚拟化";
            if (p.Contains("os-easy") || p.Contains("oseasy") || p.Contains("噢易")) return "噢易多媒体教学系统";
            return "课堂软件客户端";
        }

        /// <summary>
        /// 从服务的 ImagePath 里取出可执行文件所在目录。
        /// 形如 `C:\...\diskless_service.exe`、`"C:\...\RunClient.exe" /service` 都要能认。
        /// </summary>
        public static string ServiceDirectory(string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath)) return null;
                string p = imagePath.Trim();
                if (p.StartsWith("\"", StringComparison.Ordinal))
                {
                    int end = p.IndexOf('"', 1);
                    if (end > 1) p = p.Substring(1, end - 1);
                }
                else
                {
                    // 未加引号时，参数一般以 " /" 或 " -" 开头，按此截断（路径本身允许含空格）
                    int idx = p.IndexOf(" /", StringComparison.Ordinal);
                    if (idx < 0) idx = p.IndexOf(" -", StringComparison.Ordinal);
                    if (idx > 0) p = p.Substring(0, idx);
                }
                p = p.Trim();
                if (!System.IO.Path.IsPathRooted(p)) return null;
                string dir = System.IO.Path.GetDirectoryName(p);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch { return null; }
        }

        /// <summary>在目录里找已知的客户端 exe（只扫两层，避免拖慢）。</summary>
        private static List<ClassroomCandidate> ScanDirQuiet(string dir, string source)
        {
            var result = new List<ClassroomCandidate>();
            try
            {
                if (!Directory.Exists(dir)) return result;
                foreach (string f in SafeFiles(dir))
                {
                    if (!KnownExeNames.Contains(System.IO.Path.GetFileName(f).ToLowerInvariant())) continue;
                    result.Add(new ClassroomCandidate
                    {
                        Path = f,
                        ProcessName = System.IO.Path.GetFileName(f),
                        ProductName = GuessProduct(f),
                        Source = source
                    });
                }
                foreach (string sub in SafeDirs(dir))
                {
                    foreach (string f in SafeFiles(sub))
                    {
                        if (!KnownExeNames.Contains(System.IO.Path.GetFileName(f).ToLowerInvariant())) continue;
                        result.Add(new ClassroomCandidate
                        {
                            Path = f,
                            ProcessName = System.IO.Path.GetFileName(f),
                            ProductName = GuessProduct(f),
                            Source = source
                        });
                    }
                }
            }
            catch { }
            return result;
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "*.exe"); } catch { return new string[0]; }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); } catch { return new string[0]; }
        }
    }
}
