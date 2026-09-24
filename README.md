# 机房管理助手（复刻版）

**当前版本：v0.01**（2026-09-24 首个公开版本）· 项目地址：https://github.com/ikeee/labguard · 许可：MIT（见 [LICENSE](LICENSE)）· 更新记录：[CHANGELOG.md](CHANGELOG.md)

对 Windows 学生机房常见"学生端管控软件"的**开源复刻实现**。功能定位与原版《学生机房管理助手 v13.03》等价：
保护电子教室（极域/红蜘蛛/锐捷云课堂）不被学生脱控、规范机房上机行为、统一壁纸与机器编号。

> 本目录是**独立实现**，不含、也不依赖原版程序的任何文件；原理分析见 [docs/01-原理分析-原版学生机房管理助手v13.03.md](docs/01-原理分析-原版学生机房管理助手v13.03.md)。

---

## 1. 组成

| 程序 | 对应原版 | 作用 |
|---|---|---|
| `LabGuard.Service.exe` | `zmserv.exe` | Windows 服务（SYSTEM）：hosts 黑名单、USB 存储禁用、注册表权限加固、安全模式限制、浏览器组策略、新文件监控、守护代理进程 |
| `LabGuard.Agent.exe` | `about.exe` + `jfglzsn.exe` + `przs.exe` | 托盘小助手：电子教室保护、网络/IP/防火墙守护、违规软件拦截、任务栏与 Win 键策略、桌面壁纸与编号、互相守护；系统功能菜单（设置/暂停/退出，均需密码） |
| `LabGuard.Settings.exe` | `set.exe` | 设置程序：密码 + 15 组策略开关 + 壁纸选择 |
| `LabGuard.Uninstall.exe` | `uninstall.exe` | 卸载并**逐项还原**系统设置 |
| `LabGuard.Launcher.exe` | `zy\*.exe` | 浏览器"上网入口"（可选，默认不接管快捷方式；原版用 9 个冒充浏览器的假程序做主页推广，本复刻不冒充） |
| `LabGuard.SelfTest.exe` | — | 自检：验证口令/hosts/拦截清单，并用**干跑模式**完整启动-停止引擎（不修改系统） |

## 2. 快速开始

