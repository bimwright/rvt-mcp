using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace RvtMcp.Scripting
{
    public sealed class ScriptJsonDocument
    {
        public string CompactJson { get; }
        public string CanonicalJson { get; }
        public bool IsObject { get; }
        public bool IsNull { get; }

        internal ScriptJsonDocument(string compact, string canonical, bool isObject, bool isNull)
        {
            CompactJson = compact;
            CanonicalJson = canonical;
            IsObject = isObject;
            IsNull = isNull;
        }
    }

    /// <summary>
    /// Strict JSON reader retaining number lexemes. Newtonsoft's floating-point token
    /// conversion cannot preserve the exact coefficient/exponent required for identity.
    /// </summary>
    public static class ScriptJson
    {
        public static ScriptJsonDocument Parse(string json) => Parse(json, null);

        internal static ScriptJsonDocument Parse(string json, int? compactByteLimit)
        {
            if (json == null) throw Invalid();
            var node = new Reader(json).Read();
            var compact = node.Render(false);
            if (compactByteLimit.HasValue && ScriptIdentity.Utf8Bytes(compact) > compactByteLimit.Value)
                throw new ScriptContractException(ScriptErrorCodes.DataTooLarge, "Compact script data exceeds its UTF-8 budget.");
            // Only runtime data reaches the entry shim; identity hashing keeps accepting any exponent.
            if (compactByteLimit.HasValue) node.RequireFiniteDoubles();
            return new ScriptJsonDocument(compact, node.Render(true), node.Kind == '{',
                node.Kind == 'l' && node.Text == "null");
        }

        private static ScriptContractException Invalid()
            => new ScriptContractException(ScriptErrorCodes.InputInvalid, "Invalid, duplicate-key or excessively nested script JSON.");

        private sealed class Node
        {
            internal char Kind;
            internal string Text;
            internal List<KeyValuePair<string, Node>> Properties;
            internal List<Node> Items;

            internal string Render(bool canonical)
            {
                if (Kind == 's') return Quote(Text);
                if (Kind == 'n') return canonical ? NormalizeNumber(Text) : Text;
                if (Kind == 'l') return Text;
                if (Kind == '[') return "[" + string.Join(",", Items.Select(n => n.Render(canonical))) + "]";
                IEnumerable<KeyValuePair<string, Node>> properties = Properties;
                if (canonical) properties = properties.OrderBy(p => p.Key, StringComparer.Ordinal);
                return "{" + string.Join(",", properties.Select(p => Quote(p.Key) + ":" + p.Value.Render(canonical))) + "}";
            }

            // The shim reads fraction/exponent numbers as double: .NET Framework throws on overflow while newer
            // runtimes return Infinity. Integer lexemes stay exact (Int64/BigInteger) and are not limited here.
            internal void RequireFiniteDoubles()
            {
                if (Kind == 'n' && Text.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0
                    && (!double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsInfinity(value)))
                    throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Script data numbers with a fraction or exponent must fit a finite double.");
                foreach (var item in Items ?? Enumerable.Empty<Node>()) item.RequireFiniteDoubles();
                foreach (var property in Properties ?? Enumerable.Empty<KeyValuePair<string, Node>>()) property.Value.RequireFiniteDoubles();
            }
        }

        private sealed class Reader
        {
            private readonly string _json;
            private int _offset;
            internal Reader(string json) { _json = json; }
            private char Peek => _offset < _json.Length ? _json[_offset] : '\0';
            private void White() { while (Peek == ' ' || Peek == '\t' || Peek == '\r' || Peek == '\n') _offset++; }
            private bool Take(char value) { if (Peek != value) return false; _offset++; return true; }
            private void Expect(char value) { if (!Take(value)) throw Invalid(); }
            private static bool Digit(char value) => value >= '0' && value <= '9';

            internal Node Read()
            {
                var node = Value(0);
                White();
                if (_offset != _json.Length) throw Invalid();
                return node;
            }

            private Node Value(int depth)
            {
                White();
                var kind = Peek;
                if (kind == '{' || kind == '[')
                {
                    if (depth >= ScriptLimits.JsonDepth) throw Invalid();
                    _offset++;
                    var node = new Node { Kind = kind };
                    White();
                    if (kind == '[')
                    {
                        node.Items = new List<Node>();
                        if (Take(']')) return node;
                        do { node.Items.Add(Value(depth + 1)); White(); } while (Take(','));
                        Expect(']');
                    }
                    else
                    {
                        node.Properties = new List<KeyValuePair<string, Node>>();
                        var keys = new HashSet<string>(StringComparer.Ordinal);
                        if (Take('}')) return node;
                        do
                        {
                            White();
                            var key = String();
                            if (!keys.Add(key)) throw Invalid();
                            White();
                            Expect(':');
                            node.Properties.Add(new KeyValuePair<string, Node>(key, Value(depth + 1)));
                            White();
                        } while (Take(','));
                        Expect('}');
                    }
                    return node;
                }
                if (kind == '"') return new Node { Kind = 's', Text = String() };
                if (kind == '-' || Digit(kind)) return new Node { Kind = 'n', Text = Number() };
                foreach (var literal in new[] { "true", "false", "null" })
                    if (_json.Length - _offset >= literal.Length &&
                        string.CompareOrdinal(_json, _offset, literal, 0, literal.Length) == 0)
                    {
                        _offset += literal.Length;
                        return new Node { Kind = 'l', Text = literal };
                    }
                throw Invalid();
            }

            private string String()
            {
                Expect('"');
                var value = new StringBuilder();
                while (_offset < _json.Length)
                {
                    var ch = _json[_offset++];
                    if (ch == '"') return value.ToString();
                    if (ch < 32) throw Invalid();
                    if (ch != '\\') { value.Append(ch); continue; }
                    if (_offset == _json.Length) throw Invalid();
                    var escaped = _json[_offset++];
                    switch (escaped)
                    {
                        case '"': case '\\': case '/': value.Append(escaped); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            if (_json.Length - _offset < 4) throw Invalid();
                            var code = 0;
                            for (var i = 0; i < 4; i++)
                            {
                                var hex = _json[_offset++];
                                var digit = hex >= '0' && hex <= '9' ? hex - '0' :
                                    hex >= 'a' && hex <= 'f' ? hex - 'a' + 10 : hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                                if (digit < 0) throw Invalid();
                                code = code * 16 + digit;
                            }
                            value.Append((char)code);
                            break;
                        default: throw Invalid();
                    }
                }
                throw Invalid();
            }

            private string Number()
            {
                var start = _offset;
                Take('-');
                if (!Take('0'))
                {
                    if (Peek < '1' || Peek > '9') throw Invalid();
                    while (Digit(Peek)) _offset++;
                }
                if (Take('.'))
                {
                    if (!Digit(Peek)) throw Invalid();
                    while (Digit(Peek)) _offset++;
                }
                if (Take('e') || Take('E'))
                {
                    if (!Take('+')) Take('-');
                    if (!Digit(Peek)) throw Invalid();
                    while (Digit(Peek)) _offset++;
                }
                return _json.Substring(start, _offset - start);
            }
        }

        private static string NormalizeNumber(string number)
        {
            var exponentAt = number.IndexOfAny(new[] { 'e', 'E' });
            var significand = exponentAt < 0 ? number : number.Substring(0, exponentAt);
            var exponent = exponentAt < 0 ? BigInteger.Zero :
                BigInteger.Parse(number.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var negative = significand[0] == '-';
            if (negative) significand = significand.Substring(1);
            var dot = significand.IndexOf('.');
            var fractionalDigits = dot < 0 ? 0 : significand.Length - dot - 1;
            var coefficient = significand.Replace(".", "").TrimStart('0');
            if (coefficient.Length == 0) return "0";
            var trimmed = coefficient.TrimEnd('0');
            exponent += coefficient.Length - trimmed.Length - fractionalDigits;
            return (negative ? "-" : "") + trimmed + "e" + exponent.ToString(CultureInfo.InvariantCulture);
        }

        private static string Quote(string text)
        {
            var output = new StringBuilder("\"");
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                switch (ch)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                            output.Append(ch).Append(text[++i]);
                        else if (ch < 32 || char.IsSurrogate(ch))
                            output.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else output.Append(ch);
                        break;
                }
            }
            return output.Append('"').ToString();
        }
    }
}
