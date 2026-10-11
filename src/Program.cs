using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CodexCompletionReminder
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--self-test") >= 0)
                return SelfTests.Run();

            bool testPopup = Array.IndexOf(args, "--test-popup") >= 0;
            bool createdNew;
            using (Mutex mutex = new Mutex(false, @"Local\CodexCompletionReminderV4", out createdNew))
            {
                if (!createdNew) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ReminderContext(testPopup));
            }
            return 0;
        }
    }

    internal sealed class ReminderContext : ApplicationContext
    {
        private const string PipeName = "CodexCompletionReminderV4";
        // Codex exposes native question notifications. The current public Hook API does not
        // expose a stable custom event for conversation-choice prompts.
        private const bool ChoiceEventSourceAvailable = false;
        private readonly Control dispatcher;
        private readonly NotifyIcon tray;
        private readonly Thread pipeThread;
        private readonly HashSet<string> recentEvents = new HashSet<string>();
        private readonly Queue<string> recentOrder = new Queue<string>();
        private ReminderSettings settings;
        private ReminderPopup popup;
        private string visibleKind;
        private bool paused;
        private volatile bool stopping;
        private readonly PendingNotificationQueue pending = new PendingNotificationQueue();
        private bool closingForTransition;
        private ToolStripMenuItem pauseItem;
        private ToolStripMenuItem enabledItem;

        public ReminderContext(bool testPopup)
        {
            settings = SettingsStore.Load();
            dispatcher = new Control();
            IntPtr ignoredHandle = dispatcher.Handle;
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Information;
            tray.Text = "Codex 完成提醒器（运行中）";
            tray.ContextMenuStrip = CreateMenu();
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenSettings(); };
            pipeThread = new Thread(ListenForEvents);
            pipeThread.IsBackground = true;
            pipeThread.Name = "Codex reminder pipe";
            pipeThread.Start();
            RefreshEnabledMenu();

            if (testPopup)
                dispatcher.BeginInvoke((MethodInvoker)delegate { AcceptEvent(new ReminderEvent { Kind = ReminderKinds.Completion, EventId = "test-" + Guid.NewGuid().ToString("N") }); });
        }

        private ContextMenuStrip CreateMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("提醒设置…");
            settingsItem.Click += delegate { OpenSettings(); };
            enabledItem = new ToolStripMenuItem("启用本插件提醒") { CheckOnClick = true };
            enabledItem.Click += delegate
            {
                settings.Enabled = enabledItem.Checked;
                SaveSettings();
                if (!settings.Enabled) ClearPendingAndClose();
                RefreshEnabledMenu();
            };
            pauseItem = new ToolStripMenuItem("临时暂停提醒") { CheckOnClick = true };
            pauseItem.Click += delegate
            {
                paused = pauseItem.Checked;
                if (paused) ClearPendingAndClose();
                RefreshEnabledMenu();
            };
            ToolStripMenuItem clearItem = new ToolStripMenuItem("清空待处理提醒");
            clearItem.Click += delegate { ClearPendingAndClose(); };
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出提醒器");
            exitItem.Click += delegate { ExitApplication(); };
            menu.Items.Add(settingsItem);
            menu.Items.Add(enabledItem);
            menu.Items.Add(pauseItem);
            menu.Items.Add(clearItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            return menu;
        }

        private void RefreshEnabledMenu()
        {
            if (enabledItem != null) enabledItem.Checked = settings.Enabled;
            if (pauseItem != null) pauseItem.Checked = paused;
            tray.Text = !settings.Enabled ? "Codex 完成提醒器（已关闭）" :
                (paused ? "Codex 完成提醒器（临时暂停）" : "Codex 完成提醒器（运行中）");
        }

        private void OpenSettings()
        {
            using (ReminderSettingsForm form = new ReminderSettingsForm(settings.Clone(), ChoiceEventSourceAvailable))
            {
                if (form.ShowDialog() != DialogResult.OK) return;
                settings = form.ResultSettings;
                SaveSettings();
                ClearPendingAndClose();
                RefreshEnabledMenu();
            }
        }

        private void SaveSettings()
        {
            try { SettingsStore.Save(settings); }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(3500, "提醒设置未保存", ex.Message, ToolTipIcon.Warning);
            }
        }

        private void ListenForEvents()
        {
            while (!stopping)
            {
                try
                {
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        using (StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 512, true))
                        {
                            ReminderEvent reminderEvent;
                            if (ReminderEvent.TryParse(reader.ReadLine(), out reminderEvent))
                                dispatcher.BeginInvoke((MethodInvoker)delegate { AcceptEvent(reminderEvent); });
                        }
                    }
                }
                catch (ObjectDisposedException) { if (stopping) break; }
                catch (IOException) { if (stopping) break; }
                catch (InvalidOperationException) { if (stopping) break; }
                catch { if (stopping) break; }
            }
        }

        private void AcceptEvent(ReminderEvent reminderEvent)
        {
            if (stopping || paused || !settings.Enabled || reminderEvent == null) return;
            if (!settings.WantsCustomPopup(reminderEvent.Kind, ChoiceEventSourceAvailable)) return;

            string dedupeKey = reminderEvent.Kind + ":" + reminderEvent.EventId;
            if (!recentEvents.Add(dedupeKey)) return;
            recentOrder.Enqueue(dedupeKey);
            while (recentOrder.Count > 512)
                recentEvents.Remove(recentOrder.Dequeue());

            pending.Add(reminderEvent.Kind);

            ShowHighestPriorityReminder();
        }

        private void ShowHighestPriorityReminder()
        {
            if (!settings.Enabled || paused) return;
            string nextKind = pending.CurrentKind;
            if (nextKind == null) return;

            if (popup != null && !popup.IsDisposed && visibleKind == nextKind)
            {
                UpdatePopupCount();
                return;
            }

            if (popup != null && !popup.IsDisposed)
                ClosePopupOnly();

            visibleKind = nextKind;
            popup = new ReminderPopup(settings);
            popup.FormClosed += PopupFormClosed;
            UpdatePopupCount();
            popup.Show();
            bool hooksInstalled = InputDismissHook.Install(delegate
            {
                if (popup != null && !popup.IsDisposed)
                    popup.BeginInvoke((MethodInvoker)delegate { DismissVisibleAndAdvance(); });
            });
            if (!hooksInstalled)
                tray.ShowBalloonTip(3500, "输入监听不可用", "可以从托盘菜单清空当前提醒。", ToolTipIcon.Warning);
        }

        private void UpdatePopupCount()
        {
            if (popup == null || popup.IsDisposed) return;
            popup.SetContent(settings.GetTitle(visibleKind), settings.GetDescription(visibleKind),
                pending.Count(visibleKind));
        }

        private void PopupFormClosed(object sender, FormClosedEventArgs e)
        {
            if (closingForTransition) return;
            popup = null;
            visibleKind = null;
            InputDismissHook.Uninstall();
            ShowHighestPriorityReminder();
        }

        private void DismissVisibleAndAdvance()
        {
            pending.Dismiss(visibleKind);
            ClosePopupOnly();
            ShowHighestPriorityReminder();
        }

        private void ClosePopupOnly()
        {
            ReminderPopup current = popup;
            popup = null;
            visibleKind = null;
            InputDismissHook.Uninstall();
            if (current != null && !current.IsDisposed)
            {
                closingForTransition = true;
                current.Close();
                closingForTransition = false;
            }
        }

        private void ClearPendingAndClose()
        {
            pending.Clear();
            ClosePopupOnly();
        }

        private void ExitApplication()
        {
            stopping = true;
            ClearPendingAndClose();
            tray.Visible = false;
            tray.Dispose();
            dispatcher.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stopping = true;
                InputDismissHook.Uninstall();
                if (popup != null && !popup.IsDisposed) popup.Dispose();
                tray.Visible = false;
                tray.Dispose();
                dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ReminderPopup : Form
    {
        private readonly Label accent;
        private readonly Label dot;
        private readonly Label title;
        private readonly Label description;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopmost = 0x00000008;
        private const int WsExTransparent = 0x00000020;
        private const int WsExLayered = 0x00080000;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost | WsExTransparent | WsExLayered;
                return cp;
            }
        }

        public ReminderPopup(ReminderSettings settings)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ClientSize = new Size(settings.Width, settings.Height);
            BackColor = ColorTranslator.FromHtml(settings.BackgroundColor);

            accent = new Label { BackColor = ColorTranslator.FromHtml(settings.AccentColor), Location = new Point(0, 0), Size = new Size(6, Height) };
            dot = new Label { Text = "●", ForeColor = ColorTranslator.FromHtml(settings.AccentColor), BackColor = BackColor, Font = new Font("Segoe UI", 18, FontStyle.Bold), Location = new Point(18, 15), AutoSize = true };
            title = new Label { ForeColor = ColorTranslator.FromHtml(settings.TitleColor), BackColor = BackColor, Font = new Font("Segoe UI", 16, FontStyle.Bold), Location = new Point(55, 18), AutoEllipsis = true };
            description = new Label { ForeColor = ColorTranslator.FromHtml(settings.DescriptionColor), BackColor = BackColor, Font = new Font("Segoe UI", 10, FontStyle.Regular), Location = new Point(56, 56), AutoEllipsis = true };
            Controls.Add(accent);
            Controls.Add(dot);
            Controls.Add(title);
            Controls.Add(description);
            LayoutContent();
            Shown += PositionWithoutActivation;
            SizeChanged += delegate { LayoutContent(); };
        }

        public void SetContent(string titleText, string descriptionText, int count)
        {
            string titleValue = String.IsNullOrWhiteSpace(titleText) ? "Codex" : titleText.Trim();
            title.Text = count > 1 ? String.Format("{0} · {1} 项", titleValue, count) : titleValue;
            description.Text = descriptionText ?? "";
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, 255, 0x00000002);
        }

        public void BringToFrontWithoutActivation()
        {
            if (IsDisposed) return;
            TopMost = true;
            PositionWithoutActivation(this, EventArgs.Empty);
        }

        private void LayoutContent()
        {
            accent.Size = new Size(6, ClientSize.Height);
            title.Size = new Size(Math.Max(120, ClientSize.Width - 78), 30);
            description.Size = new Size(Math.Max(120, ClientSize.Width - 78), Math.Max(20, ClientSize.Height - 68));
        }

        private void PositionWithoutActivation(object sender, EventArgs e)
        {
            Screen screen;
            IntPtr foreground = GetForegroundWindow();
            try { screen = foreground == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(foreground); }
            catch { screen = Screen.PrimaryScreen; }
            Rectangle area = screen.WorkingArea;
            int displayWidth = Math.Max(1, Math.Min(Width, area.Width - 24));
            int displayHeight = Math.Max(1, Math.Min(Height, area.Height - 24));
            int x = Math.Max(area.Left + 12, area.Right - displayWidth - 12);
            int y = Math.Max(area.Top + 12, Math.Min(area.Top + 22, area.Bottom - displayHeight - 12));
            SetWindowPos(Handle, HwndTopmost, x, y, displayWidth, displayHeight, SwpNoActivate | SwpShowWindow);
        }
    }

    internal static class InputDismissHook
    {
        private const int WhKeyboardLl = 13;
        private const int WhMouseLl = 14;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmRightButtonDown = 0x0204;
        private const int WmMiddleButtonDown = 0x0207;
        private const int WmXButtonDown = 0x020B;
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        private static HookProc keyboardCallback;
        private static HookProc mouseCallback;
        private static IntPtr keyboardHook = IntPtr.Zero;
        private static IntPtr mouseHook = IntPtr.Zero;
        private static Action onInput;
        private static int dismissQueued;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        public static bool Install(Action dismiss)
        {
            if (keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero) return true;
            onInput = dismiss;
            keyboardCallback = KeyboardCallback;
            mouseCallback = MouseCallback;
            IntPtr module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WhKeyboardLl, keyboardCallback, module, 0);
            mouseHook = SetWindowsHookEx(WhMouseLl, mouseCallback, module, 0);
            if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
            {
                Uninstall();
                return false;
            }
            Interlocked.Exchange(ref dismissQueued, 0);
            return true;
        }

        public static void Uninstall()
        {
            if (keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
            if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            onInput = null;
            keyboardCallback = null;
            mouseCallback = null;
            Interlocked.Exchange(ref dismissQueued, 0);
        }

        private static void RequestDismiss()
        {
            if (Interlocked.Exchange(ref dismissQueued, 1) != 0) return;
            Action handler = onInput;
            if (handler != null) { try { handler(); } catch { } }
        }

        private static IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            int message = wParam.ToInt32();
            if (code >= 0 && (message == WmKeyDown || message == WmSysKeyDown)) RequestDismiss();
            return CallNextHookEx(keyboardHook, code, wParam, lParam);
        }

        private static IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            int message = wParam.ToInt32();
            if (code >= 0 && (message == WmLeftButtonDown || message == WmRightButtonDown ||
                message == WmMiddleButtonDown || message == WmXButtonDown)) RequestDismiss();
            return CallNextHookEx(mouseHook, code, wParam, lParam);
        }
    }
}
