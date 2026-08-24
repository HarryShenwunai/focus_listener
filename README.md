# Focus Listener

**Windows Classroom Refocus Assistant**  
**Windows 课堂专注复位助手**

Focus Listener is a lightweight Windows widget designed to help learners regain attention during lectures. It identifies complete, self-contained knowledge statements from mathematics, science, history, languages, and other subjects, then generates a three-option recall question that requires **no calculation**.

Focus Listener 是一款轻量级 Windows 课堂专注辅助工具。它会在听课过程中识别数学、科学、历史、语言及其他学科中完整、可独立复述的知识关系，并自动生成一道**不要求计算**的三选一问题，帮助学习者快速恢复注意力。

Questions may be triggered automatically or manually with:

问题可以自动触发，也可以通过以下快捷键手动触发：

```text
Ctrl + Shift + Q
```

> **Release channel / 发布渠道**
>
> `v0.1.0-beta.1` — unsigned portable Beta for Windows 10/11 x64.  
> This Beta is intended only for adults and higher-education learners.
>
> `v0.1.0-beta.1` —— 适用于 Windows 10/11 x64 的未签名便携 Beta。  
> 当前 Beta 仅面向成人及高校学习者。

---

## Key Features / 核心功能

The current repository contains a runnable MVP with the following capabilities:

当前仓库包含一个可运行的 MVP，具备以下功能：

### Classroom questions / 课堂提问

- Supports six universal knowledge types: **definition, cause and effect, rule or condition, process or sequence, comparison or distinction, and classification or example**.
- Formulae, numbers, and variable relationships may be recognised as knowledge points, but generated questions never require substitution, calculation, equation setup, or numerical evaluation.
- Uses a default **60-second warm-up period**.
- After an automatic question is closed, a **120-second cooldown period** begins.
- Maintains no more than **three candidate knowledge points** at a time.
- Provides an initial **8-second answer window**.
- The answer window may be extended once, up to **20 seconds in total**.
- When time expires, the question remains available through a pending-answer badge for **two minutes**.
- After an answer is submitted, the application displays the result and the corresponding classroom wording for **three seconds**.

---

- 支持六类通用知识点：**定义、因果关系、规则或条件、过程或顺序、比较或区分、分类或举例**。
- 公式、数字和变量关系可以被识别为知识点，但生成的问题不会要求代入、计算、列式或求值。
- 默认设置 **60 秒预热时间**。
- 自动题关闭后进入 **120 秒冷却时间**。
- 候选池中最多保留 **3 个候选知识点**。
- 初始答题时间为 **8 秒**。
- 可以延长一次，最长答题时间为 **20 秒**。
- 超时后，问题会通过待答徽标继续保留 **2 分钟**。
- 答题后会显示正确性结果，并展示对应的课堂原话，持续 **3 秒**。

### Audio capture and device control / 音频采集与设备控制

- Windows microphone and system playback devices can be selected and remembered independently.
- Supports four audio modes:
  - Automatic selection
  - Microphone only
  - System audio only
  - Smart mixed mode
- Captures microphone and system playback audio through separate channels.
- Aligns both channels using **100 ms time buckets**.
- When system audio is active, system audio is prioritised. Otherwise, the application falls back to the microphone.
- The application does **not** mix the two PCM streams together.
- Device lists refresh when hardware is connected or removed, but the application does not silently switch to another device.

---

- Windows 麦克风和系统播放设备可以分别选择并记忆。
- 支持四种音频模式：
  - 自动选择
  - 仅麦克风
  - 仅系统声音
  - 智能混合
- 麦克风和系统播放声音通过两条独立通道采集。
- 两路音频按照 **100 毫秒时间桶**进行对齐。
- 系统声音活跃时优先使用系统声音；否则回退到麦克风。
- 软件不会将两路 PCM 音频直接混合。
- 设备插入或移除后，设备列表会自动刷新，但软件不会在未经用户确认的情况下改用其他设备。

### Live transcription and subtitles / 实时转写与字幕

