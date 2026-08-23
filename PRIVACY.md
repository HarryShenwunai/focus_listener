# Focus Listener Privacy Notice / 隐私说明

Focus Listener Portable Beta is intended for adults and higher-education learners. It has no product account, cloud database, automatic analytics, advertising, or automatic crash upload.

Focus Listener 便携 Beta 面向成人与高校学习者，不提供产品账户、云端数据库、自动分析、广告或自动崩溃上传。

## Data sent to Google / 发送给 Google 的数据

In real-lesson mode, selected microphone or system audio is sent directly from the device to the Gemini API using the user's own API key. Final transcript excerpts are sent to Gemini to decide whether a grounded reset question can be created. Google processes this data under the Gemini API terms applicable to the user's region and billing status: <https://ai.google.dev/gemini-api/terms>.

在真实课堂模式下，所选麦克风或系统声音会使用用户自己的 API Key，从设备直接发送到 Gemini API。最终转写片段会用于判断能否生成有课堂证据的复位题。Google 会依据用户所在地区和计费状态适用的 Gemini API 条款处理数据：<https://ai.google.dev/gemini-api/terms>。

Do not use sensitive, confidential, personal, or unauthorized classroom content. The user is responsible for obtaining all necessary permission to capture and submit audio.

请勿使用敏感、机密、个人或未经授权的课堂内容。用户有责任取得采集和提交音频所需的全部许可。

## Local data / 本地数据

- API keys are stored in Windows Credential Manager.
- Raw audio and full transcripts are never written to disk.
- Questions, short evidence excerpts, answer accuracy, timing, and technical events are stored locally for 30 days by default.
- Local records can be exported or cleared from Settings.

- API Key 保存在 Windows 凭据管理器。
- 原始音频和完整转写不会写入磁盘。
- 题目、短证据、答题正确率、耗时和技术事件默认在本地保留 30 天。
- 用户可在设置中导出或清除本地记录。

## Diagnostics and network access / 诊断与网络访问

Focus Listener does not check for updates in the background. The Help & About page contacts GitHub only when the user chooses Check for updates. Diagnostic bundles are created locally and exclude API keys, device identity, audio, transcripts, questions, evidence, and the database. Users review and submit bundles manually.

Focus Listener 不会在后台检查更新。只有用户在“帮助与关于”中点击“检查更新”时才会访问 GitHub。诊断包只在本地生成，不包含 API Key、设备身份、音频、转写、题目、证据或数据库；用户检查后自行提交。

## Contact / 联系

Report privacy or security concerns at <https://github.com/HarryShenwunai/focus_listener/issues>.
