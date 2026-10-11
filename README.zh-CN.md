# Codex Windows 完成提醒器

一个面向 Windows Codex 桌面端的托盘提醒工具。任务完成或 Codex 请求工具权限时，它会显示一个置顶但不抢键盘焦点的浮窗。浮窗可通过键盘任意键或鼠标按键关闭，输入会继续传递给下层应用。

v1 仅支持 **Windows 上的 Codex 桌面端**，不承诺支持 Codex CLI、WSL、远程会话或服务会话。

## 功能

- 分别设置任务完成、工具权限审批、对话选择请求的提醒方式。
- 每类可选择自定义浮窗、Codex 原生通知、两者或关闭。
- 每类分别编辑标题与说明；自定义弹窗可调整宽度、高度及四种颜色，并实时预览。
- 支持临时暂停、持久关闭本插件提醒或卸载。
- 不联网。Hook 只向本机托盘程序传递事件类型和不透明的事件/回合 ID，不保存或发送提示词、命令文本、键盘或鼠标输入。

## 事件支持

- **任务完成：**使用 Codex `Stop` Hook。
- **工具权限审批：**使用 Codex `PermissionRequest` Hook。Hook 不会替用户批准或拒绝，Codex 原有审批界面和策略保持生效。
- **对话选择/输入：**当前使用 Codex 桌面端的原生问题通知作为受支持的回退。现行公开 Hook API 没有能稳定捕捉所有对话选择请求的自定义事件，因此该类选择自定义浮窗时会回退到原生通知。本工具不会通过问号或回复文本猜测。

首次创建设置时，任务完成和权限审批默认使用自定义浮窗；对话选择请求默认使用 Codex 原生通知。已有设置文件中的渠道选择会保留。

`Stop` 表示助手当前一轮回复结束，不代表用户的整体任务一定完成。如果助手以提问结束一轮，自定义完成浮窗可能与 Codex 原生问题通知同时出现。默认仍启用自定义完成浮窗；若想避免这类并发提醒，可将完成提醒改为原生通知或关闭。

Codex 原生通知由 Codex 和 Windows 管理，本工具不会修改相关设置。请在 Codex 设置中打开对应的问题/权限通知，并允许 Windows 向 ChatGPT/Codex 应用发送通知。原生通知的内容和停留时间仍由 Codex/Windows 控制。详见 [Codex 通知说明](https://learn.chatgpt.com/docs/notifications)。

## 构建

需要 Windows、.NET Framework 4.x 和 Windows PowerShell。

```powershell
.\build.ps1
```

构建会生成 `dist\CodexReminder.exe` 与 `dist\CodexReminderHook.exe`，并运行内置设置/路由自测。

## 安装和更新

使用预编译版本时，从 GitHub Releases 下载 Windows ZIP，解压后在 PowerShell 中运行 `install.ps1`。Release 中的程序未签名，详见下方安全说明。

从源码安装时，先克隆仓库并运行 `build.ps1`，再运行 `install.ps1`：

```powershell
.\install.ps1
```

安装脚本会构建程序、复制到 `%USERPROFILE%\.codex\codex-reminder`，将本工具的 `Stop` 和 `PermissionRequest` Hook 合并到 `%USERPROFILE%\.codex\hooks.json`，并创建当前用户的登录启动项。其他 Hook 和 Codex 原生通知设置会保留。

重启 Codex，并在提示时审核、信任更新后的 Hook 定义。未审核并信任的 Hook 会被 Codex 跳过。

## 设置

右键托盘图标，选择 **提醒设置…**。设置按 Windows 用户保存到 `%LOCALAPPDATA%\CodexCompletionReminder\settings.json`。

- 全局开关和各事件的提醒渠道控制本插件的自定义浮窗。Codex 原生通知仍由 Codex/Windows 设置控制。
- 对话选择请求页面会说明：当前无法通过稳定自定义事件识别这类状态，因此使用原生通知回退。
- 临时暂停只持续到程序退出；全局开关会跨重启保存。
- **退出提醒器**只关闭当前进程，不会卸载 Hook，也不阻止下次事件重新启动。要禁用提醒请关闭全局开关；要移除集成请运行卸载脚本。

## 卸载

```powershell
.\uninstall.ps1
```

卸载只删除本工具管理的 Hook 和登录启动项，默认保留用户设置。若也要删除设置文件：

```powershell
.\uninstall.ps1 -RemoveSettings
```

## 测试

```powershell
.\tests\Run-Tests.ps1
```

自动测试覆盖默认值、配置序列化与规范化、渠道路由、Hook 输出和安装/卸载合并。发布前还需手动验证 Codex 前后台弹窗、键盘/鼠标关闭、点击穿透、原生通知回退和多显示器尺寸。

## Release 信任说明

GitHub Release 中的可执行文件没有代码签名。请核对随 Release 提供的 SHA-256，或从源码自行构建。Windows 可能显示 SmartScreen 提示。

## 许可证

MIT，详见 [LICENSE](LICENSE)。
