using DeadworksManaged.Api;

namespace AutoMatch;

/// <summary>How much a player wants a hero. Clicking a hero on the board steps through these in order.</summary>
internal enum HeroPick { None, Selected, Priority, HighPriority }

internal static class HeroPicks {
	/// <summary>What another click makes of a hero's pick: up a level, and back to none after high priority.</summary>
	public static HeroPick Next(this HeroPick pick) => pick == HeroPick.HighPriority ? HeroPick.None : pick + 1;

	public static string Describe(this HeroPick pick) => pick switch {
		HeroPick.HighPriority => "high priority",
		HeroPick.Priority => "priority",
		_ => "picked",
	};
}

/// <param name="Picks">The heroes the player picked and how much they want each. A player only gets one of these.</param>
/// <param name="Bot">A bot. It picks nothing and can play any hero the game has bot AI for.</param>
/// <param name="Debt">
/// Matches in a row the player has been given something below their best pick. Ties go their way the more they're owed.
/// </param>
internal sealed record DraftEntry(IReadOnlyDictionary<Heroes, HeroPick> Picks, bool Bot, int Debt);

/// <summary>Hands out the match's heroes from what each player picked.</summary>
internal static class HeroDraft {
	/// <summary>
	/// Give every player a different hero, from their own picks for as many players as the picks allow. Of the ways to do
	/// that it takes the one with the most high priority picks, then the most priority picks. Whatever that leaves open
	/// (who wins a contested hero) is random, weighted toward players who came off worse in earlier matches. Anyone the
	/// picks can't cover gets a random hero nobody else has.
	/// </summary>
	/// <returns>The hero for each player, in the order given, or null when there aren't enough heroes to go round.</returns>
	public static Heroes[]? Assign(IReadOnlyList<DraftEntry> players, IReadOnlyList<HeroInfo> pool, Random random) {
		int n = players.Count, m = pool.Count;
		var result = new Heroes[n];
		if (n == 0) return result;
		if (m < n) return null;

		// Each level outweighs everything below it summed over all the players: one more high priority beats any number
		// of priorities, and so on down. The luck term stays under one step of the smallest level summed over everyone,
		// so it only ever breaks ties.
		double step = n + 1;
		double selected = 1, priority = step, high = step * step;
		// Going without any of your picks outweighs every pick summed over all the players, so the fewest players miss out.
		double unpicked = step * step * step;
		int maxDebt = players.Max(player => player.Debt);
		// Large enough to never be chosen over a real hero, small enough to keep the luck term's precision.
		const double forbidden = 1e9;

		var cost = new double[n, m];
		for (int i = 0; i < n; i++) {
			var player = players[i];
			for (int j = 0; j < m; j++) {
				var hero = pool[j];
				var pick = player.Picks.GetValueOrDefault(hero.Hero);
				if (player.Bot && !hero.BotPlayable) {
					cost[i, j] = forbidden;
					continue;
				}
				double weight = pick switch {
					HeroPick.HighPriority => high,
					HeroPick.Priority => priority,
					HeroPick.Selected => selected,
					_ => 0,
				};
				// What a player is owed counts for more the higher the pick (a flat bonus on every hero they could get
				// would cancel out), and one match owed outweighs any roll of the dice.
				double owed = player.Debt * (int)pick;
				double luck = (owed + random.NextDouble() / step) / ((maxDebt * (int)HeroPick.HighPriority + 1) * step);
				cost[i, j] = (pick == HeroPick.None && !player.Bot ? unpicked : 0) - (weight + luck);
			}
		}

		var columns = MinimumCostAssignment(cost);
		for (int i = 0; i < n; i++) {
			// The cheapest way still gave a bot a hero it can't play: there aren't enough to go round.
			if (cost[i, columns[i]] >= forbidden) return null;
			result[i] = pool[columns[i]].Hero;
		}
		return result;
	}

	/// <summary>
	/// Hungarian algorithm for an n x m cost matrix with n &lt;= m: a different column for every row, with the lowest
	/// total cost. Returns each row's column.
	/// </summary>
	private static int[] MinimumCostAssignment(double[,] cost) {
		int n = cost.GetLength(0), m = cost.GetLength(1);
		var u = new double[n + 1];
		var v = new double[m + 1];
		var owner = new int[m + 1]; // row (1-based) holding each column, 0 for none
		var way = new int[m + 1];

		for (int row = 1; row <= n; row++) {
			owner[0] = row;
			int column = 0;
			var minimum = new double[m + 1];
			Array.Fill(minimum, double.PositiveInfinity);
			var used = new bool[m + 1];
			do {
				used[column] = true;
				int current = owner[column], next = 0;
				double delta = double.PositiveInfinity;
				for (int j = 1; j <= m; j++) {
					if (used[j]) continue;
					double reduced = cost[current - 1, j - 1] - u[current] - v[j];
					if (reduced < minimum[j]) {
						minimum[j] = reduced;
						way[j] = column;
					}
					if (minimum[j] < delta) {
						delta = minimum[j];
						next = j;
					}
				}
				for (int j = 0; j <= m; j++) {
					if (used[j]) {
						u[owner[j]] += delta;
						v[j] -= delta;
					} else {
						minimum[j] -= delta;
					}
				}
				column = next;
			} while (owner[column] != 0);
			do {
				int previous = way[column];
				owner[column] = owner[previous];
				column = previous;
			} while (column != 0);
		}

		var columns = new int[n];
		for (int j = 1; j <= m; j++)
			if (owner[j] != 0) columns[owner[j] - 1] = j - 1;
		return columns;
	}
}
