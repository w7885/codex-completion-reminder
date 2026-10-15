# Codex Windows Completion Reminder

## Preview status and known limitations

This is a limited source preview with task-completion and opt-in conversation-choice reminders. Tool-approval reminders have been removed. Readable non-minimized choice cards have historical acceptance evidence; minimized, cross-chat, multi-window coverage and reliable automatic withdrawal remain unverified. Confirming settings synchronizes only native completion and question notification preferences. Approval notifications and native sidebar states are not managed. Historical experiments and archived patches are not installable features.

A Windows tray companion for Codex Desktop. It can show a persistent, non-activating popup for task completion or a detected unanswered structured choice card. Keyboard or mouse buttons dismiss the popup while passing input through.

This project targets **Codex Desktop on Windows**. Codex CLI, WSL, remote sessions, and service sessions are not supported in v1.

## Features

- Separate notification preferences for task completion and conversation-choice requests.
- Choose custom popup, Codex native notification, both, or off for each category.
- Edit a title and description per category; adjust popup width, height, and four colors with a live preview.
- Pause temporarily, disable this companion persistently, or uninstall it.
- No network calls. The hook passes only event type and an opaque event/turn ID to the local companion. It does not store or transmit prompts, command text, or key/mouse input.

## Event support

- **Task completed:** Codex `Stop` hook.
- **Conversation choice/input:** an opt-in isolated scanner projects request metadata from the current user's local Codex Desktop sessions. IDs are scoped by session, turn and request, independent of which chat is rendered. It skips questions, options, answers and ordinary chat text, with no implicit native fallback.

Only the verified desktop-client record format and latest turn are accepted. Complete submission records resolve their own session; partial reads and timeouts do not clear requests. Each scan prioritizes recently modified files, bounds candidates to 32 and enumeration to 2048 files, and has a time budget and child-process watchdog. Large historical collections are not guaranteed full coverage. Output contains only opaque IDs, counts and status; no record content is stored or sent over the network. Local desktop testing confirmed visible choice reminders while another application or another chat in the same Codex window was in front, with no repeat after dismissal. Minimized, multiple-window and other-version scenarios remain unverified.

New settings use custom channels for completion and choice. Experimental choice monitoring is off by default and requires explicit opt-in and a restart. Existing completion, choice and appearance preferences are retained; legacy approval fields are ignored and no longer serialized when settings are saved.

`Stop` marks the end of an assistant turn, not proof that the user's overall task is finished. When protocol metadata confirms an outstanding structured choice in the same session and turn, the Hook drops that custom completion event rather than queueing it. Closing the choice popup does not replay that event. Final completion after submission follows the original channel; other sessions and old turns are not globally suppressed.

The bounded adapter projects tool types, request/turn IDs, question counts, creation acknowledgements and reply-envelope IDs; it skips questions, options, answers and message bodies. It requires session_id, turn_id, transcript_path and the supported local record format. Reads are limited to the current CODEX_HOME sessions directory, 32 MiB and a 500 ms parsing budget. Missing metadata, format changes, partial writes or unavailable reads preserve the original Stop behavior and record a status. Ordinary text questions are not treated as structured choices.

Confirming channel settings backs up and synchronizes Codex completion and question notification preferences. Approval notifications, approval policy, native sidebar unread dots and input-needed indicators remain outside this companion. Restart Codex to verify reloading externally changed native preferences.

## Build

Requirements: Windows with .NET Framework 4.x and Windows PowerShell.

```powershell
.\build.ps1
```

The build compiles `CodexReminder.exe`, `CodexReminderScanner.exe` and `CodexReminderHook.exe` into `dist/` and runs the built-in settings/route self-tests.

## Install and update

No stable installer is published. Builds are source previews and require desktop acceptance before installation.

For a safe build and isolated installation test, run:

```powershell
.\tests\Run-Tests.ps1
```

The current installer allows isolated test directories only; ordinary production installation remains gated on desktop acceptance. It registers only the app-owned Stop hook and removes legacy app-owned PermissionRequest handlers while preserving unrelated hooks, native configuration and other startup entries. Installed V4 compatibility builds require backup and the verified compatibility workflow; do not mix V4 and standard V5 transports.

Restart Codex and review/trust the updated hook definitions when prompted. Hooks that have not been reviewed and trusted are skipped by Codex.

## Settings

Right-click the tray icon and choose **提醒设置…**. Settings are saved per Windows user under `%LOCALAPPDATA%\CodexCompletionReminder\settings.json`.

- The global switch and per-event channel choices control this companion's custom popups. Confirming settings synchronizes only native completion and question preferences.
- The choice page provides the experimental monitoring opt-in, restart requirement and coverage limitations.
- The temporary pause only applies until the app exits. The global switch persists across restarts.
- Choosing **退出提醒器** closes the current process; it does not uninstall the hook or disable a future launch. Use the global switch to disable reminders or run the uninstaller to remove the integration.

## Troubleshooting missing reminders

The installer uses an explicit PowerShell invocation and the JSON `commandWindows` override. A bare quoted executable path evaluates to a string in PowerShell instead of running the program. Review and trust the changed hook definitions in Codex after updating their commands.

Start the tray app from Windows File Explorer. Launching it from a Codex isolated terminal may place its windows on `CodexSandboxDesktop`, invisible on the user's interactive desktop. The hook does not auto-start the app on that isolated desktop.

`%LOCALAPPDATA%\CodexCompletionReminder\hook-status.log` records only event type and transport status. It contains no conversation, command, keyboard, or mouse content. `received-Stop` means the hook ran; `pipe-written` means the message was written, not that a user saw the popup. `pipe-error-UnauthorizedAccessException` indicates a permission boundary blocked transport. Stop keeps its continuation contract; unsupported events produce no approval decision.

Use `CodexReminderHook.exe --diagnostic` with a test event in the user's own PowerShell to get a nonzero exit code on transport failure. Tests in Codex's isolated tools can be denied access. A zero hook exit code or a window's visible flag is not an end-to-end desktop acceptance test.

## Uninstall

```powershell
.\uninstall.ps1
```

Uninstall removes only this app's hook handlers and Startup entry. It keeps user settings by default. To also remove the settings file:

```powershell
.\uninstall.ps1 -RemoveSettings
```

## Tests

```powershell
.\tests\Run-Tests.ps1
```

The automated tests cover defaults, config serialization and normalization, channel routing, hook output behavior, and safe install/uninstall merging. The release checklist also requires a manual desktop test for foreground/background popups, key/mouse dismissal, click-through, choice detection, preserved native sidebar state, and multi-monitor sizing.

## Release trust

GitHub Release executables are unsigned. Verify the SHA-256 file attached to a release, or build from source. Windows may show a SmartScreen warning for unsigned binaries.

## License

MIT. See [LICENSE](LICENSE).
