using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;
using LabGuard.Core.Config;

namespace LabGuard.Core.Settings
{
    /// <summary>
    /// 开关目录契约快照导出（<c>labguard-catalog/1</c>）。
    ///
    /// 这是**双仓契约的另一半**：教师机服务端（labguard-server）不手写任何开关，
    /// 它加载这份快照来动态渲染策略面板——所以客户端一旦增删开关，
    /// 必须重出快照并同步进服务端仓库（否则教师机面板与真客户端开关静默漂移）。
    ///
    /// 手写 JSON 而不是用 JavaScriptSerializer：要保证**逐字节稳定**（同样的开关就出同样的文件），
    /// 服务端仓库里那份快照才有可能做 diff 审查。
    /// </summary>
    public static class CatalogExport
    {
        public const string ExpectedFormat = "labguard-catalog/1";

        public static string Build()
        {
            return Build(AppInfo.Version, DateTime.Now);
        }

        public static string Build(string clientVersion, DateTime generatedAt)
        {
            var all = SettingsCatalog.All();
            string defaultPolicyJson = ConfigStore.Serialize(new GuardConfig());

            var sb = new StringBuilder(64 * 1024);
            sb.Append("{\"format\":").Append(Quote(ExpectedFormat));
            sb.Append(",\"clientVersion\":").Append(Quote(clientVersion));
            sb.Append(",\"generatedAt\":").Append(Quote(generatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sb.Append(",\"fieldCount\":").Append(all.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"defaultPolicy\":").Append(Quote(defaultPolicyJson));
            sb.Append(",\"fields\":[");
            for (int i = 0; i < all.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendField(sb, all[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>一个字段 → JSON 对象（键顺序固定，便于 diff）。</summary>
        private static void AppendField(StringBuilder sb, FieldSpec f)
        {
            object def = f.Get(new GuardConfig());
            sb.Append("{\"section\":").Append(Quote(f.Section));
            sb.Append(",\"path\":").Append(Quote(f.Path));
            sb.Append(",\"label\":").Append(Quote(f.Label));
            sb.Append(",\"hint\":").Append(Quote(f.Hint));
            sb.Append(",\"kind\":").Append(Quote(f.Kind.ToString()));
            sb.Append(",\"choices\":").Append(stringArray(f.Choices));
            sb.Append(",\"values\":").Append(stringArray(f.Values));
            sb.Append(",\"min\":").Append(f.Min.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"max\":").Append(f.Max.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"dangerous\":").Append(f.Dangerous ? "true" : "false");
            sb.Append(",\"default\":").Append(Value(def));
            sb.Append('}');
        }

        /// <summary>
        /// 契约完整性自检（与服务端 <c>CatalogDoc.ValidateAgainstDefaultPolicy</c> 同义）：
        /// 每个字段的 path 都必须能在 defaultPolicy（= <c>new GuardConfig()</c> 的 JSON）里解析到值。
        /// 返回错误清单，空 = 通过。教师机面板就是按这份快照渲染的，path 解析不到 = 面板上会出现空控件。
        /// </summary>
        public static List<string> ValidateCoverage()
        {
            var errors = new List<string>();
            var defaults = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }
                .DeserializeObject(ConfigStore.Serialize(new GuardConfig())) as Dictionary<string, object>;
            if (defaults == null) return new List<string> { "默认策略不是合法 JSON 对象" };
            foreach (FieldSpec f in SettingsCatalog.All())
            {
                if (Resolve(defaults, f.Path) == null) errors.Add("默认策略里找不到 " + f.Path);
            }
            return errors;
        }

        /// <summary>按 <c>A.B.C</c> 逐段取值（JavaScriptSerializer 的嵌套对象是 Dictionary&lt;string,object&gt;）。</summary>
        private static object Resolve(Dictionary<string, object> root, string path)
        {
            object cur = root;
            foreach (string seg in (path ?? "").Split('.'))
            {
                if (!(cur is Dictionary<string, object> dict)) return null;
                if (!dict.TryGetValue(seg, out cur)) return null;
            }
            return cur;
        }

        private static string stringArray(string[] items)
        {
            if (items == null) return "null";
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(items[i]));
            }
            return sb.Append(']').ToString();
        }

        /// <summary>默认值 → JSON（Bool / 字符串 / 整数 / 字符串列表 四种，目录里的 Kind 只产生这几种）。</summary>
        private static string Value(object v)
        {
            if (v == null) return "null";
            if (v is bool b) return b ? "true" : "false";
            if (v is string s) return Quote(s);
            if (v is IEnumerable<string> list)
            {
                var sb = new StringBuilder("[");
                bool first = true;
                foreach (string item in list)
                {
                    if (!first) sb.Append(',');
                    sb.Append(Quote(item));
                    first = false;
                }
                return sb.Append(']').ToString();
            }
            if (v is IConvertible) return Convert.ToDecimal(v, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
            return Quote(v.ToString());
        }

        /// <summary>JSON 字符串字面量（含转义；中文原样输出，服务端按 UTF-8 读）。</summary>
        private static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
