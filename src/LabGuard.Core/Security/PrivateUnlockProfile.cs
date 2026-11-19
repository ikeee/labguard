using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using LabGuard.Core.Interop;

namespace LabGuard.Core.Security
{
    /// <summary>
    /// 从构建时注入的私有资源读取解锁资料。
    /// 公开源码不包含组合本身；没有注入时只保留密码通道。
    /// </summary>
    internal static class PrivateUnlockProfile
    {
        private const string ResourceSuffix = ".private-unlock.profile";
        private static bool _loaded;
        private static UnlockGestureProfile _cached;

        public static bool TryGet(out UnlockGestureProfile profile)
        {
            if (_loaded)
            {
                profile = _cached;
                return profile != null;
            }
            _loaded = true;
            _cached = Load();
            profile = _cached;
            return profile != null;
        }

        private static UnlockGestureProfile Load()
        {
            try
            {
                Assembly assembly = typeof(PrivateUnlockProfile).Assembly;
                foreach (string name in assembly.GetManifestResourceNames())
                {
                    if (!name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream stream = assembly.GetManifestResourceStream(name))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        return Parse(reader.ReadToEnd());
                }
            }
            catch
            {
                // 私有资源损坏时退化为仅密码通道，不影响程序启动。
            }
            return null;
        }

        private static UnlockGestureProfile Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int[] modifiers = null;
            string[] sequence = null;
            int window = 2;

            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = line.Trim();
                if (value.Length == 0 || value[0] == '#') continue;
                int eq = value.IndexOf('=');
                if (eq <= 0) continue;
                string key = value.Substring(0, eq).Trim().ToLowerInvariant();
                string raw = value.Substring(eq + 1).Trim();
                if (key == "m")
                {
                    var values = new System.Collections.Generic.List<int>();
                    foreach (string part in raw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int number;
                        if (!TryParseInt(part, out number))
                            return null;
                        values.Add(number);
                    }
                    modifiers = values.ToArray();
                }
                else if (key == "s")
                {
                    sequence = UnlockGesture.ParseSequence(raw);
                }
                else if (key == "w")
                {
                    int parsed;
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        window = parsed;
                }
            }

            var profile = new UnlockGestureProfile(modifiers, sequence, window);
            return profile.IsValid ? profile : null;
        }

        private static bool TryParseInt(string text, out int value)
        {
            value = 0;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(text.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out value);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
