using System;
using System.Diagnostics;

namespace XtermSharp {
	// MIGUEL TODO:
	// The original code used Rune + Code, but it really makes no sense to keep those separate, excpt for null that has a
	// zero-width thing for code 0.
	[DebuggerDisplay("[CharData (Attr={Attribute},Rune={Rune},W={Width},Code={Code})]")]
	public struct CharData {
		public CellAttribute Attribute;
		public Rune Rune;
		public int Width;
		public int Code;

		/// <summary>
		/// The combining marks that follow <see cref="Code"/> in this cell, or null when the cell
		/// holds a single codepoint. A cell is a grapheme cluster, and a mark has no column of its
		/// own, so it has nowhere else to live.
		/// </summary>
		public string Combining;

		public static readonly CellAttribute DefaultAttr = CellAttribute.Default;
		public static readonly CellAttribute InvertedAttr = CellAttribute.InvertedDefault;

		public static CharData Null = new CharData (DefaultAttr, '\u0200', 1, 0);
		public static CharData WhiteSpace = new CharData (DefaultAttr, ' ', 1, 32);
		public static CharData LeftBrace = new CharData (DefaultAttr, '{', 1, 123);
		public static CharData RightBrace = new CharData (DefaultAttr, '}', 1, 125);
		public static CharData LeftBracket = new CharData (DefaultAttr, '[', 1, 91);
		public static CharData RightBracket = new CharData (DefaultAttr, ']', 1, 93);
		public static CharData LeftParenthesis = new CharData (DefaultAttr, '(', 1, 40);
		public static CharData RightParenthesis = new CharData (DefaultAttr, ')', 1, 41);
		public static CharData Period = new CharData (DefaultAttr, '.', 1, 46);

		public CharData (CellAttribute attribute, Rune rune, int width, int code)
		{
			Attribute = attribute;
			Rune = rune;
			Width = width;
			Code = code;
			Combining = null;
		}

		// Returns an empty CharData with the specified attribute
		public CharData (CellAttribute attribute)
		{
			Attribute = attribute;
			Rune = '\u0200';
			Width = 1;
			Code = 0;
			Combining = null;
		}

		/// <summary>
		/// Returns true if this CharData matches the given Rune, irrespective of character attributes
		/// </summary>
		public bool MatchesRune(Rune rune)
		{
			return rune == Rune;
		}

		/// <summary>
		/// Returns true if this CharData matches the given Rune, irrespective of character attributes
		/// </summary>
		public bool MatchesRune (CharData chr)
		{
			return Rune == chr.Rune;
		}

		/// <summary>
		/// returns true if this CharData matches Null or has a code of 0
		/// </summary>
		public bool IsNullChar()
		{
			return Rune == Null.Rune || Code == 0;
		}
	}
}
