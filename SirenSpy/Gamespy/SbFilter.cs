using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SirenSpy.Gamespy
{
	// Evaluates server browser filters (SQL-like, e.g. "gamemode = 12 and numplayers < maxplayers and hostname like '%foo%'")
	// against a server's QR2 keys. Unknown keys read as "". Anything we can't parse matches every server.
	public static class SbFilter
	{
		public static bool Matches(string? filter, Func<string, string> key)
		{
			if (string.IsNullOrWhiteSpace(filter)) return true;
			try
			{
				var p = new Parser(Tokenize(filter), key);
				var result = p.ParseOr();
				return Truthy(result);
			}
			catch (Exception ex)
			{
				Siren.Log($"[SB] Could not evaluate filter \"{filter}\" ({ex.Message}), returning all servers", ConsoleColor.Yellow);
				return true;
			}
		}

		private record Token(string Kind, string Text); // ident, num, str, op, lparen, rparen

		private static List<Token> Tokenize(string s)
		{
			var tokens = new List<Token>();
			int i = 0;
			while (i < s.Length)
			{
				char c = s[i];
				if (char.IsWhiteSpace(c)) { i++; continue; }
				if (c == '(') { tokens.Add(new("lparen", "(")); i++; continue; }
				if (c == ')') { tokens.Add(new("rparen", ")")); i++; continue; }
				if (c == '\'' || c == '"')
				{
					int end = s.IndexOf(c, i + 1);
					if (end < 0) end = s.Length;
					tokens.Add(new("str", s[(i + 1)..end]));
					i = end + 1;
					continue;
				}
				if ("=<>!".Contains(c))
				{
					int j = i + 1;
					while (j < s.Length && "=<>".Contains(s[j])) j++;
					tokens.Add(new("op", s[i..j]));
					i = j;
					continue;
				}
				if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
				{
					int j = i + 1;
					while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
					tokens.Add(new("num", s[i..j]));
					i = j;
					continue;
				}
				{
					int j = i;
					while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '.')) j++;
					if (j == i) throw new FormatException($"unexpected '{c}'");
					var word = s[i..j];
					var lower = word.ToLowerInvariant();
					tokens.Add(lower is "and" or "or" or "not" or "like" ? new("op", lower) : new("ident", word));
					i = j;
				}
			}
			return tokens;
		}

		private static bool Truthy(object v) => v switch
		{
			bool b => b,
			double d => d != 0,
			string s => s.Length > 0 && s != "0",
			_ => false,
		};

		private class Parser(List<Token> tokens, Func<string, string> key)
		{
			private int _pos;
			private Token? Peek => _pos < tokens.Count ? tokens[_pos] : null;
			private bool IsOp(string op) => Peek is { Kind: "op" } t && t.Text == op;

			public object ParseOr()
			{
				var left = ParseAnd();
				while (IsOp("or")) { _pos++; var right = ParseAnd(); left = Truthy(left) || Truthy(right); }
				return left;
			}

			private object ParseAnd()
			{
				var left = ParseNot();
				while (IsOp("and")) { _pos++; var right = ParseNot(); left = Truthy(left) && Truthy(right); }
				return left;
			}

			private object ParseNot()
			{
				if (IsOp("not")) { _pos++; return !Truthy(ParseNot()); }
				return ParseComparison();
			}

			private object ParseComparison()
			{
				var left = ParsePrimary();
				bool negate = false;
				if (IsOp("not") && _pos + 1 < tokens.Count && tokens[_pos + 1].Text == "like") { _pos++; negate = true; }
				if (Peek is { Kind: "op" } op && op.Text is not ("and" or "or" or "not"))
				{
					_pos++;
					var right = ParsePrimary();
					bool r = Compare(left, op.Text, right);
					return negate ? !r : r;
				}
				return left;
			}

			private object ParsePrimary()
			{
				var t = Peek ?? throw new FormatException("unexpected end");
				_pos++;
				switch (t.Kind)
				{
					case "lparen":
						var v = ParseOr();
						if (Peek?.Kind == "rparen") _pos++;
						return v;
					case "num": return double.Parse(t.Text, CultureInfo.InvariantCulture);
					case "str": return t.Text;
					case "ident": return key(t.Text);
					default: throw new FormatException($"unexpected '{t.Text}'");
				}
			}

			private static bool Compare(object a, string op, object b)
			{
				if (op == "like")
				{
					var pattern = "^" + Regex.Escape(Convert.ToString(b, CultureInfo.InvariantCulture) ?? "")
						.Replace("%", ".*").Replace("_", ".") + "$";
					return Regex.IsMatch(Convert.ToString(a, CultureInfo.InvariantCulture) ?? "", pattern, RegexOptions.IgnoreCase);
				}

				int cmp;
				if (TryNum(a, out var x) && TryNum(b, out var y)) cmp = x.CompareTo(y);
				else cmp = string.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

				return op switch
				{
					"=" or "==" => cmp == 0,
					"!=" or "<>" => cmp != 0,
					"<" => cmp < 0,
					">" => cmp > 0,
					"<=" => cmp <= 0,
					">=" => cmp >= 0,
					_ => throw new FormatException($"unknown operator {op}"),
				};
			}

			private static bool TryNum(object v, out double d)
			{
				if (v is double dd) { d = dd; return true; }
				if (v is bool b) { d = b ? 1 : 0; return true; }
				return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
			}
		}
	}
}