```powershell
# 1) 构建（需要 .NET SDK 8；产物为 net48，Win7/10/11 直接可跑）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SelfTest

# 2) 安装（管理员 PowerShell，在 dist 目录里执行）
powershell -ExecutionPolicy Bypass -File install.ps1
#   不想用"内存指纹"随机目录时：install.ps1 -SimplePath -InstallDir C:\LabGuard

# 3) 打开【设置】程序，设置密码并逐项选择策略（未设密码前监控不启用）

# 4) 卸载
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

**先在 1 台学生机上试用一周**，确认与你的电子教室 / 考试软件（mpython、3D One、人机对话等）兼容后再批量部署。

> `LabGuard.Settings.exe` 与 `LabGuard.Uninstall.exe` 需要管理员权限（会弹 UAC）；`LabGuard.Agent.exe` 由安装时创建的"最高权限计划任务"拉起。

## 2.1 所有功能都由你自己开关（80 项全覆盖）

- 配置项共 **94 个属性**，其中 **80 个是真开关**，全部在设置程序里可见可改，分成 **14 组**：
  总体 / 电子教室保护 / 网络与防火墙 / 集中 DNS / 违规软件拦截 / 新文件监控 / USB / hosts / 浏览器 /
  任务栏与系统工具 / 安全模式 / 桌面壁纸与编号 / 注册表权限加固 / 互相守护。
- 每个开关 = 一个控件（勾选 / 下拉 / 数字 / 路径 / 多行列表），带中文说明；会造成关机、重启这类后果的项**标红加【危险】**，默认关闭。
- 支持 **导出配置 / 导入配置**（一个 JSON 拷到所有学生机）、**恢复默认**（保留密码）。
- 这条不是"承诺"，是**有自检兜底的保证**：
  - `LabGuard.SelfTest.exe` 的 `[7] 设置项覆盖` 会反射遍历 `GuardConfig`，若某个配置项没有对应开关、或某个开关指向了不存在的字段、或 Get/Set 接错字段 → **直接报 FAIL**（当前：80 项全覆盖、0 隐藏项、80 个开关读写正确）。
  - `LabGuard.Settings.exe --selftest-ui` 会真正构建一次界面并统计控件数（不显示窗口），用于确认"每个开关都渲染出来了"。

## 3. 功能对照（摘要，完整版见 docs/03）

| 能力 | 复刻实现 |
|---|---|
| 电子教室进程被挂起 | 轮询线程状态，检出即 `NtResumeProcess` 强制恢复 |
| 电子教室进程被结束 | 按配置路径/自动探测重新拉起 |
| 极域关键服务停止 | 自动 `StartService` |
| 极域频道/自动登录参数被改 | 检测并回写合法值 |
| 拔网线 / 禁用网卡 | `NetworkGuard.IsDisconnected()`：网卡变 Down / IP 清空 / `0.0.0.0` / APIPA `169.254.*`，**持续 20 秒**确认后告警（开机 120 秒预热，避免误判；默认只提示，可选锁屏/关机） |
| 断网**全屏遮罩**（屏保式） | 六张壁纸随机一张做背景（可切纯色页）+ 大字提示「网络已断开」+ 机位号 + 已断开时长；**吞掉所有键**，但**插回网线 10 秒内自动消失**（不需要老师）；老师连续按 5 次 `Esc` + 密码可解除（解除后监控暂停）。`LabGuard.Agent.exe --preview-mask 8` 可先预览 |
| 断网**响鸣** | 断网满 N 秒后「嘀嘀嘀」**有限次数**（默认 60 秒后响 3 次 × 700ms，可关/可调），避免变成"学生拔线制造噪音"的新玩法 |
| 改 IP | 与启动时基线比对，`netsh` 还原原 IP（DHCP 则 `source=dhcp`） |
| 开防火墙"阻止所有传入连接" | 读 `FirewallPolicy` 三个 profile，`netsh advfirewall set allprofiles state off` |
| 破解/脱控工具、进程工具、虚拟桌面、杀软管家、解压软件 | 进程名 + 窗口标题双表拦截（清单来自原版字符串还原） |
| 小游戏（扫雷/纸牌/…/Chrome 小恐龙/Edge 冲浪） | 进程拦截 + `Image File Execution Options\Debugger=null` + 浏览器组策略 |
| 任务管理器 / 注册表 / cmd | 组策略键 + 窗口标题拦截（可逐项开关） |
| 任务视图（虚拟桌面）与 Win 键 | `ShowTaskViewButton=0` + 低级键盘钩子吞掉 Win 键 |
| 新 exe/reg/bat/zip… 文件 | `FileSystemWatcher` 监控下载目录与根目录，三档模式（全部允许 / 仅 C++ / 全部禁止） |
| USB 存储设备 | `usbstor` 驱动 `Start=4`（键鼠保留），被改回即告警并重新禁用 |
| **上网管控（主防线）** | **集中 DNS 锁定**：学生机 DNS 必须指向教师机 AdGuard（或校内 DNS），每 15 秒核对、被改即 `netsh` 改回；并关闭 Chrome/Edge/Firefox/Win11 的 **DoH**（否则一次点击就能绕过过滤）；另带"AdGuard 是否存活"的 UDP 53 探针 |
| 下载站/网盘/游戏站 | **默认关闭**（有集中 DNS 就不需要）。开启时：hosts 受管区块（原版 75 条清单 + 自定义）、隐藏、被删被改即恢复；**不开也保护 hosts**：ACL 收紧为普通用户只读 + **内容守候**（学生哪怕用管理员权限往里写映射，也在 20 秒内被还原并记日志） |
| 浏览器下载/另存为/开发者工具/小恐龙/冲浪 | Chrome/Edge/Firefox/IE 组策略（不碰浏览器本体） |
| 带网络的安全模式 | 删除 `SafeBoot\Network`（删除前导出 `.reg` 备份，退出/卸载时还原） |
| 桌面壁纸 + 机器编号 | 6 张自绘规范壁纸，实时合成"计算机名后 6 位"到右上角 |
| 自我保护 | 服务守护（代理被结束自动拉起）、关键文件 SHA256 校验、暂停标记防互相打架、注册表与目录 ACL 加固 |
| 老师退出/暂停 | 任务栏小助手 → 系统功能（F）→ 暂停监控 / 退出程序（都要密码），退出时逐项还原 |
| 口令保护 | **退出、暂停、改设置、解除遮罩、卸载——全部需要密码**；改密码必须提供旧密码（`--init` 需 `--old`）；静默卸载必须带 `--password`（或老师显式 `--force`）；部署脚本/文档/预设**不装到学生机**，只在老师自己的介质上 |
| 抗破解（停服务/删目录） | 服务 DACL 去掉了普通用户的"停止"权、异常终止 5 秒自动重启、**服务镜像放在仅 SYSTEM/管理员可访问的备份副本目录**、程序文件被删/被改**自动恢复**、配置文件有注册表加密镜像（删了自动恢复并报警）、可选**心跳上报**给教师端监视器 —— 详见 [docs/05-防破解与应急.md](docs/05-防破解与应急.md) |
| 老师后路 | **安全模式（无网络）不加载任何管控**（默认；可在设置第 10 组改为"安全模式也管控"）+ `scripts\cleanup-all.ps1` 一键彻底卸载还原（纯 PowerShell、不依赖安装目录里的文件、安全模式可用） |

## 4. 与原版刻意不同的地方（重要）

### 4.0 场景：一体机机房（有线为主 + 无线常开做热点）

这类机房的电脑有两块网卡：有线（主链路）+ 无线（STEAM 课要开热点给机器人/平板用）。
程序按"**判据网卡**"思路处理，避免两种误判：

| 风险 | 处理 |
|---|---|
| 热点/无线一关就被当成"拔网线" → 误弹遮罩 | 默认**忽略** Wi-Fi Direct / 移动热点 / Hyper-V / VMware / 蓝牙等网卡（关键字可配）；Windows 移动热点的固定网关 `192.168.137.1` 也会被直接识别为热点网卡 |
| 有线拔了、热点还开着 → 遮罩误以为"网络已恢复"而消失 | 遮罩的"恢复条件"只看**判据网卡**是否恢复 IP，不看"本机还有没有 IP" |
| 热点客户端（机器人/平板）被塞进教师机 DNS | `DNS 只锁在上网网卡上`（默认开），热点网卡不动 |
| 只拔有线就想脱离管控 | `要全部判据网卡断开才算断网`（默认开）：有线断了但无线仍联网 = 机器还有网，不判违规；只提示日志。想严格就关掉这项 |

建议配法（这块机房）：**`只监视有线网卡` 保持勾选即可**（默认就是勾上的——无线/热点完全不参与断网判定，不必手填网卡名）；
想更精确再填 `只监控这些网卡`（如 `以太网`）；`忽略的网卡关键字` 保留默认。若某台机器没有有线网卡，程序会**自动退回"自动选择"并记日志**（不会静默失去监控）。

⚠️ 一个必须想清楚的点：**只监视有线 = 有线就是"唯一正经链路"**。如果这台机器的无线还自动连着校园网，那么"拔有线"就不是真正的脱网（学生可以用无线上网）。两种情况：

1. 无线只用来开热点（不连校园 WiFi）→ 只监视有线**完全正确**，这是你这种情况的推荐配法；
2. 无线也会自动连校园网 → 建议要么在无线网卡上取消该网络的"自动连接"（保留热点能力），要么把 `要全部判据网卡断开才算断网` 打开（默认已开）、并把无线也纳入判据。

`LabGuard.SelfTest.exe` 的 `[8] 网卡判据` 会打印**这台机器实际会监控哪些网卡**，部署前先看它一眼。

1. **不动 Windows Defender、不要求卸载杀毒软件**：原版要求先禁用 Defender 并卸载安全软件；本复刻只写策略键，建议改为在安全软件里给本程序加白名单。
2. **不做主页劫持、不冒充其它浏览器**：原版用 9 个 `zy\*.exe`（元数据写着 `Google Chrome`）把学生浏览器主页改成自家导航站变现；本复刻的上网入口只做"按设置里的主页启动真浏览器"，默认不接管快捷方式。
3. **不隐藏痕迹**：原版会改 `HideFileExt`/`NoFolderOptions`/`ShowSuperHidden` 让 hosts 改动难以发现，并把 hosts 设成隐藏；本复刻默认仍可关闭这些"隐藏"项（配置里 `Hosts.HideHostsFile`、`Shell.HideFileExtensions`）。
4. **服务异常不重启电脑**：原版 `sc failure … actions= reboot`，本复刻默认只重启服务（可配置）。
5. **安全模式限制可还原**：原版删除 `SafeBoot\Network` 后无法还原；本复刻删除前导出备份。
6. **干跑模式**：`LabGuard.Agent.exe --dry-run` 只记录日志、不修改系统，方便先在教师机上验证策略判定。

## 5. 自检

```powershell
src\LabGuard.SelfTest\bin\Release\net48\LabGuard.SelfTest.exe          # 全部通过 = 逻辑正确
src\LabGuard.SelfTest\bin\Release\net48\LabGuard.SelfTest.exe --verbose # 看每条系统动作（干跑）
src\LabGuard.SelfTest\bin\Release\net48\LabGuard.SelfTest.exe --only UsbGuard  # 单独调试某个模块
```

自检以 `DryRun` 运行，**不会**修改 hosts、注册表、USB、壁纸（已实测：自检前后系统状态快照完全一致）。

`scripts\test-hosts-watchdog.ps1`（管理员运行）是**真跑**的端到端验证：运行中往 hosts 写一条假映射，
30 秒内应被还原；结束后 hosts 与运行前**字节一致**、ACL 恢复默认继承（实测通过）。

## 6. 目录

```
src/LabGuard.Core            策略引擎与 13 个 Guard、配置、口令、hosts、UI 共用件
src/LabGuard.Service         守护服务（SYSTEM）
src/LabGuard.Agent           托盘小助手（用户会话）
src/LabGuard.Settings        设置程序
src/LabGuard.Uninstall       卸载与还原
src/LabGuard.Launcher        上网入口
src/LabGuard.SelfTest        自检
scripts/build.ps1            构建 + 发布 dist
scripts/install.ps1          安装（服务 + 计划任务 + 快捷方式 + 壁纸 + ACL）
scripts/uninstall.ps1        卸载
scripts/make-wallpapers.ps1  生成 6 张机房规范壁纸（1920×1080）
docs/                        原理分析 / 设计说明 / 功能对照表
```

## 7. 安全与合规

- 仅用于学校自有教学机房的学生机管理；使用前应告知师生、并在机房管理制度中说明。
- 口令只存 PBKDF2-SHA256 散列（10 万次迭代，随机盐），配置用 DPAPI 机器密钥加密，目录/注册表 ACL 限制普通用户写入。
- 老师随时可以用密码暂停或退出（`--unlock` 快捷方式 / 托盘菜单 / 设置程序），卸载程序会把所有改动还原。
- 日志：`%ProgramData%\LabGuard\logs\guard-YYYYMMDD.log`。
