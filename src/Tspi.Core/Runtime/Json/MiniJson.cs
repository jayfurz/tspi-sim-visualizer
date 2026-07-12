using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tspi.Core.Json
{
    /// <summary>
    /// Minimal JSON codec used for .tspi footers so Tspi.Core carries zero package
    /// dependencies (a hard requirement for painless Unity consumption).
    ///
    /// Scope: parses/serializes the standard JSON data model into plain CLR types:
    ///   object -> Dictionary&lt;string, object&gt; (insertion-ordered)
    ///   array  -> List&lt;object&gt;
    ///   string -> string, true/false -> bool, null -> null
    ///   number -> long when integral and in range, otherwise double
    ///
    /// It fully handles escape sequences including \uXXXX (surrogate halves are
    /// appended as-is, which is correct for UTF-16 strings). It is NOT a general
    /// user-input parser: manifests are parsed by Tspi.Sim with System.Text.Json;
    /// this codec only reads footers that this library itself wrote.
    /// </summary>
    public static class MiniJson
    {
        private const int MaxDepth = 128;

        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int pos = 0;
            object value = ParseValue(text, ref pos, 0);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
                throw new FormatException("Trailing content at position " + pos);
            return value;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        // ---------------- parsing ----------------

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }

        private static object ParseValue(string s, ref int pos, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON nesting too deep");
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos, depth + 1);
                case '[': return ParseArray(s, ref pos, depth + 1);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref pos);
                    throw new FormatException("Unexpected character '" + c + "' at position " + pos);
            }
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new FormatException("Invalid literal at position " + pos);
            pos += literal.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int pos, int depth)
        {
            var dict = new Dictionary<string, object>();
            pos++; // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"')
                    throw new FormatException("Expected object key at position " + pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                    throw new FormatException("Expected ':' at position " + pos);
                pos++;
                dict[key] = ParseValue(s, ref pos, depth);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated object");
                char c = s[pos];
                if (c == ',') { pos++; continue; }
                if (c == '}') { pos++; return dict; }
                throw new FormatException("Expected ',' or '}' at position " + pos);
            }
        }

        private static List<object> ParseArray(string s, ref int pos, int depth)
        {
            var list = new List<object>();
            pos++; // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref pos, depth));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated array");
                char c = s[pos];
                if (c == ',') { pos++; continue; }
                if (c == ']') { pos++; return list; }
                throw new FormatException("Expected ',' or ']' at position " + pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new FormatException("Unterminated string");
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) throw new FormatException("Unterminated escape");
                char e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new FormatException("Truncated \\u escape");
                        sb.Append((char)ParseHex4(s, pos));
                        pos += 4;
                        break;
                    default: throw new FormatException("Invalid escape '\\" + e + "'");
                }
            }
        }

        private static int ParseHex4(string s, int pos)
        {
            int v = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = s[pos + i];
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
                else throw new FormatException("Invalid hex digit in \\u escape");
                v = (v << 4) | d;
            }
            return v;
        }

        private static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            bool isIntegral = true;
            if (pos < s.Length && s[pos] == '-') pos++;
            while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            if (pos < s.Length && s[pos] == '.')
            {
                isIntegral = false;
                pos++;
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            }
            if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
            {
                isIntegral = false;
                pos++;
                if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) pos++;
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            }
            string token = s.Substring(start, pos - start);
            if (token.Length == 0 || token == "-")
                throw new FormatException("Invalid number at position " + start);
            if (isIntegral && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return l;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            throw new FormatException("Invalid number '" + token + "' at position " + start);
        }

        // ---------------- serialization ----------------

        private static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("JSON nesting too deep");
            if (value == null) { sb.Append("null"); return; }
            switch (value)
            {
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: WriteString(sb, s); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case uint u: sb.Append(u.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case double d: WriteDouble(sb, d); return;
                case float f: WriteDouble(sb, f); return;
                case IDictionary<string, object> dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        WriteValue(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable<object> seq:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in seq)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                    throw new InvalidOperationException(
                        "MiniJson cannot serialize type " + value.GetType().FullName);
            }
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new InvalidOperationException("JSON cannot represent NaN/Infinity");
            // Round-trippable shortest form; force a decimal marker so a reparse
            // yields double (not long) and the value round-trips type-stably.
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            sb.Append(s);
            if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0 && s.IndexOf('E') < 0)
                sb.Append(".0");
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
