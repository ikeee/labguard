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
    /// 自动识别学生机上的课堂软件（极域 / 红蜘蛛 / 锐捷云课堂 / 联想云课堂 …）客户端路径。
    /// 识别不到时由界面提示老师**手动填写**（设置程序与安装向导都会给出填写框）。
    ///
    /// 识别的四个来源（按可信度排序）：
    ///   1) 正在运行的进程（能直接拿到完整路径，且是"真的在用"）；
    ///   2) 已知软件目录的常见安装路径；
    ///   3) 控制面板卸载记录里的安装位置（识别厂商/产品名）；
    ///   4) 极域等产品留在注册表里的路径信息。
    /// </summary>
    public static class ClassroomDetector
    {
        /// <summary>常见客户端可执行文件名（小写比较）。</summary>
        private static readonly string[] KnownExeNames =
        {
            "studentmain.exe", "redagent.exe", "classmangerapp.exe", "classmanagerapp.exe",
            "student.exe", "client.exe", "cloudclass.exe", "ruijiestudent.exe"
        };

        /// <summary>产品关键字（用于匹配卸载记录 / 目录名）。</summary>
        private static readonly string[] ProductKeywords =
        {
            "极域", "topdomain", "e-learning", "红蜘蛛", "3000soft", "red spider", "锐捷", "ruijie",
            "云课堂", "classmanager", "classmanger", "联想", "lenovo", "还原", "机房"
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
                foreach (string name in new[] { "StudentMain", "REDAgent", "ClassMangerApp", "ClassManagerApp", "e-Learning" })
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

        /// <summary>取最可信的一个（找不到返回 null，调用方应提示老师手动填写）。</summary>
        public static ClassroomCandidate DetectBest(out string reason)
        {
            List<ClassroomCandidate> all = DetectAll();
            // 优先"正在运行"
            ClassroomCandidate best = all.FirstOrDefault(c => c.Source == "正在运行") ?? all.FirstOrDefault();
            reason = best == null
                ? "未识别到课堂软件，请在设置里手动填写学生端程序路径"
                : "识别到：" + best.Path + "（" + best.Source + "）";
            return best;
        }

        private static bool MatchesProduct(string text)
        {
            string t = (text ?? "").ToLowerInvariant();
            return ProductKeywords.Any(k => t.Contains(k.ToLowerInvariant()));
        }

        private static string GuessProduct(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            if (p.Contains("topdomain") || p.Contains("studentmain")) return "极域电子教室";
            if (p.Contains("3000soft") || p.Contains("redagent")) return "红蜘蛛电子教室";
            if (p.Contains("classman")) return "锐捷云课堂 / ClassManager";
            return "课堂软件客户端";
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
