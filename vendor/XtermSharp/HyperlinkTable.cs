using System;
using System.Collections.Generic;

namespace XtermSharp {
	/// <summary>
	/// PATCH 21: the urls that OSC 8 hyperlinks in the grid point at, addressed by the int a
	/// <see cref="CharData.Hyperlink"/> carries.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Ids are minted monotonically and <b>never reused</b>. That is the whole safety property of
	/// this type: a cell holds an id long after the entry behind it may have gone, and an id that
	/// could come back meaning a different url would turn a stale cell into a link to somewhere the
	/// program never named. Everything else here is bookkeeping around keeping that true.
	/// </para>
	/// <para>
	/// Because of it, eviction is safe. A full table drops its oldest entry — the one deepest in the
	/// scrollback — and any cell still pointing at it stops resolving and becomes ordinary text.
	/// That is the correct failure: a link that has aged out is inert, never wrong. Note this is the
	/// opposite of what VTE does; its pool recycles indices after a mark-and-sweep proves no live
	/// cell references them, and recycling without that proof is exactly the bug above. Monotonic
	/// ids buy the same safety with none of the sweep.
	/// </para>
	/// </remarks>
	public sealed class HyperlinkTable {
		/// <summary>
		/// How many distinct links are remembered at once. Reaching it means a program has opened
		/// 65,536 different urls in one session; VTE caps at 2^20 and calls that unreachable.
		/// </summary>
		public const int MaxEntries = 65536;

		/// <summary>
		/// The longest url accepted. 2083 is what VTE and iTerm2 both use — there is no de jure
		/// limit and this is the de facto one. An overlong url is dropped, not truncated: half a
		/// url is a link to somewhere else.
		/// </summary>
		/// <remarks>
		/// Load-bearing here in a way it is not in xterm.js, whose parser refuses to dispatch an
		/// over-long OSC at all. This one truncates the payload and dispatches anyway
		/// (<c>EscapeSequenceParser.MaxOscBytes</c>, patch 20), so a truncated url would arrive
		/// looking well-formed.
		/// </remarks>
		public const int MaxUriLength = 2083;

		/// <summary>
		/// The longest <c>id=</c> parameter accepted, as VTE. An overlong id is ignored and the
		/// link falls back to a fresh anonymous one, rather than the link being dropped: the id is
		/// an optional grouping hint, and losing it costs only the grouping. Without a cap the
		/// intern key is unbounded and one link could pin megabytes.
		/// </summary>
		public const int MaxIdLength = 250;

		struct Entry {
			public string Uri;

			/// <summary>The intern key this entry is reachable by, or null when it is anonymous.</summary>
			public string Key;
		}

		readonly Dictionary<string, int> interned = new Dictionary<string, int> (StringComparer.Ordinal);
		readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry> ();
		readonly Queue<int> order = new Queue<int> ();

		int next = 1;

		/// <summary>How many links are currently resolvable.</summary>
		public int Count => entries.Count;

		/// <summary>
		/// The id for a link to <paramref name="uri"/>, minting one if needed, or 0 when the link
		/// cannot be represented — an empty or overlong url, or an exhausted id space.
		/// </summary>
		/// <param name="parameters">
		/// The OSC 8 parameter field, colon-separated. Only <c>id=</c> is read; anything else is
		/// ignored, as both xterm.js and VTE ignore it.
		/// </param>
		public int Open (string parameters, string uri)
		{
			if (string.IsNullOrEmpty (uri) || uri.Length > MaxUriLength)
				return 0;

			// An id that would overflow the counter is refused rather than wrapped. Reaching this
			// needs two billion OSC 8 opens in one session; wrapping would break the one invariant
			// this type has.
			if (next == int.MaxValue)
				return 0;

			var id = IdOf (parameters);
			string key = null;

			if (id != null) {
				key = id + ";" + uri;

				// A hit on an entry that has since been evicted mints a fresh one instead of
				// resurrecting a dead id, so the two dictionaries cannot disagree.
				if (interned.TryGetValue (key, out var existing) && entries.ContainsKey (existing))
					return existing;
			}

			var minted = next++;
			entries [minted] = new Entry { Uri = uri, Key = key };
			order.Enqueue (minted);

			if (key != null)
				interned [key] = minted;

			while (entries.Count > MaxEntries)
				EvictOldest ();

			return minted;
		}

		/// <summary>The url behind an id, or false when there is none or it has aged out.</summary>
		public bool TryGetUri (int id, out string uri)
		{
			if (id != 0 && entries.TryGetValue (id, out var entry)) {
				uri = entry.Uri;
				return true;
			}

			uri = null;
			return false;
		}

		/// <summary>
		/// Forgets every link. The id counter is not rewound, so ids minted before a reset stay
		/// distinct from the ones minted after it.
		/// </summary>
		public void Clear ()
		{
			interned.Clear ();
			entries.Clear ();
			order.Clear ();
		}

		void EvictOldest ()
		{
			while (order.Count > 0) {
				var oldest = order.Dequeue ();
				if (!entries.TryGetValue (oldest, out var entry))
					continue;

				entries.Remove (oldest);

				// Only when it still points here: a later Open on the same key has already
				// overwritten it, and that newer entry must not be unregistered by this eviction.
				if (entry.Key != null && interned.TryGetValue (entry.Key, out var current) && current == oldest)
					interned.Remove (entry.Key);

				return;
			}
		}

		/// <summary>
		/// The <c>id=</c> value in a colon-separated parameter field, or null when there is none.
		/// </summary>
		/// <remarks>
		/// An empty value counts as none: the specification says a cell with an empty id and a cell
		/// with no id are interchangeable, and interning on the empty string would make every
		/// <c>id=</c>-less link to one url a single link across the whole session.
		/// </remarks>
		static string IdOf (string parameters)
		{
			if (string.IsNullOrEmpty (parameters))
				return null;

			foreach (var parameter in parameters.Split (':')) {
				if (!parameter.StartsWith ("id=", StringComparison.Ordinal))
					continue;

				var id = parameter.Substring (3);
				if (id.Length == 0 || id.Length > MaxIdLength)
					return null;

				return id;
			}

			return null;
		}
	}
}
