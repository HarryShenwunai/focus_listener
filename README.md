# Focus Listener

一个 Windows 课堂注意力复位小组件。它在听课途中识别适合复述的小学数学行程问题知识点，自动弹出不含计算的三选一问题，也支持 `Ctrl + Shift + Q` 手动触发。

当前仓库包含可运行的 MVP：

- `.NET 10 + WPF` 置顶悬浮窗口；
- 8 秒答题窗口，可延长一次至总计 20 秒；
- 2 分钟待答徽标、一个当前题和至多一个排队题；
- 答题后显示正确性与课堂原话 3 秒；
- Windows 麦克风与系统播放双路采集，按 100 ms 时间桶对齐；系统声活跃时优先系统声，否则回退麦克风，不做 PCM 混音；
- Gemini Live 实时转写与 Gemini Flash-Lite 结构化出题；
- 本地 SQLite 事件记录和 CSV 导出，不保存原始音频；
- 无 API Key 时可直接使用内置模拟课堂。

## 运行

需要 Windows 10 19041 或更高版本，以及 .NET SDK `10.0.400`。

```powershell
dotnet run --project src/FocusListener.App/FocusListener.App.csproj
```

首次启动默认为模拟模式。点击窗口顶部的模式说明可配置 Gemini API Key；密钥保存到当前 Windows 用户的凭据管理器，不写入仓库或 SQLite。也可以在启动进程前设置 `GEMINI_API_KEY` 环境变量。

配置密钥后，应用使用：

- 实时转写：`gemini-3.1-flash-live-preview`
- 资格判断与三选一生成：`gemini-3.5-flash-lite`

模型名集中在 `GeminiFocusOptions`，可随 Gemini 模型生命周期调整。

## 验证

```powershell
dotnet build FocusListener.slnx
dotnet test tests/FocusListener.Tests/FocusListener.Tests.csproj
```

状态机测试通过与桌面端相同的 `IFocusSession` 接口发送用户意图，覆盖自动答题与幂等、手动触发、延长/待答恢复、单排队容量以及题目有误后的队列提升。

逻辑原型位于 `prototypes/focus-session-state.prototype.html`，可直接用浏览器打开。它是丢弃型状态机实验台，不属于生产代码。

## 本地数据

运行数据默认写入：

```text
%LOCALAPPDATA%\FocusListener\focus-listener.db
```

应用结束课堂并完成或跳过 1–5 分注意力复位评分后，可从界面导出 CSV。记录包含答题结果、答题耗时、触发与状态事件；不会写入原始音频。

## 线下验收

真实模式仍需使用自己的或明确获授权的课堂音频完成五组现场测试：清晰播放、远距离麦克风、噪声、中英混合、长停顿。目标是至少 4/5 次完整结束，每次同时验证自动与手动触发，所有题都有课堂证据且不要求计算，合格单元到弹卡中位数不超过 8 秒。

详细决策见 `docs/product-spec.md`，架构决策见 `docs/adr/0001-use-native-windows-single-process.md`。
