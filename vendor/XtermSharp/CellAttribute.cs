using System;

namespace XtermSharp {
	/// <summary>
	/// Which colour space a <see cref="CellColor"/> is expressed in.
	/// </summary>
	public enum CellColorKind : byte {
		Default = 0,
		InvertedDefault = 1,
		Indexed = 2,
		Rgb = 3
	}

	/// <summary>
	/// One foreground or background colour, as the program asked for it.
	/// </summary>
	public readonly struct CellColor : IEquatable<CellColor> {
		CellColor (CellColorKind kind, byte red, byte green, byte blue)
		{
			Kind = kind;
			Red = red;
			Green = green;
			Blue = blue;
		}

		public CellColorKind Kind { get; }

		public byte Red { get; }

		public byte Green { get; }

		public byte Blue { get; }

		/// <summary>
		/// The palette entry, meaningful only when <see cref="Kind"/> is <see cref="CellColorKind.Indexed"/>.
		/// </summary>
		public byte Index => Red;

		public static CellColor Default { get; } = new CellColor (CellColorKind.Default, 0, 0, 0);

		public static CellColor InvertedDefault { get; } = new CellColor (CellColorKind.InvertedDefault, 0, 0, 0);

		public static CellColor Indexed (byte index) => new CellColor (CellColorKind.Indexed, index, 0, 0);

		public static CellColor Rgb (byte red, byte green, byte blue) => new CellColor (CellColorKind.Rgb, red, green, blue);

		public bool Equals (CellColor other) =>
			Kind == other.Kind && Red == other.Red && Green == other.Green && Blue == other.Blue;

		public override bool Equals (object obj) => obj is CellColor other && Equals (other);

		public override int GetHashCode () => ((int)Kind << 24) | (Red << 16) | (Green << 8) | Blue;

		public static bool operator == (CellColor left, CellColor right) => left.Equals (right);

		public static bool operator != (CellColor left, CellColor right) => !left.Equals (right);

		public override string ToString ()
		{
			switch (Kind) {
			case CellColorKind.Default:
				return "default";
			case CellColorKind.InvertedDefault:
				return "inverted-default";
			case CellColorKind.Indexed:
				return "@" + Index;
			case CellColorKind.Rgb:
				return string.Format ("#{0:x2}{1:x2}{2:x2}", Red, Green, Blue);
			}

			throw new InvalidOperationException (string.Format ("Unknown cell colour kind {0}.", Kind));
		}
	}

	/// <summary>
	/// The whole appearance of a cell: the style flags plus a foreground and a background that each
	/// carry their own colour space, so a 24-bit colour survives to the renderer instead of being
	/// resolved to a palette slot while it is being parsed.
	/// </summary>
	public readonly struct CellAttribute : IEquatable<CellAttribute> {
		public CellAttribute (FLAGS flags, CellColor foreground, CellColor background)
		{
			Flags = flags;
			Foreground = foreground;
			Background = background;
		}

		public FLAGS Flags { get; }

		public CellColor Foreground { get; }

		public CellColor Background { get; }

		public static CellAttribute Default { get; } =
			new CellAttribute (default (FLAGS), CellColor.Default, CellColor.Default);

		public static CellAttribute InvertedDefault { get; } =
			new CellAttribute (default (FLAGS), CellColor.InvertedDefault, CellColor.InvertedDefault);

		public CellAttribute WithBackground (CellColor background) => new CellAttribute (Flags, Foreground, background);

		public bool Equals (CellAttribute other) =>
			Flags == other.Flags && Foreground.Equals (other.Foreground) && Background.Equals (other.Background);

		public override bool Equals (object obj) => obj is CellAttribute other && Equals (other);

		public override int GetHashCode ()
		{
			var hash = (int)Flags;
			hash = (hash * 397) ^ Foreground.GetHashCode ();
			hash = (hash * 397) ^ Background.GetHashCode ();
			return hash;
		}

		public static bool operator == (CellAttribute left, CellAttribute right) => left.Equals (right);

		public static bool operator != (CellAttribute left, CellAttribute right) => !left.Equals (right);

		public override string ToString () => string.Format ("{0} on {1} {2}", Foreground, Background, Flags);
	}
}