- Live transcription and the independent always-on-top subtitle window can be enabled or disabled separately.
- Subtitle opacity and font size are configurable.
- Interim transcription is used only for live subtitles.
- Only finalised transcription may enter the question-generation pipeline.
- The subtitle window can be shown, hidden, locked, moved, and resized.
- Subtitle position and size are saved automatically.

---

- 实时转写和独立的半透明置顶字幕窗可以分别开启或关闭。
- 可以调整字幕透明度和字号。
- 临时转写文字只用于显示实时字幕。
- 只有最终确认的转写文字才能进入出题流程。
- 字幕窗口可以显示、隐藏、锁定、移动和调整大小。
- 字幕窗口的位置和尺寸会自动保存。

### Gemini integration / Gemini 集成

After live transcription is completed, Gemini Flash-Lite performs a structured evaluation covering:

实时转写完成后，Gemini Flash-Lite 会通过一次结构化判断完成以下任务：

- Subject identification
- Knowledge-type classification
- Eligibility checking
- Candidate-quality scoring
- Three-option question generation
- Verbatim evidence validation

---

- 判断所属学科
- 判断知识类型
- 判断内容是否具备出题资格
- 评估候选知识点质量
- 生成三选一问题
- 校验课堂原话证据

The configured models are:

当前配置的模型为：

```text
Live transcription:
gemini-3.1-flash-live-preview

Eligibility evaluation and question generation:
gemini-3.5-flash-lite
```

Model names are centralised in `GeminiFocusOptions` so they can be updated as Gemini model availability changes.

模型名称集中配置在 `GeminiFocusOptions` 中，便于根据 Gemini 模型生命周期和可用性进行调整。

### One-click system check / 一键系统检测

The built-in system check runs for approximately 15 seconds and evaluates:

内置的一键系统检测会运行约 15 秒，并同时检查：

- The selected microphone
- The selected system playback device
- A safe test sound
- Gemini connectivity
- Live transcription
- Question generation from the actual transcription
- Local data export

---

- 当前选择的麦克风
- 当前选择的系统播放设备
- 安全测试音
- Gemini 连接状态
- 实时转写
- 根据本次真实转写进行出题
- 本地数据导出

The system check uses the same question-generation rules as a formal classroom session. It does not substitute fixed sample content when transcription is missing.

一键系统检测与正式课堂使用同一套出题规则。当本次检测没有获得有效转写时，软件不会使用固定示例内容代替真实转写。

### Local records and simulation / 本地记录与模拟模式

- Uses SQLite for local event records.
- Supports CSV export.
- Does not save raw audio.
- Does not save complete classroom transcripts.
- Includes a built-in multi-subject simulated classroom that can be used without a Gemini API key.

---

- 使用 SQLite 保存本地事件记录。
- 支持导出 CSV。
- 不保存原始音频。
- 不保存完整课堂转写。
- 未配置 Gemini API Key 时，可以直接使用内置的多学科模拟课堂。

---

## Download and First Launch / 下载与首次运行

### 1. Download the release / 下载发布版本

