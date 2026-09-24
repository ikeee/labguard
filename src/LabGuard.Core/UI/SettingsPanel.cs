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
    /// 按 <see cref="SettingsCatalog"/> 渲染的"全部功能开关"面板（14 组 / 90+ 项）。
    /// 设置程序与安装向导共用同一个控件，保证"装的时候能选、装完也能改"。
    /// </summary>
    public class SettingsPanel : UserControl
    {
        private readonly FlowLayoutPanel _panel;
        private readonly List<KeyValuePair<FieldSpec, Control>> _bindings =
            new List<KeyValuePair<FieldSpec, Control>>();

        public SettingsPanel()
        {
            _panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(6)
            };
            Controls.Add(_panel);
            Build();
        }

        public int FieldCount => _bindings.Count;

        private void Build()
        {
            foreach (string section in SettingsCatalog.All().Select(f => f.Section).Distinct())
            {
                List<FieldSpec> fields = SettingsCatalog.All().Where(f => f.Section == section).ToList();
                _panel.Controls.Add(BuildSection(section, fields));
            }
        }

        private GroupBox BuildSection(string title, List<FieldSpec> fields)
        {
            var box = new GroupBox
            {
                Text = title,
                Width = 830,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(3, 3, 3, 10),
                Padding = new Padding(10)
            };
            var table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));

            foreach (FieldSpec f in fields)
            {
                Control control = BuildControl(f);
                var label = new Label
                {
                    Text = f.Label + (string.IsNullOrEmpty(f.Hint) ? "" : "\r\n" + f.Hint),
                    AutoSize = true,
                    MaximumSize = new Size(465, 0),
                    ForeColor = f.Dangerous ? Color.Firebrick : Color.Black
                };
                table.Controls.Add(label, 0, table.RowCount);
                table.Controls.Add(control, 1, table.RowCount);
                table.RowCount++;
                _bindings.Add(new KeyValuePair<FieldSpec, Control>(f, control));
            }
            box.Controls.Add(table);
            return box;
        }

        private Control BuildControl(FieldSpec f)
        {
            object current = null;   // 初次渲染时由 Refresh(config) 填值
            switch (f.Kind)
            {
                case FieldKind.Bool:
                    return new CheckBox { Checked = false, Text = "开关", AutoSize = true };
                case FieldKind.Choice:
                {
                    var combo = new ComboBox { Width = 310, DropDownStyle = ComboBoxStyle.DropDownList };
                    combo.Items.AddRange(f.Choices.Cast<object>().ToArray());
                    combo.SelectedIndex = 0;
                    return combo;
                }
                case FieldKind.Number:
                    return new NumericUpDown { Width = 120, Minimum = f.Min, Maximum = f.Max, Value = f.Min };
                case FieldKind.List:
                    return new TextBox { Multiline = true, Width = 310, Height = 70, ScrollBars = ScrollBars.Vertical };
                case FieldKind.Path:
                {
                    var wrap = new Panel { Width = 310, Height = 30 };
                    var text = new TextBox { Width = 220, Text = Convert.ToString(current) ?? "" };
                    var browse = new Button { Text = "浏览…", Left = 226, Width = 80, Height = 24 };
                    browse.Click += (s, e) =>
                    {
                        using (var dlg = new OpenFileDialog { Filter = f.Filter ?? "所有文件|*.*" })
                        {
                            if (dlg.ShowDialog() == DialogResult.OK) text.Text = dlg.FileName;
                        }
                    };
                    wrap.Controls.Add(text);
                    wrap.Controls.Add(browse);
                    return wrap;
                }
                default:
                    return new TextBox { Width = 300 };
            }
        }

        private static string[] Lines(object value)
        {
            var list = value as IEnumerable<string>;
            return list == null ? new string[0] : list.ToArray();
        }

        /// <summary>把控件上的值写回配置对象。</summary>
        public void Collect(GuardConfig config)
        {
            foreach (var kv in _bindings)
            {
                FieldSpec f = kv.Key;
                Control c = kv.Value;
                switch (f.Kind)
                {
                    case FieldKind.Bool:
                        f.Set(config, ((CheckBox)c).Checked);
                        break;
                    case FieldKind.Choice:
                        f.Set(config, f.Values[Math.Max(0, ((ComboBox)c).SelectedIndex)]);
                        break;
                    case FieldKind.Number:
                        f.Set(config, (int)((NumericUpDown)c).Value);
                        break;
                    case FieldKind.List:
                        f.Set(config, ((TextBox)c).Lines.Select(x => x.Trim()).Where(x => x.Length > 0).ToList());
                        break;
                    case FieldKind.Path:
                        f.Set(config, c.Controls.OfType<TextBox>().First().Text.Trim());
                        break;
                    default:
                        f.Set(config, ((TextBox)c).Text.Trim());
                        break;
                }
            }
        }

        /// <summary>用配置对象的值刷新界面。</summary>
        public void Refresh(GuardConfig config)
        {
            foreach (var kv in _bindings)
            {
                FieldSpec f = kv.Key;
                Control c = kv.Value;
                object v = f.Get(config);
                switch (f.Kind)
                {
                    case FieldKind.Bool: ((CheckBox)c).Checked = v is bool && (bool)v; break;
                    case FieldKind.Choice:
                        int idx = Array.IndexOf(f.Values, Convert.ToString(v));
                        ((ComboBox)c).SelectedIndex = idx >= 0 ? idx : 0;
                        break;
                    case FieldKind.Number:
                        decimal d;
                        ((NumericUpDown)c).Value = decimal.TryParse(Convert.ToString(v), out d)
                            ? Math.Min(Math.Max(d, f.Min), f.Max) : f.Min;
                        break;
                    case FieldKind.List: ((TextBox)c).Text = string.Join("\r\n", Lines(v)); break;
                    case FieldKind.Path: c.Controls.OfType<TextBox>().First().Text = Convert.ToString(v) ?? ""; break;
                    default: ((TextBox)c).Text = Convert.ToString(v) ?? ""; break;
                }
            }
        }
    }
}
