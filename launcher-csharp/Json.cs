using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace KartDriftLocal
{
    static class J
    {
        // The original launcher used System.Web.Script.Serialization. That type
        // does not exist outside .NET Framework, so the wire format is reproduced
        // on top of System.Text.Json: objects become Dictionary<string, object>,
        // arrays become List<object>, integral numbers become long, fractional
        // numbers become decimal, and non-ASCII text is escaped as \uXXXX exactly
        // like the framework serializer did.
        public static object Read(string text)
        {
            using (var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 200 }))
                return Normalize(document.RootElement);
        }
        static object Normalize(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject()) map[property.Name] = Normalize(property.Value);
                    return map;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in element.EnumerateArray()) list.Add(Normalize(item));
                    return list;
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number:
                    long integer; if (element.TryGetInt64(out integer)) return integer;
                    decimal precise; if (element.TryGetDecimal(out precise)) return precise;
                    return element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
        public static string Write(object value) { var text = new StringBuilder(256); Append(text, value); return text.ToString(); }
        static void Append(StringBuilder text, object value)
        {
            if (value == null) { text.Append("null"); return; }
            var literal = value as string;
            if (literal != null) { AppendString(text, literal); return; }
            if (value is bool) { text.Append((bool)value ? "true" : "false"); return; }
            if (value is char) { AppendString(text, value.ToString()); return; }
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                text.Append('{'); bool first = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!first) text.Append(','); first = false;
                    AppendString(text, Convert.ToString(entry.Key, CultureInfo.InvariantCulture)); text.Append(':'); Append(text, entry.Value);
                }
                text.Append('}'); return;
            }
            var sequence = value as IEnumerable;
            if (sequence != null)
            {
                text.Append('['); bool first = true;
                foreach (var item in sequence) { if (!first) text.Append(','); first = false; Append(text, item); }
                text.Append(']'); return;
            }
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.SByte: case TypeCode.Byte:
                case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32:
                case TypeCode.Int64:
                    text.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return;
                case TypeCode.UInt64:
                    text.Append(((ulong)value).ToString(CultureInfo.InvariantCulture)); return;
                case TypeCode.Decimal:
                    text.Append(((decimal)value).ToString(CultureInfo.InvariantCulture)); return;
                case TypeCode.Double:
                    text.Append(((double)value).ToString("R", CultureInfo.InvariantCulture)); return;
                case TypeCode.Single:
                    text.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)); return;
                default:
                    AppendString(text, Convert.ToString(value, CultureInfo.InvariantCulture)); return;
            }
        }
        static void AppendString(StringBuilder text, string value)
        {
            text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        // Match JavaScriptSerializer: escape control characters,
                        // markup characters and everything outside printable ASCII.
                        if (c < ' ' || c > '~' || c == '<' || c == '>' || c == '&' || c == '\'')
                            text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }
        public static object Copy(object value) { return Read(Write(value)); }
        public static Dictionary<string, object> D(object value) { var d = value as Dictionary<string, object>; if (d == null) throw new ArgumentException("Expected object"); return d; }
        public static List<object> A(object value) { var a = value as List<object>; if (a == null) throw new ArgumentException("Expected array"); return a; }
        public static Dictionary<string, object> M(params object[] pairs)
        { var result = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1]; return result; }
        public static List<object> L(params object[] values) { return values.ToList(); }
        public static object Get(Dictionary<string, object> d, string key, object fallback = null) { object v; return d.TryGetValue(key, out v) ? v : fallback; }
        public static Dictionary<string, object> Map(Dictionary<string, object> d, string key) { return D(d[key]); }
        public static List<object> List(Dictionary<string, object> d, string key) { return A(d[key]); }
        public static Dictionary<string, object> EnsureMap(Dictionary<string, object> d, string key) { if (!d.ContainsKey(key)) d[key] = M(); return D(d[key]); }
        public static List<object> EnsureList(Dictionary<string, object> d, string key) { if (!d.ContainsKey(key)) d[key] = L(); return A(d[key]); }
        public static string S(object value) { if (!(value is string)) throw new ArgumentException("Expected string"); return (string)value; }
        public static bool IsInt(object value) { return value is int || value is long; }
        public static long I(object value) { if (!IsInt(value)) throw new ArgumentException("Expected integer"); return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        public static double N(object value) { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        public static bool B(object value) { if (!(value is bool)) throw new ArgumentException("Expected boolean"); return (bool)value; }
        public static bool Truth(object value)
        { if (value == null) return false; if (value is string) return ((string)value).Length != 0; if (value is bool) return (bool)value; if (value is ICollection) return ((ICollection)value).Count != 0; return N(value) != 0; }
        public static bool Same(object a, object b)
        {
            var x = a as Dictionary<string, object>; var y = b as Dictionary<string, object>;
            if (x != null && y != null) return x.Count == y.Count && x.All(p => y.ContainsKey(p.Key) && Same(p.Value, y[p.Key]));
            var l = a as List<object>; var r = b as List<object>;
            if (l != null && r != null) return l.Count == r.Count && !l.Where((v, i) => !Same(v, r[i])).Any();
            if (a == null || b == null) return a == b;
            if (IsInt(a) && IsInt(b)) return I(a) == I(b);
            return a.Equals(b);
        }
        public static void Put(Dictionary<string, object> target, Dictionary<string, object> values) { foreach (var p in values) target[p.Key] = p.Value; }
        public static string Now() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture); }
        public static long Millis() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
        public static void Need(bool valid, string message) { if (!valid) throw new ArgumentException(message); }
        public static bool NumericId(object value) { return value is string && ((string)value).Length > 0 && ((string)value).All(c => c >= '0' && c <= '9'); }
    }

    static class Bundle
    {
        static readonly Dictionary<string, string> Files = Load();
        static Dictionary<string, string> Load()
        {
            var files = new Dictionary<string, string>();
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("LocalData.zip"))
            {
                if (stream == null) throw new InvalidOperationException("LocalData.zip is not embedded; build with rebuild.ps1.");
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    foreach (var entry in zip.Entries)
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) files[entry.FullName] = reader.ReadToEnd();
            }
            return files;
        }
        public static string Text(string name) { return Files[name]; }
        public static Dictionary<string, object> Data(string name) { return J.D(J.Read(Text("data/" + name + ".json"))); }
        public static Dictionary<string, object> Templates() { return J.D(J.Read(Text("release/responses.json"))); }
    }
}