Download the latest files from [GitHub Releases](https://github.com/HarryShenwunai/focus_listener/releases):

从 [GitHub Releases](https://github.com/HarryShenwunai/focus_listener/releases) 下载最新版本：

```text
FocusListener-v*-win-x64.zip
SHA256SUMS.txt
```

### 2. Verify and run / 校验并运行

Verify the SHA-256 checksum, extract the ZIP archive, and run:

请先校验 SHA-256，然后解压 ZIP 文件并运行：

```text
FocusListener.exe
```

This is an unsigned Beta build. Windows may display a source warning or a Microsoft Defender SmartScreen prompt. Do not run the application when the calculated checksum does not match the value in `SHA256SUMS.txt`.

这是一个尚未进行数字签名的 Beta 版本。Windows 可能显示来源警告或 Microsoft Defender SmartScreen 提示。如果计算出的校验值与 `SHA256SUMS.txt` 中的值不一致，请勿运行该文件。

### 3. Complete first-time setup / 完成首次设置

During the first-time setup:

首次启动时需要：

1. Select the interface language.
2. Confirm that the application is intended for adults and authorised audio only.
3. Connect a Gemini API key or begin with simulation mode.

---

1. 选择界面语言。
2. 确认仅供成人使用，并且只处理已获得授权的音频。
3. 配置 Gemini API Key，或者先使用模拟模式。

### 4. Run the system check / 运行系统检测

Run **One-click system check** before using the application in a real classroom for the first time.

第一次在真实课堂中使用前，请先运行**一键系统检测**。

### Portable build / 便携版本

The Windows portable build is self-contained and does not require a separate .NET installation.

Windows 便携版本已经包含运行所需组件，无需另外安装 .NET。

It does not include an installer or automatic updater. To check for a newer release, use:

软件不包含安装器，也不会自动更新。需要检查新版本时，请使用：

```text
Help & About → Check for updates
帮助与关于 → 检查更新
```

---

## Privacy Boundaries / 隐私边界

When real classroom mode is used, audio is sent to Google through the user’s own Gemini API key.

使用真实课堂模式时，课堂音频会通过用户自己的 Gemini API Key 发送至 Google Gemini 服务。

Do not use Focus Listener with:

请勿使用 Focus Listener 处理：

- Sensitive content
- Confidential information
- Personal or private conversations
- Audio that the user is not authorised to capture or process

---

- 敏感内容
- 机密信息
- 私人或个人对话
- 未获得采集或处理授权的音频

The Gemini API key is stored in Windows Credential Manager. It is not written to the repository, application settings file, or SQLite database.

Gemini API Key 保存在 Windows 凭据管理器中，不会写入代码仓库、应用设置文件或 SQLite 数据库。

Focus Listener does not write raw audio or complete classroom transcripts to disk. Local analytics data is retained for **30 days by default**.

Focus Listener 不会将原始音频或完整课堂转写写入磁盘。本地分析记录默认保留 **30 天**。

See [`PRIVACY.md`](PRIVACY.md) for the complete privacy policy.

完整隐私说明请参阅 [`PRIVACY.md`](PRIVACY.md)。

---

## Running from Source / 从源码运行

### Requirements / 环境要求

- Windows 10 build 19041 or later
- Windows 11
- .NET SDK `10.0.400`

---

- Windows 10 19041 或更高版本
- Windows 11
- .NET SDK `10.0.400`

Run the application with:

使用以下命令运行：

```powershell
dotnet run --project src/FocusListener.App/FocusListener.App.csproj
```

The first launch uses simulation mode by default.

首次启动时，应用默认进入模拟模式。

To configure a Gemini API key, click the mode description at the top of the main window. The key is stored for the current Windows user in Windows Credential Manager and is not written to the repository or SQLite database.

要配置 Gemini API Key，请点击主窗口顶部的模式说明。密钥会保存在当前 Windows 用户的凭据管理器中，不会写入仓库或 SQLite 数据库。

A key may also be supplied through the environment before launching the application:

也可以在启动应用前通过环境变量提供密钥：

```powershell
$env:GEMINI_API_KEY = "YOUR_API_KEY"
dotnet run --project src/FocusListener.App/FocusListener.App.csproj
```

---

## How to Use / 使用方法

### 1. Select a mode / 选择运行模式

Configure a Gemini API key from the top of the window, or enter the built-in simulated classroom.

点击窗口顶部配置 Gemini API Key，或者直接进入内置模拟课堂。

### 2. Configure audio devices / 配置音频设备

Open **Classroom Question Settings** and select:

打开**课堂提问设置**，分别选择：

- The microphone actually being used
- The system playback device actually being used
- The preferred audio mode

---

- 当前实际使用的麦克风
- 当前实际使用的系统播放设备
- 所需的音频模式

Available audio modes are:

可选音频模式包括：

```text
Automatic selection / 自动选择
Microphone only / 仅麦克风
System audio only / 仅系统声音
Smart mixed mode / 智能混合
```

### 3. Configure transcription and subtitles / 配置转写与字幕

On the same settings page, configure:

在同一设置页面中，可以配置：

- Live transcription
- Independent subtitle window
- Subtitle opacity
- Subtitle font size
- Keyboard shortcuts

---

- 实时转写
- 独立字幕窗口
- 字幕透明度
- 字幕字号
- 快捷键

### 4. Run the one-click system check / 运行一键系统检测

The test procedure depends on the selected mode:

测试方式取决于当前选择的音频模式：

- In **Microphone only** mode, read a complete knowledge statement aloud.
- In **System audio only** mode, play the statement through the computer.
- The application also plays a soft test sound and displays separate microphone and system-audio level indicators.

---

- 使用**仅麦克风**模式时，请朗读一段完整的知识关系。
- 使用**仅系统声音**模式时，请让电脑播放这段语音。
- 软件还会自动播放轻柔的测试音，并分别显示麦克风和系统声音的音量状态。

### 5. Confirm real transcription-based generation / 确认真实转写出题

The test page generates a question only when the current live-transcription session produces finalised text.

只有本次实时转写真正收到最终文字时，检测页面才会根据该文字出题。

When there is no finalised text or no verbatim evidence for a valid answer, the application clearly reports that no question can be generated. It does not replace the missing transcription with fixed test material.

如果没有最终文字，或者没有能够支持正确答案的逐字证据，软件会明确说明无法出题，不会使用固定测试素材代替。

### 6. Start the classroom session / 开始课堂

During a session, the floating window can be used to:

课堂开始后，可以通过悬浮窗口：

- Change audio devices
- Pause or resume transcription
- Show or hide subtitles
- Retry the Gemini Live connection
- Trigger a prepared question immediately
- Wait for an eligible question to trigger automatically

---

- 更换音频设备
- 暂停或继续转写
- 显示或隐藏字幕
- 重试 Gemini Live 连接
- 立即触发已经准备好的问题
- 等待符合条件的问题自动触发

When **Question ready** appears, the question can be triggered immediately or left for the automatic scheduler.

出现**题目已准备**时，可以立即提问，也可以继续等待自动触发。

### 7. Export classroom analytics / 导出课堂分析

At the end of a session, export the local analytics as a CSV file.

课堂结束后，可以将本地分析记录导出为 CSV 文件。

---

## Default Keyboard Shortcuts / 默认快捷键

| Action | 操作 | Shortcut / 快捷键 |
|---|---|---|
| Ask a question immediately | 立即提问 | `Ctrl + Shift + Q` |
| Show or hide subtitles | 显示或隐藏字幕 | `Ctrl + Shift + S` |
| Lock or unlock subtitle movement | 锁定或解锁字幕位置 | `Ctrl + Shift + L` |

When the subtitle window is unlocked, it can be dragged and resized. Its position and dimensions are saved automatically.

字幕窗口解锁后，可以拖动和调整大小；位置和尺寸会自动保存。

---

## Accessibility and Animation / 无障碍与动画

The breathing animation shown for **Question ready** can be disabled in the application settings.

“题目已准备”的呼吸动画可以在设置中关闭。

When Windows animation effects are disabled, Focus Listener automatically respects the system setting and disables the corresponding application animation.

如果 Windows 已关闭动画效果，Focus Listener 会自动遵循该系统设置，并关闭相应动画。

---

## Build and Test / 构建与测试

Build the solution:

构建解决方案：

```powershell
dotnet build FocusListener.slnx
```

Run the automated tests:

运行自动化测试：

```powershell
dotnet test tests/FocusListener.Tests/FocusListener.Tests.csproj
```

The test suite uses the same core interfaces as the desktop application. It covers:

测试使用与桌面端相同的核心接口，覆盖以下内容：

- Legacy answer behaviour
- Six universal knowledge types
- Verbatim evidence validation
- No-calculation constraints
- Rejection of dangerous content
- Candidate ranking and eviction
- Warm-up and cooldown behaviour
- Settings boundaries
- Forward-compatible SQLite migration

---

- 旧版答题行为
- 六类通用知识点
- 逐字证据校验
- 无计算约束
- 危险内容拒绝
- 候选池排序与淘汰
- 预热与冷却逻辑
- 设置边界
- SQLite 向前迁移

The disposable state-machine prototype is located at:

丢弃型状态机原型位于：

```text
prototypes/focus-session-state.prototype.html
```

It can be opened directly in a browser. It is an experimental workbench and is not part of the production application.

该文件可以直接使用浏览器打开。它仅用于状态机实验，不属于生产代码。

---

## Local Data / 本地数据

Application data is stored by default at:

应用运行数据默认保存在：

```text
%LOCALAPPDATA%\FocusListener\focus-listener.db
```

Application settings are stored at:

应用设置保存在：

```text
%LOCALAPPDATA%\FocusListener\settings.json
```

### SQLite and CSV records / SQLite 与 CSV 记录内容

Local records may include:

本地记录可能包括：

- Subject
- Knowledge type
- Candidate quality
- Candidate priority
- Trigger method
- Generated question
- Classroom evidence used by the question
- Answer correctness
- Response time
- Generation-failure reason
- Candidate eviction or expiry events
- Reporting events

---

- 学科
- 知识类型
- 候选质量
- 候选优先级
- 触发方式
- 生成的问题
- 问题使用的课堂证据
- 答题正确性
- 答题耗时
- 生成失败原因
- 候选淘汰或过期事件
- 报告事件

The application does not store:

应用不会保存：

- Raw audio
- Complete classroom transcripts

---

- 原始音频
- 完整课堂转写

### Clearing local records / 清除本地记录

The **Clear local records** action deletes only:

设置页面中的**清除本地记录**只会删除：

- The application database
- The application diagnostics directory

---

- 应用数据库
- 应用诊断目录

It does not delete:

该操作不会删除：

- CSV files previously exported to another location
- Application settings
- Gemini credentials stored in Windows Credential Manager

---

- 已导出到其他位置的 CSV 文件
- 应用设置
- 保存在 Windows 凭据管理器中的 Gemini 凭据

---

## Offline Acceptance Testing / 线下验收

Real-mode acceptance testing must use audio owned by the tester or audio for which the tester has explicit permission.

真实模式的验收测试必须使用测试者自己的音频，或者已经获得明确授权的音频。

At least the following six content types should be tested:

至少应测试以下六类内容：

1. Mathematical relationship or formula
2. Scientific cause and effect
3. Historical comparison
4. Language definition
5. Process or sequence
6. Classification or example

---

1. 数学关系或公式
2. 科学因果关系
3. 历史比较
4. 语言定义
5. 过程或顺序
6. 分类或举例

The following cases must also be verified as **ineligible for question generation**:

还需要确认以下内容**不会触发出题**：

- Incomplete sentence fragments
- Requests requiring calculation
- Content without verbatim supporting evidence
- Dangerous instructions or unsafe content

---

- 不完整的残句
- 要求计算的问题
- 没有逐字支持证据的内容
- 危险指令或不安全内容

Audio conditions should include:

音频环境应覆盖：

- Clear system playback
- Distant microphone input
- Background noise
- Mixed Chinese and English
- Long pauses

---

- 清晰的系统播放声音
- 远距离麦克风输入
- 背景噪声
- 中英文混合内容
- 较长停顿

---

## Documentation / 相关文档

Universal question-generation rules:

通用知识题生成规则：

[`docs/universal-knowledge-questions.md`](docs/universal-knowledge-questions.md)

Architecture decision: native Windows single-process application:

架构决策：Windows 原生单进程应用：

[`docs/adr/0001-use-native-windows-single-process.md`](docs/adr/0001-use-native-windows-single-process.md)

Architecture decision: universal knowledge candidate scheduler:

架构决策：通用知识候选调度器：

[`docs/adr/0002-universal-knowledge-candidate-scheduler.md`](docs/adr/0002-universal-knowledge-candidate-scheduler.md)
