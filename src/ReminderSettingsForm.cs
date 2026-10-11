using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CodexCompletionReminder
{
    internal sealed class ReminderSettingsForm : Form
    {
        private ReminderSettings working;
        private readonly bool choiceSourceAvailable;
        private CheckBox globalEnabled;
        private ComboBox completionChannel;
        private ComboBox permissionChannel;
        private ComboBox choiceChannel;
        private TextBox completionTitle;
        private TextBox completionDescription;
        private TextBox permissionTitle;
        private TextBox permissionDescription;
        private TextBox choiceTitle;
        private TextBox choiceDescription;
        private NumericUpDown widthInput;
        private NumericUpDown heightInput;
        private readonly Dictionary<string, Button> colorButtons = new Dictionary<string, Button>();
        private ComboBox previewKind;
        private Panel previewPanel;
        private Panel previewAccent;
        private Label previewDot;
        private Label previewTitle;
        private Label previewDescription;
        private bool loading;
        private bool textHandlersAttached;

        public ReminderSettings ResultSettings { get; private set; }

        public ReminderSettingsForm(ReminderSettings settings, bool choiceEventAvailable)
        {
            working = settings == null ? ReminderSettings.Defaults() : settings.Clone();
            working.Normalize();
            choiceSourceAvailable = choiceEventAvailable;
            Text = "Codex 完成提醒器设置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(760, 650);

            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildEventsPage());
            tabs.TabPages.Add(BuildAppearancePage());
            Controls.Add(tabs);

            Panel buttons = new Panel { Dock = DockStyle.Bottom, Height = 50 };
            Button reset = new Button { Text = "恢复默认", Width = 100, Height = 30, Left = 18, Top = 10 };
            reset.Click += delegate { ResetDefaults(); };
            Button save = new Button { Text = "保存", Width = 100, Height = 30, Left = ClientSize.Width - 224, Top = 10, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            save.Click += delegate
            {
                working.Normalize();
                ResultSettings = working;
                DialogResult = DialogResult.OK;
                Close();
            };
            Button cancel = new Button { Text = "取消", Width = 100, Height = 30, Left = ClientSize.Width - 112, Top = 10, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(reset);
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = cancel;
            LoadIntoControls();
        }

        private TabPage BuildEventsPage()
        {
            TabPage page = new TabPage("提醒类型") { AutoScroll = true };
            globalEnabled = new CheckBox { Text = "启用本插件的自定义浮窗（不更改 Codex 原生通知设置）", Left = 20, Top = 14, AutoSize = true };
            globalEnabled.CheckedChanged += delegate
            {
                if (!loading) working.Enabled = globalEnabled.Checked;
            };
            page.Controls.Add(globalEnabled);

            completionChannel = NewChannelCombo();
            completionTitle = NewTextBox(100);
            completionDescription = NewTextBox(180);
            page.Controls.Add(BuildEventGroup("任务完成", 48, 170, completionChannel, completionTitle, completionDescription,
                "Stop Hook 表示当前回复结束；助手提问等待你回复时也可能触发，因此可能同时出现完成浮窗和 Codex 原生问题通知。"));

            permissionChannel = NewChannelCombo();
            permissionTitle = NewTextBox(100);
            permissionDescription = NewTextBox(180);
            page.Controls.Add(BuildEventGroup("权限审批", 227, 150, permissionChannel, permissionTitle, permissionDescription,
                "例如运行命令或访问网络前，需要你批准。"));

            choiceChannel = NewChannelCombo();
            choiceTitle = NewTextBox(100);
            choiceDescription = NewTextBox(180);
            string choiceHint = choiceSourceAvailable
                ? "Codex 已提供可监听的选择请求事件。"
                : "当前版本无法通过自定义 Hook 可靠识别对话选择请求；选择自定义浮窗时将回退到 Codex 原生通知。";
            page.Controls.Add(BuildEventGroup("对话选择请求", 386, 184, choiceChannel, choiceTitle, choiceDescription, choiceHint));

            Label nativeHint = new Label
            {
                Text = "原生通知由 Codex 与 Windows 设置控制，本插件不会更改它们。选择“两者都要”时可能出现双重提醒；原生通知开关需在 Codex/Windows 中管理。",
                Left = 20, Top = 582, Width = 690, Height = 45, AutoSize = false
            };
            page.Controls.Add(nativeHint);
            return page;
        }

        private GroupBox BuildEventGroup(string heading, int top, int height, ComboBox channel, TextBox title, TextBox description, string hint)
        {
            GroupBox group = new GroupBox { Text = heading, Left = 16, Top = top, Width = 704, Height = height };
            Label channelLabel = new Label { Text = "提醒方式", Left = 14, Top = 26, Width = 76 };
            channel.Left = 92;
            channel.Top = 22;
            channel.Width = 240;
            Label titleLabel = new Label { Text = "标题", Left = 14, Top = 62, Width = 76 };
            title.Left = 92;
            title.Top = 58;
            title.Width = 590;
            Label descriptionLabel = new Label { Text = "说明", Left = 14, Top = 97, Width = 76 };
            description.Left = 92;
            description.Top = 93;
            description.Width = 590;
            group.Controls.Add(channelLabel);
            group.Controls.Add(channel);
            group.Controls.Add(titleLabel);
            group.Controls.Add(title);
            group.Controls.Add(descriptionLabel);
            group.Controls.Add(description);
            if (!String.IsNullOrWhiteSpace(hint))
            {
                Label hintLabel = new Label { Text = hint, Left = 92, Top = 124, Width = 590, Height = Math.Max(20, height - 130), AutoSize = false, ForeColor = Color.DimGray };
                group.Controls.Add(hintLabel);
            }
            return group;
        }

        private TabPage BuildAppearancePage()
        {
            TabPage page = new TabPage("弹窗外观");
            Label intro = new Label { Text = "外观设置适用于所有自定义浮窗；文案可在“提醒类型”中分别设置。", Left = 20, Top = 18, Width = 700, Height = 26 };
            page.Controls.Add(intro);

            GroupBox sizeGroup = new GroupBox { Text = "大小", Left = 20, Top = 55, Width = 340, Height = 82 };
            Label widthLabel = new Label { Text = "宽度", Left = 16, Top = 34, Width = 54 };
            widthInput = new NumericUpDown { Left = 72, Top = 30, Width = 82, Minimum = 300, Maximum = 720, Increment = 10 };
            Label heightLabel = new Label { Text = "高度", Left = 178, Top = 34, Width = 54 };
            heightInput = new NumericUpDown { Left = 234, Top = 30, Width = 82, Minimum = 96, Maximum = 360, Increment = 8 };
            widthInput.ValueChanged += delegate { if (!loading) { working.Width = (int)widthInput.Value; UpdatePreview(); } };
            heightInput.ValueChanged += delegate { if (!loading) { working.Height = (int)heightInput.Value; UpdatePreview(); } };
            sizeGroup.Controls.Add(widthLabel);
            sizeGroup.Controls.Add(widthInput);
            sizeGroup.Controls.Add(heightLabel);
            sizeGroup.Controls.Add(heightInput);
            page.Controls.Add(sizeGroup);

            GroupBox colorGroup = new GroupBox { Text = "颜色", Left = 20, Top = 148, Width = 340, Height = 240 };
            AddColorButton(colorGroup, "弹窗背景", "BackgroundColor", 18, 34);
            AddColorButton(colorGroup, "强调色", "AccentColor", 18, 80);
            AddColorButton(colorGroup, "标题文字", "TitleColor", 18, 126);
            AddColorButton(colorGroup, "说明文字", "DescriptionColor", 18, 172);
            page.Controls.Add(colorGroup);

            GroupBox previewGroup = new GroupBox { Text = "实时预览", Left = 380, Top = 55, Width = 340, Height = 333 };
            Label sampleLabel = new Label { Text = "预览内容", Left = 16, Top = 30, Width = 80 };
            previewKind = new ComboBox { Left = 102, Top = 26, Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
            previewKind.Items.Add(new KeyValuePair<string, string>(ReminderKinds.Completion, "任务完成"));
            previewKind.Items.Add(new KeyValuePair<string, string>(ReminderKinds.Permission, "权限审批"));
            previewKind.Items.Add(new KeyValuePair<string, string>(ReminderKinds.Choice, "对话选择"));
            previewKind.DisplayMember = "Value";
            previewKind.ValueMember = "Key";
            previewKind.SelectedIndexChanged += delegate { UpdatePreview(); };
            previewGroup.Controls.Add(sampleLabel);
            previewGroup.Controls.Add(previewKind);

            previewPanel = new Panel { Left = 18, Top = 82, Width = 300, Height = 174, BorderStyle = BorderStyle.FixedSingle };
            previewAccent = new Panel { Left = 0, Top = 0, Width = 6, Height = previewPanel.Height };
            previewDot = new Label { Text = "●", Left = 18, Top = 18, Width = 28, Height = 28, Font = new Font("Segoe UI", 17, FontStyle.Bold) };
            previewTitle = new Label { Left = 54, Top = 20, Width = 230, Height = 31, Font = new Font("Segoe UI", 14, FontStyle.Bold), AutoEllipsis = true };
            previewDescription = new Label { Left = 55, Top = 62, Width = 230, Height = 56, Font = new Font("Segoe UI", 9), AutoEllipsis = true };
            previewPanel.Controls.Add(previewAccent);
            previewPanel.Controls.Add(previewDot);
            previewPanel.Controls.Add(previewTitle);
            previewPanel.Controls.Add(previewDescription);
            previewGroup.Controls.Add(previewPanel);
            Label passThrough = new Label { Text = "浮窗保持置顶且不激活窗口；键盘和鼠标按键可关闭并传递给下层应用。", Left = 16, Top = 274, Width = 300, Height = 42, AutoSize = false, ForeColor = Color.DimGray };
            previewGroup.Controls.Add(passThrough);
            page.Controls.Add(previewGroup);
            return page;
        }

        private void AddColorButton(Control parent, string labelText, string propertyName, int left, int top)
        {
            Label label = new Label { Text = labelText, Left = left, Top = top + 7, Width = 105 };
            Button button = new Button { Text = "选择…", Left = left + 118, Top = top, Width = 180, Height = 30, Tag = propertyName, UseVisualStyleBackColor = false };
            button.Click += ChooseColor;
            colorButtons[propertyName] = button;
            parent.Controls.Add(label);
            parent.Controls.Add(button);
        }

        private void ChooseColor(object sender, EventArgs e)
        {
            Button button = (Button)sender;
            string propertyName = (string)button.Tag;
            ColorDialog dialog = new ColorDialog { FullOpen = true, Color = button.BackColor };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string hex = ColorTranslator.ToHtml(dialog.Color);
            if (propertyName == "BackgroundColor") working.BackgroundColor = hex;
            else if (propertyName == "AccentColor") working.AccentColor = hex;
            else if (propertyName == "TitleColor") working.TitleColor = hex;
            else if (propertyName == "DescriptionColor") working.DescriptionColor = hex;
            UpdatePreview();
        }

        private ComboBox NewChannelCombo()
        {
            ComboBox combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.Add(new KeyValuePair<string, string>(ReminderChannels.Custom, "自定义浮窗"));
            combo.Items.Add(new KeyValuePair<string, string>(ReminderChannels.Native, "Codex 原生通知"));
            combo.Items.Add(new KeyValuePair<string, string>(ReminderChannels.Both, "两者都要"));
            combo.Items.Add(new KeyValuePair<string, string>(ReminderChannels.Off, "关闭本插件此类浮窗"));
            combo.DisplayMember = "Value";
            combo.ValueMember = "Key";
            combo.SelectedValueChanged += ChannelChanged;
            return combo;
        }

        private TextBox NewTextBox(int maxLength)
        {
            return new TextBox { MaxLength = maxLength, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        }

        private void ChannelChanged(object sender, EventArgs e)
        {
            if (loading) return;
            if (sender == completionChannel) working.CompletionChannel = SelectedChannel(completionChannel);
            else if (sender == permissionChannel) working.PermissionChannel = SelectedChannel(permissionChannel);
            else if (sender == choiceChannel) working.ChoiceChannel = SelectedChannel(choiceChannel);
            UpdatePreview();
        }

        private static string SelectedChannel(ComboBox combo)
        {
            if (combo.SelectedItem is KeyValuePair<string, string>)
                return ((KeyValuePair<string, string>)combo.SelectedItem).Key;
            return ReminderChannels.Custom;
        }

        private void LoadIntoControls()
        {
            loading = true;
            globalEnabled.Checked = working.Enabled;
            SelectChannel(completionChannel, working.CompletionChannel);
            SelectChannel(permissionChannel, working.PermissionChannel);
            SelectChannel(choiceChannel, working.ChoiceChannel);
            completionTitle.Text = working.CompletionTitle;
            completionDescription.Text = working.CompletionDescription;
            permissionTitle.Text = working.PermissionTitle;
            permissionDescription.Text = working.PermissionDescription;
            choiceTitle.Text = working.ChoiceTitle;
            choiceDescription.Text = working.ChoiceDescription;
            widthInput.Value = working.Width;
            heightInput.Value = working.Height;
            foreach (KeyValuePair<string, Button> pair in colorButtons)
                pair.Value.BackColor = ColorTranslator.FromHtml(GetColor(pair.Key));
            if (previewKind.Items.Count > 0) previewKind.SelectedIndex = 0;
            loading = false;

            if (!textHandlersAttached)
            {
                completionTitle.TextChanged += delegate { if (!loading) { working.CompletionTitle = completionTitle.Text; UpdatePreview(); } };
                completionDescription.TextChanged += delegate { if (!loading) { working.CompletionDescription = completionDescription.Text; UpdatePreview(); } };
                permissionTitle.TextChanged += delegate { if (!loading) { working.PermissionTitle = permissionTitle.Text; UpdatePreview(); } };
                permissionDescription.TextChanged += delegate { if (!loading) { working.PermissionDescription = permissionDescription.Text; UpdatePreview(); } };
                choiceTitle.TextChanged += delegate { if (!loading) { working.ChoiceTitle = choiceTitle.Text; UpdatePreview(); } };
                choiceDescription.TextChanged += delegate { if (!loading) { working.ChoiceDescription = choiceDescription.Text; UpdatePreview(); } };
                textHandlersAttached = true;
            }
            UpdatePreview();
        }

        private static void SelectChannel(ComboBox combo, string channel)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                KeyValuePair<string, string> item = (KeyValuePair<string, string>)combo.Items[i];
                if (item.Key == channel) { combo.SelectedIndex = i; return; }
            }
            combo.SelectedIndex = 0;
        }

        private void ResetDefaults()
        {
            working = ReminderSettings.Defaults();
            LoadIntoControls();
        }

        private string GetColor(string propertyName)
        {
            if (propertyName == "BackgroundColor") return working.BackgroundColor;
            if (propertyName == "AccentColor") return working.AccentColor;
            if (propertyName == "TitleColor") return working.TitleColor;
            return working.DescriptionColor;
        }

        private void UpdatePreview()
        {
            if (previewPanel == null || previewKind == null) return;
            string kind = ReminderKinds.Completion;
            if (previewKind.SelectedItem is KeyValuePair<string, string>)
                kind = ((KeyValuePair<string, string>)previewKind.SelectedItem).Key;
            previewPanel.BackColor = ColorTranslator.FromHtml(working.BackgroundColor);
            previewAccent.BackColor = ColorTranslator.FromHtml(working.AccentColor);
            previewDot.ForeColor = ColorTranslator.FromHtml(working.AccentColor);
            previewTitle.ForeColor = ColorTranslator.FromHtml(working.TitleColor);
            previewDescription.ForeColor = ColorTranslator.FromHtml(working.DescriptionColor);
            previewTitle.Text = working.GetTitle(kind);
            previewDescription.Text = working.GetDescription(kind);
            previewPanel.Size = new Size(Math.Min(300, working.Width - 24), Math.Min(174, working.Height + 66));
            previewAccent.Height = previewPanel.Height;
            foreach (KeyValuePair<string, Button> pair in colorButtons)
                pair.Value.BackColor = ColorTranslator.FromHtml(GetColor(pair.Key));
        }

    }
}
