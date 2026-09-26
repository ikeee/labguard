# LabGuard

**当前版本：v0.01**（2026-09-24 首个公开版本）· 项目地址：https://github.com/ikeee/labguard · 许可：MIT（见 [LICENSE](LICENSE)）· 更新记录：[CHANGELOG.md](CHANGELOG.md)

> **English TL;DR** — LabGuard is an open-source, self-healing policy guard for Windows PCs in school computer labs
> (.NET Framework 4.8). It protects the classroom-management client (TopDomain / Red Spider / Ruijie cloud class)
> from being killed or suspended by students, enforces lab policies (USB storage, downloads, small games,
> Task Manager / Registry, browser downloads), and ships with a one-file setup wizard where
> **every single policy has its own on/off switch**.
> **AdGuard is optional**: the program works with no DNS filtering at all; if you run AdGuard on the teacher PC,
> you can additionally turn on "lock students' DNS to it" (off by default). Details below.
> It is an independent, clean-room implementation — it contains no code, assets or binaries from any third-party product.
> There is always a teacher escape hatch: `scripts/cleanup-all.ps1` fully uninstalls and reverts everything.
> Docs are in Chinese (target: Chinese K-12 lab teachers).

**LabGuard** 是一套面向学校计算机机房的开源**学生机管控程序**：
保护课堂软件（极域 / 红蜘蛛 / 锐捷云课堂）不被学生脱离控制、规范机房上机行为
（下载、小游戏、任务管理器、U 盘、浏览器下载等）、可选集中 DNS 上网过滤。**不修改学生机的系统壁纸与浏览器主页。**

> 本仓库只包含**自己的实现与文档**：不含任何第三方程序的代码、素材或二进制文件。
> 功能清单与全部开关见 [docs/03-功能与开关清单.md](docs/03-功能与开关清单.md)。

---

## 1. 组成

| 程序 | 角色 | 作用 |
|---|---|---|
| `LabGuard.Service.exe` | 守护服务 | Windows 服务（SYSTEM）：hosts 黑名单、USB 存储禁用、注册表权限加固、安全模式限制、浏览器组策略、新文件监控、守护代理进程 |
| `LabGuard.Agent.exe` | 托盘交互 | 托盘小助手：课堂软件保护、网络/IP/防火墙守护、违规软件拦截、任务栏与 Win 键策略、互相守护；系统功能菜单（设置/暂停/退出，均需密码） |
| `LabGuard.Settings.exe` | 配置界面 | 设置程序：密码 + **14 组 / 92 项开关**（左分组导航 + 搜索 + 只看已改动 + 改动计数）+ 导入导出 |
| `LabGuard.Uninstall.exe` | 卸载 | 卸载并**逐项还原**系统设置 |
| `LabGuard.Launcher.exe` | 上网入口（可选） | 按设置里的主页启动学生机上的**真实浏览器**；默认不接管快捷方式，也不冒充任何软件 |
| `LabGuard.SelfTest.exe` | — | 自检：验证口令/hosts/拦截清单，并用**干跑模式**完整启动-停止引擎（不修改系统） |

## 1.5 ⚠️ AdGuard（以及"上网管控"）是**可选项**，不是前置条件

**不装 AdGuard 也能完整使用本程序。** 上网怎么管，你自己选一档，甚至完全不管：

| 档位 | 需要什么 | 在设置里怎么选 | 说明 |
|---|---|---|---|
| **A. 不管上网**（默认就是这档） | 什么都不需要 | 第 3 组「集中 DNS」不勾 / 第 7 组「hosts 黑名单」不勾 | 程序只做**课堂软件保护 + 行为规范（USB/下载/小游戏/任务管理器…）+ 断网遮罩**。域名过滤完全不做 |
| **B. 本地 hosts 黑名单** | 什么都不需要 | 第 7 组勾「启用 hosts 域名黑名单」 | 用内置 75 条域名清单（下载站/网盘/小游戏站）+ 自定义域名，写进本机 hosts。**不需要 AdGuard、不需要服务器**；缺点：学生若拿到管理员权限可直接改 hosts（程序会把 hosts 设为只读并守候还原） |
| **C. 集中 DNS 过滤**（可选加强） | 教师机（或校内服务器）上跑 **AdGuard Home**（或其他 DNS 过滤服务） | 第 3 组：填教师机 IP + 勾「锁定学生机 DNS」+ 勾「关闭 DoH」 | 过滤对学生机**所有程序**统一生效、学生本地无文件可改；程序会每 15 秒核对 DNS 被改就改回，并关闭浏览器/系统加密 DNS（DoH）。**代价：教师机是单点**，AdGuard 不开机 → 学生机域名解析失效（表现为"没网"） |

