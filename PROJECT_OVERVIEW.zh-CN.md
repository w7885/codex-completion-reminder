# 项目整理与交接说明

本目录是原项目的独立副本，用于后续维护和推送 GitHub。创建副本没有修改原项目、已安装的提醒器、Codex 设置或登录启动项。

## 产品定位

面向 Windows Codex 桌面端的可定制提醒工具。它是独立的 Windows 托盘程序，通过 Codex Hook 接收事件；不是 Skill，当前也不是插件市场中的打包插件。

## 当前功能

- 完成事件默认显示“任务已完成”；实验性对话选择默认显示“任务需选择”。权限审批提醒已移除。
- 对话选择请求默认保留自定义渠道，识别须显式启用；最小化、跨聊天及可靠撤销尚未完整验收。
- 支持分事件渠道与文案、自定义尺寸和颜色、实时预览、恢复默认。
- 置顶、不抢焦点；键盘或鼠标按键关闭提醒并传递输入。
- 选择提醒优先，完成提醒合并计数；支持暂停、全局关闭、登录启动和卸载。

## 技术与验证

实际实现为 C#、WinForms、.NET Framework、PowerShell 和本机命名管道。
自动测试覆盖设置、路由、Hook 输出契约、安装卸载以及含空格路径的 PowerShell 调用。
完成 Hook 与非最小化选择卡片有历史真实浮窗验收；当前版本不注册权限审批 Hook，不检测人工审批卡片。

修复过两个具体问题：从 Codex 隔离桌面启动的窗口对用户不可见；裸引号程序路径在 PowerShell 中只输出字符串而不执行。代码已有启动保护、显式调用和不含对话内容的状态日志。

## 文件位置

- src/：程序源码。
- tests/：回归测试。
- install.ps1、uninstall.ps1、build.ps1：安装、卸载和构建。
- README.md、README.zh-CN.md：中英文说明。
- docs/RELEASE_CHECKLIST.zh-CN.md：验证状态和发布前待办。
- dist/：最新编译文件。
- release/：最新未签名安装包及 SHA-256。
- .github/workflows/：Windows 构建和测试工作流。
- LICENSE：MIT 许可证。
- .git/：原项目提交历史。

## GitHub 推送边界

本副本已配置远端 https://github.com/w7885/codex-completion-reminder。源码按早期预览项目维护；正式发布安装包前还需完成干净 Windows 环境及完整桌面验收。实验程序不属于已安装功能，详见 docs/validation/2026-10-11-uia-probe.md。

.gitignore 已排除编译文件、安装包、日志、备份及用户设置。建议 Git 仓库提交源码与文档，安装包作为 GitHub Release 附件上传，不要把用户级 hooks.json、config.toml 或运行日志加入仓库。

不要从 Codex 隔离终端启动或重启生产托盘程序。请从自己的 Windows 文件资源管理器启动，或使用已配置的登录启动项。修改 Hook 定义后需由用户在 Codex 中重新审核并信任。