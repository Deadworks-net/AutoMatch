using DeadworksManaged.Api;

namespace AutoMatch;

/// <param name="BotPlayable">
/// The game has bot AI for this hero (bot difficulty Easy or above; 0 is None and -1 is never). A fake client given any
/// other ends up with no hero.
/// </param>
internal sealed record HeroInfo(Heroes Hero, string Name, string Image, bool BotPlayable);

/// <summary>The heroes a player can pick: the ones the game itself offers in matchmaking.</summary>
internal static class HeroPool {
	private static List<HeroInfo>? _all;

	/// <summary>Sorted by display name. Read from hero VData, so only valid once a map has loaded.</summary>
	public static IReadOnlyList<HeroInfo> All => _all ??= Load();

	/// <summary>Drop the cached list; VData is reloaded with the map.</summary>
	public static void Reset() => _all = null;

	private static List<HeroInfo> Load() => Enum.GetValues<Heroes>()
		.Select(hero => (hero, data: hero.GetHeroData()))
		.Where(entry => entry.data is { IsValid: true, AvailableInGame: true })
		.Select(entry => new HeroInfo(entry.hero, entry.hero.ToDisplayName(), entry.data!.IconImageSmall ?? "",
			entry.data.AllyBotDifficulty > 0 && entry.data.EnemyBotDifficulty > 0))
		.OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
		.ToList();

	public static bool IsPickable(Heroes hero) => All.Any(info => info.Hero == hero);

	public static bool IsBotPlayable(Heroes hero) => All.Any(info => info.Hero == hero && info.BotPlayable);

	public static string NameOf(Heroes hero) => hero.ToDisplayName();

	/// <summary>Matches a typed name against display names and internal names, exact first, then by prefix.</summary>
	public static HeroInfo? Find(string text) {
		text = text.Trim();
		if (text.Length == 0) return null;
		return All.FirstOrDefault(info => Matches(info, text, exact: true))
			?? All.FirstOrDefault(info => Matches(info, text, exact: false));
	}

	private static bool Matches(HeroInfo info, string text, bool exact) {
		foreach (var candidate in new[] { info.Name, info.Name.Replace(" ", ""), info.Hero.ToString() }) {
			if (exact ? candidate.Equals(text, StringComparison.OrdinalIgnoreCase)
			          : candidate.StartsWith(text, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	/// <summary>
	/// A random pickable hero outside <paramref name="taken"/>, or any pickable hero when all are taken. Bots only get
	/// heroes the game has bot AI for.
	/// </summary>
	public static Heroes Random(IEnumerable<Heroes> taken, bool forBot = false) {
		var excluded = taken.ToHashSet();
		var candidates = All.Where(info => !forBot || info.BotPlayable).ToList();
		var free = candidates.Where(info => !excluded.Contains(info.Hero)).Select(info => info.Hero).ToList();
		var pool = free.Count > 0 ? free
			: candidates.Count > 0 ? candidates.Select(info => info.Hero).ToList()
			: Enum.GetValues<Heroes>().ToList();
		return pool[System.Random.Shared.Next(pool.Count)];
	}
}