要点：

- **默认配置（`A` 档）不需要 AdGuard**：装上就能用，不会因为"没配 DNS"而报错或功能缺失。
- 选 `C` 档才需要 AdGuard；而且**只填一个 IP**，不填就不锁定（适合校园网 DHCP 统一下发 DNS 的场景）。
- `B` 和 `C` 可以同时开（互为补充），也可以只开一个。
- 相关开关都在设置程序第 3 组 / 第 7 组，随你改；`--show-config` 能一眼看到当前处于哪一档。

## 2. 安装（用一键安装程序 `LabGuard-Setup.exe`）

**推荐路径（老师用这个）**：从 [Releases](https://github.com/ikeee/labguard/releases) 下载
**`LabGuard-Setup-v0.02.exe`**（单文件，内置全部程序）→ 拷到学生机 → **双击**（会弹 UAC）→ 按向导走：

| 向导步骤 | 你要做的事 |
|---|---|
| 1. 欢迎 | 看一遍它会做什么（含"不卸杀毒、不改 UAC、不劫持主页"），勾选"我已阅读并了解" |
| 2. 安装位置 | 默认 `C:\Program Files\LabGuard`（普通用户不可写、不可随手删）；也可自定义 |
| 3. 设置密码 | 输两遍；6 位及以上字母数字，弱口令会被拒绝 |
| 3. **课堂软件** | 点【自动识别】查找极域 / 红蜘蛛 / 锐捷云课堂等学生端；**识别不到就手动填写或浏览选择**（留空也行，运行时会再自动探测） |
| 4. 设置密码 | 输两遍，6 位以上字母数字 |
| 5. **功能开关（92 项）** | 左分组导航 + 搜索 + 只看已改动；先套预设（普通PC机房 / 一体机机房 / 只管电子教室 / 全部放开），再逐项勾选；**AdGuard 相关的"集中 DNS"就在这里，不勾就是 A 档** |
| 5. 安装 | 点【开始安装】→ 实时日志 → 完成后可勾"立即启用监控"，并可一键打开设置复核 |

装完自检（3 分钟）：

```powershell
# 在安装目录里（默认 C:\Program Files\LabGuard；实际路径可用 --show-config 查看）
LabGuard.SelfTest.exe            # 全绿才算装好
LabGuard.Settings.exe --show-config   # 核对当前策略（含"集中 DNS 是否为未配置"）
```

卸载：开始菜单 →「LabGuard」→ 卸载（**要密码**）。

> **顺序很重要**：装好后默认禁用 U 盘，所以**先把安装包拷进机器，再安装**；之后要用 U 盘就用密码暂停小助手。
> **不需要**卸载杀毒软件、**不需要**把 UAC 改成"从不通知"（与不同）；把安装目录加入杀软白名单即可。
> **先在 1 台学生机上试用一周**（含一次真实课堂），确认与你的电子教室 / 考试软件（mpython、3D One、人机对话等）兼容后再批量部署。

### 2.1 批量部署 / 无人值守（可选，脚本方式）

一个机房几十台时，用发布包里的脚本更快（详见 [docs/04-机房部署指南.md](docs/04-机房部署指南.md)）：

```powershell
# 无人值守安装（脚本方式；包在 labguard-v0.01.zip 的 dist\ 目录里）
powershell -ExecutionPolicy Bypass -File install.ps1 -Password "你的密码123" -Dns "192.168.1.10"
#   -Dns 只在你要用 C 档（集中 DNS）时才需要；不填就是 A 档
#   -ConfigJson "presets\主机房-普通PC.json" 可套用预设；-DryRun 可先预演（不改系统）

# 一键安装程序同样支持无人值守：
LabGuard-Setup-v0.01.exe --silent --password "你的密码123" [--config presets\xxx.json] [--dns 192.168.1.10] [--no-start]
```

也可以从源码构建（需要 .NET SDK 8）：`scripts\build.ps1 -SelfTest` → `scripts\build-installer.ps1`（会同时产出安装程序与发布包）。

> `LabGuard.Settings.exe` 与 `LabGuard.Uninstall.exe` 需要管理员权限（会弹 UAC）；`LabGuard.Agent.exe` 由安装时创建的"最高权限计划任务"拉起。

## 2.2 所有功能都由你自己开关（92 项全覆盖，带搜索与"只看已改动"）

- 配置项共 **92 项**（含"集中 DNS""hosts 黑名单"这类**可选上网管控**），全部在设置程序里可见可改，分成 **14 组**：
  总体 / 电子教室保护 / 网络与防火墙 / 集中 DNS / 违规软件拦截 / 新文件监控 / USB / hosts / 浏览器 /
  任务栏与系统工具 / 安全模式 / 机位标识 / 注册表权限加固 / 互相守护。
- 面板顶部有**搜索框**（当前分组内按名称/说明过滤）、**「只看已改动」**（与出厂默认值对比）、以及"本组显示 x/y · 全局已改动 N 项"计数；
  左侧分组导航上会标出每组改了几项，改完一眼能看到自己动过什么。
- 每个开关 = 一个控件（勾选 / 下拉 / 数字 / 路径 / 多行列表），带中文说明；会造成关机、重启这类后果的项**标红加【危险】**，默认关闭。
- 支持 **导出配置 / 导入配置**（一个 JSON 拷到所有学生机）、**恢复默认**（保留密码）。
- 这条不是"承诺"，是**有自检兜底的保证**：
  - `LabGuard.SelfTest.exe` 的 `[7] 设置项覆盖` 会反射遍历 `GuardConfig`，若某个配置项没有对应开关、或某个开关指向了不存在的字段、或 Get/Set 接错字段 → **直接报 FAIL**（当前：80 项全覆盖、0 隐藏项、80 个开关读写正确）。
  - `LabGuard.Settings.exe --selftest-ui` 会真正构建一次界面并统计控件数（不显示窗口），用于确认"每个开关都渲染出来了"。

## 3. 功能一览（摘要，完整版见 [docs/03](docs/03-功能与开关清单.md)）

| 能力 | 实现方式 |
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
| 破解/脱控工具、进程工具、虚拟桌面、杀软管家、解压软件 | 进程名 + 窗口标题双表拦截（清单来自字符串还原） |
| 小游戏（扫雷/纸牌/…/Chrome 小恐龙/Edge 冲浪） | 进程拦截 + `Image File Execution Options\Debugger=null` + 浏览器组策略 |
| 任务管理器 / 注册表 / cmd | 组策略键 + 窗口标题拦截（可逐项开关） |
| 任务视图（虚拟桌面）与 Win 键 | `ShowTaskViewButton=0` + 低级键盘钩子吞掉 Win 键 |
| 新 exe/reg/bat/zip… 文件 | `FileSystemWatcher` 监控下载目录与根目录，三档模式（全部允许 / 仅 C++ / 全部禁止） |
| USB 存储设备 | `usbstor` 驱动 `Start=4`（键鼠保留），被改回即告警并重新禁用 |
| **上网管控（可选三档，见 §1.5）** | ① 不控（默认）② **本地 hosts 黑名单**（内置 75 条 + 自定义，纯本机）③ **集中 DNS 锁定**（可选，配合教师机 AdGuard：DNS 被改即 `netsh` 改回、关闭 Chrome/Edge/Firefox/Win11 的 **DoH**、带 AdGuard 存活探针） |
| 下载站/网盘/游戏站 | **默认关闭**（有集中 DNS 就不需要）。开启时：hosts 受管区块（ 75 条清单 + 自定义）、隐藏、被删被改即恢复；**不开也保护 hosts**：ACL 收紧为普通用户只读 + **内容守候**（学生哪怕用管理员权限往里写映射，也在 20 秒内被还原并记日志） |
| 浏览器下载/另存为/开发者工具/小恐龙/冲浪 | Chrome/Edge/Firefox/IE 组策略（不碰浏览器本体） |
| 带网络的安全模式 | 删除 `SafeBoot\Network`（删除前导出 `.reg` 备份，退出/卸载时还原） |
| 机位标识（**不改系统壁纸**） | 只在程序自己的断网遮罩上显示"计算机名后 6 位"，不影响学生机桌面 |
| 自我保护 | 服务守护（代理被结束自动拉起）、关键文件 SHA256 校验、暂停标记防互相打架、注册表与目录 ACL 加固 |
| 老师退出/暂停 | 任务栏小助手 → 系统功能（F）→ 暂停监控 / 退出程序（都要密码），退出时逐项还原 |
| 口令保护 | **退出、暂停、改设置、解除遮罩、卸载——全部需要密码**；改密码必须提供旧密码（`--init` 需 `--old`）；静默卸载必须带 `--password`（或老师显式 `--force`）；部署脚本/文档/预设**不装到学生机**，只在老师自己的介质上 |
| 抗破解（停服务/删目录） | 服务 DACL 去掉了普通用户的"停止"权、异常终止 5 秒自动重启、**服务镜像放在仅 SYSTEM/管理员可访问的备份副本目录**、程序文件被删/被改**自动恢复**、配置文件有注册表加密镜像（删了自动恢复并报警）、可选**心跳上报**给教师端监视器 —— 详见 [docs/05-防破解与应急.md](docs/05-防破解与应急.md) |
| 老师后路 | **安全模式（无网络）不加载任何管控**（默认；可在设置第 10 组改为"安全模式也管控"）+ `scripts\cleanup-all.ps1` 一键彻底卸载还原（纯 PowerShell、不依赖安装目录里的文件、安全模式可用） |

## 4. 设计取舍（为什么这样做）

以下每条都是**刻意选择**，目的是"管得住，但不把自己变成事故源"：

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

1. **不动 Windows Defender、不要求卸载杀毒软件**：只写策略键，安装目录加入杀软白名单即可。
2. **不劫持主页、不冒充其它软件**：上网入口只做"按设置里的主页启动真实浏览器"，默认不接管快捷方式。
3. **不隐藏痕迹**：`Hosts.HideHostsFile` / `Shell.HideFileExtensions` / `Shell.NoFolderOptions` 都可以在设置里关掉，
   改了什么都写在注册表与日志里，老师能审计。
4. **服务异常不重启电脑**：默认只重启服务（`sc failure … actions= restart`）；"服务异常就重启本机"是可选项且默认关闭。
5. **能删也能还原**：删除"带网络的安全模式"前先导出 `.reg` 备份，退出/卸载时还原。
6. **干跑模式**：`LabGuard.Agent.exe --dry-run` 只记录日志、不修改系统，方便先在教师机上验证策略判定。
7. **留后路**：安全模式（含无网络）不加载任何管控；`scripts/cleanup-all.ps1` 一键彻底卸载还原。

## 5. 自检

```powershell
src\LabGuard.SelfTest\bin\Release\net48\LabGuard.SelfTest.exe          # 全部通过 = 逻辑正确
src\LabGuard.SelfTest\bin\Release\net48\LabGuard.SelfTest.exe --verbose # 看每条系统动作（干跑）
src\LabGuard.SelfTest\bin\Release\net48\LabGuard.SelfTest.exe --only UsbGuard  # 单独调试某个模块
```

自检以 `DryRun` 运行，**不会**修改 hosts、注册表、USB 等（已实测：自检前后系统状态快照完全一致）。

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
scripts/install.ps1          安装（服务 + 计划任务 + 快捷方式 + 目录 ACL）
scripts/uninstall.ps1        卸载
scripts/make-wallpapers.ps1  生成断网遮罩用的 6 张背景图（~1920×1080）
docs/                        原理分析 / 设计说明 / 功能对照表
```

## 7. 安全与合规

- 仅用于学校自有教学机房的学生机管理；使用前应告知师生、并在机房管理制度中说明。
- 口令只存 PBKDF2-SHA256 散列（10 万次迭代，随机盐），配置用 DPAPI 机器密钥加密，目录/注册表 ACL 限制普通用户写入。
- 老师随时可以用密码暂停或退出（`--unlock` 快捷方式 / 托盘菜单 / 设置程序），卸载程序会把所有改动还原。
- 日志：`%ProgramData%\LabGuard\logs\guard-YYYYMMDD.log`。
