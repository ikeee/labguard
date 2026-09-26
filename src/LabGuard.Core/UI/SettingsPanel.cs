using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Settings;

namespace LabGuard.Core.UI
{
    /// <summary>
    /// 详细的自定义开关管理面板：
    ///   左侧 = 分组导航（带"已改动"标记）  右上 = 搜索 / 只看已改动 / 改动计数  右下 = 该组全部开关
    /// 面板由 <see cref="SettingsCatalog"/> 驱动，因此 GuardConfig 里每一项都会出现在这里。
    /// </summary>
    public class SettingsPanel : UserControl
    {
        private readonly ListBox _nav = new ListBox();
        private readonly Panel _host = new Panel();          // 放当前分组的 GroupBox
        private readonly TextBox _search = new TextBox();
        private readonly CheckBox _changedOnly = new CheckBox { Text = "只看已改动" };
        private readonly Label _counter = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly Dictionary<string, GroupBox> _boxes = new Dictionary<string, GroupBox>();
        private readonly List<KeyValuePair<FieldSpec, Control>> _bindings = new List<KeyValuePair<FieldSpec, Control>>();
        private readonly List<string> _sections = new List<string>();
        private readonly GuardConfig _defaults = new GuardConfig();
        private GuardConfig _config;

        public SettingsPanel()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 6, 4, 0) };
            _search.Width = 220;
            _search.TextChanged += (s, e) => ApplyFilter();
            _changedOnly.Left = 240; _changedOnly.Top = 8; _changedOnly.Width = 110;
            _changedOnly.CheckedChanged += (s, e) => ApplyFilter();
            _counter.Left = 366; _counter.Top = 10;
            top.Controls.Add(_search);
            top.Controls.Add(_changedOnly);
            top.Controls.Add(_counter);

            _nav.Dock = DockStyle.Left;
            _nav.Width = 240;
            _nav.IntegralHeight = false;
            _nav.SelectedIndexChanged += (s, e) => ShowSection();

            _host.Dock = DockStyle.Fill;
            _host.AutoScroll = true;
            _host.Padding = new Padding(6, 4, 6, 6);

            Controls.Add(_host);
            Controls.Add(_nav);
            Controls.Add(top);
            Build();
        }

        public int FieldCount => _bindings.Count;

        /// <summary>选中第 n 个分组（0 起）；供向导/自检使用。</summary>
        public void SelectSection(int index)
        {
            if (index >= 0 && index < _nav.Items.Count) _nav.SelectedIndex = index;
        }

        /// <summary>按分组标题选中（找不到返回 false）。</summary>
        public bool SelectSectionByName(string namePart)
        {
            for (int i = 0; i < _sections.Count; i++)
            {
                if (_sections[i].IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _nav.SelectedIndex = i;
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ 构建
        private void Build()
        {
            foreach (string section in SettingsCatalog.All().Select(f => f.Section).Distinct())
            {
                _sections.Add(section);
                var box = new GroupBox
                {
                    Text = section,
                    Width = 800,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(3, 3, 3, 10),
                    Padding = new Padding(10),
                    Dock = DockStyle.Top
                };
                var table = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top
                };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
                foreach (FieldSpec f in SettingsCatalog.All().Where(x => x.Section == section))
                {
                    Control control = BuildControl(f);
                    var label = new Label
                    {
                        Text = f.Label + (string.IsNullOrEmpty(f.Hint) ? "" : "\r\n" + f.Hint),
                        AutoSize = true,
                        MaximumSize = new Size(435, 0),
                        ForeColor = f.Dangerous ? Color.Firebrick : Color.Black
                    };
                    table.Controls.Add(label, 0, table.RowCount);
                    table.Controls.Add(control, 1, table.RowCount);
                    table.RowCount++;
                    _bindings.Add(new KeyValuePair<FieldSpec, Control>(f, control));
                    control.Tag = f.Path;
                }
                box.Controls.Add(table);
                _boxes[section] = box;
            }
            _nav.Items.AddRange(_sections.Cast<object>().ToArray());
            if (_nav.Items.Count > 0) _nav.SelectedIndex = 0;
        }

        private Control BuildControl(FieldSpec f)
        {
            switch (f.Kind)
            {
                case FieldKind.Bool:
                    return new CheckBox { Checked = false, Text = "开关", AutoSize = true };
                case FieldKind.Choice:
                {
                    var combo = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
                    combo.Items.AddRange(f.Choices.Cast<object>().ToArray());
                    combo.SelectedIndex = 0;
                    return combo;
                }
                case FieldKind.Number:
                    return new NumericUpDown { Width = 110, Minimum = f.Min, Maximum = f.Max, Value = f.Min };
                case FieldKind.List:
                    return new TextBox { Multiline = true, Width = 300, Height = 72, ScrollBars = ScrollBars.Vertical };
                case FieldKind.Path:
                {
                    var wrap = new Panel { Width = 330, Height = 30 };
                    var text = new TextBox { Width = 150 };
                    var browse = new Button { Text = "浏览…", Left = 156, Width = 80, Height = 24 };
                    browse.Click += (s, e) =>
                    {
                        using (var dlg = new OpenFileDialog { Filter = f.Filter ?? "所有文件|*.*" })
                        {
                            if (dlg.ShowDialog() == DialogResult.OK) text.Text = dlg.FileName;
                        }
                    };
                    wrap.Controls.Add(text);
                    wrap.Controls.Add(browse);
                    // 「课堂软件学生端」额外给一个"自动识别"按钮（识别不到时可手动填写）
                    if (f.Path == "Classroom.MainExecutable")
                    {
                        var detect = new Button { Text = "自动识别", Left = 240, Width = 90, Height = 24 };
                        detect.Click += (s, e) =>
                        {
                            string picked = ClassroomPickDialog.Pick(this, text.Text);
                            if (!string.IsNullOrEmpty(picked)) text.Text = picked;
                        };
                        wrap.Controls.Add(detect);
                    }
                    return wrap;
                }
                default:
                    return new TextBox { Width = 300 };
            }
        }

        private void ShowSection()
        {
            int idx = _nav.SelectedIndex;
            if (idx < 0 || idx >= _sections.Count) return;
            _host.Controls.Clear();
            GroupBox box = _boxes[_sections[idx]];
            _host.Controls.Add(box);
            ApplyFilter();
        }

        // ------------------------------------------------------------------ 过滤 / 计数
        private void ApplyFilter()
        {
            string q = (_search.Text ?? "").Trim();
            int shown = 0, changed = 0, total = 0;
            for (int i = 0; i < _bindings.Count; i++)
            {
                FieldSpec f = _bindings[i].Key;
                Control c = _bindings[i].Value;
                if (f.Section != CurrentSection()) continue;
                total++;
                bool isChanged = IsChanged(f);
                if (isChanged) changed++;
                bool match = q.Length == 0 ||
                             (f.Label ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (f.Hint ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (f.Path ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                bool visible = match && (!_changedOnly.Checked || isChanged);
                SetRowVisible(c, visible);
                if (visible) shown++;
            }
            // 左侧导航标记每组的改动数
            for (int s = 0; s < _sections.Count; s++)
            {
                int n = _bindings.Count(kv => kv.Key.Section == _sections[s] && IsChanged(kv.Key));
                string text = _sections[s] + (n > 0 ? "   （已改 " + n + "）" : "");
                int selected = _nav.SelectedIndex;
                if (_nav.Items[s] as string != text)
                {
                    _nav.Items[s] = text;
                    if (selected == s) _nav.SelectedIndex = s;
                }
            }
            int totalChanged = _bindings.Count(kv => IsChanged(kv.Key));
            _counter.Text = string.Format("本组显示 {0}/{1} 项 · 全局已改动 {2} 项", shown, total, totalChanged);
        }

        private string CurrentSection() => _nav.SelectedIndex >= 0 && _nav.SelectedIndex < _sections.Count
            ? _sections[_nav.SelectedIndex] : null;

        private static void SetRowVisible(Control control, bool visible)
        {
            // 控件与它左边的标题在同一行：两个都控制
            control.Visible = visible;
            var table = control.Parent as TableLayoutPanel;
            if (table == null) return;
            int row = table.GetRow(control);
            foreach (Control sibling in table.GetControlFromPosition(0, row) != null
                         ? new[] { table.GetControlFromPosition(0, row) } : new Control[0])
            {
                if (sibling != null) sibling.Visible = visible;
            }
        }

        private bool IsChanged(FieldSpec f)
        {
            try
            {
                object now = ReadControl(f);
                object def = f.Get(_defaults);
                if (now == null || def == null) return !Equals(now, def);
                if (now is List<string> || def is List<string>)
                {
                    string a = string.Join("|", ((IEnumerable<string>)now) ?? new List<string>());
                    string b = string.Join("|", ((IEnumerable<string>)def) ?? new List<string>());
                    return a != b;
                }
                return !Equals(Convert.ToString(now), Convert.ToString(def));
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ 读写
        private object ReadControl(FieldSpec f)
        {
            Control c = _bindings.First(kv => kv.Key == f).Value;
            switch (f.Kind)
            {
                case FieldKind.Bool: return ((CheckBox)c).Checked;
                case FieldKind.Choice: return f.Values[Math.Max(0, ((ComboBox)c).SelectedIndex)];
                case FieldKind.Number: return (int)((NumericUpDown)c).Value;
                case FieldKind.List: return ((TextBox)c).Lines.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                case FieldKind.Path: return c.Controls.OfType<TextBox>().First().Text.Trim();
                default: return ((TextBox)c).Text.Trim();
            }
        }

        /// <summary>把控件上的值写回配置对象。</summary>
        public void Collect(GuardConfig config)
        {
            foreach (var kv in _bindings)
            {
                kv.Key.Set(config, ReadControl(kv.Key));
            }
            _config = config;
            ApplyFilter();
        }

        /// <summary>用配置对象的值刷新界面。</summary>
        public void Refresh(GuardConfig config)
        {
            _config = config;
            foreach (var kv in _bindings)
            {
                FieldSpec f = kv.Key;
                Control c = kv.Value;
                object v = f.Get(config);
                switch (f.Kind)
                {
                    case FieldKind.Bool: ((CheckBox)c).Checked = v is bool && (bool)v; break;
                    case FieldKind.Choice:
                    {
                        int idx = Array.IndexOf(f.Values, Convert.ToString(v));
                        ((ComboBox)c).SelectedIndex = idx >= 0 ? idx : 0;
                        break;
                    }
                    case FieldKind.Number:
                    {
                        decimal d;
                        ((NumericUpDown)c).Value = decimal.TryParse(Convert.ToString(v), out d)
                            ? Math.Min(Math.Max(d, f.Min), f.Max) : f.Min;
                        break;
                    }
                    case FieldKind.List:
                        ((TextBox)c).Text = string.Join("\r\n", Lines(v));
                        break;
                    case FieldKind.Path:
                        c.Controls.OfType<TextBox>().First().Text = Convert.ToString(v) ?? "";
                        break;
                    default:
                        ((TextBox)c).Text = Convert.ToString(v) ?? "";
                        break;
                }
            }
            ApplyFilter();
        }

        private static string[] Lines(object value)
        {
            var list = value as IEnumerable<string>;
            return list == null ? new string[0] : list.ToArray();
        }
    }
}
