using System;

namespace XtermSharp {
	// TODO: rename to CharacterAttributes or similar
	[Flags]
	public enum FLAGS {
		BOLD = 1,
		UNDERLINE = 2,
		BLINK = 4,
		INVERSE = 8,
		INVISIBLE = 16,
		DIM = 32,
		ITALIC = 64,
		CrossedOut = 128
	}

	public static class CharacterAttribute {
		public static string ToSGR (CellAttribute attribute)
		{
			var result = "0";

			var ca = attribute.Flags;
			if (ca.HasFlag (FLAGS.BOLD)) {
				result += ";1";
			}
			if (ca.HasFlag (FLAGS.UNDERLINE)) {
				result += ";4";
			}
			if (ca.HasFlag (FLAGS.BLINK)) {
				result += ";5";
			}
			if (ca.HasFlag (FLAGS.INVERSE)) {
				result += ";7";
			}
			if (ca.HasFlag (FLAGS.INVISIBLE)) {
				result += ";8";
			}

			result += ColorToSGR (attribute.Foreground, extended: 38, basic: 3, bright: 9);
			result += ColorToSGR (attribute.Background, extended: 48, basic: 4, bright: 10);

			result += "m";
			return result;
		}

		static string ColorToSGR (CellColor color, int extended, int basic, int bright)
		{
			switch (color.Kind) {
			case CellColorKind.Indexed:
				if (color.Index > 16)
					return $";{extended};5;{color.Index}";
				if (color.Index >= 8)
					return $";{bright}{color.Index - 8};";
				return $";{basic}{color.Index};";
			case CellColorKind.Rgb:
				return $";{extended};2;{color.Red};{color.Green};{color.Blue}";
			case CellColorKind.Default:
			case CellColorKind.InvertedDefault:
				return string.Empty;
			}

			throw new ArgumentOutOfRangeException (nameof (color), color.Kind, "Unknown cell colour kind.");
		}
	}
}
