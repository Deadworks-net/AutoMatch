using DeadworksManaged.Api;

namespace AutoMatch;

/// <summary>A place on a team, owned by one SteamID. Survives the map reload between warmup and the match.</summary>
internal sealed class Seat {
	public required ulong SteamId { get; set; }
	public string Name { get; set; } = "";
	public int Team { get; set; }
	public Heroes Hero { get; set; }
	public LaneColor Lane { get; set; }
	public bool Ready { get; set; }

	/// <summary>
	/// The heroes the player picked on the board, shared with every seat they hold. <see cref="Hero"/> is what they play
	/// in warmup, then what the draft gave them for the match.
	/// </summary>
	public Dictionary<Heroes, HeroPick> Picks { get; set; } = new();

	/// <summary>A fake client. It can't use the board, so it never holds up a ready check.</summary>
	public bool Bot { get; set; }

	/// <summary>The player's slot while connected, -1 otherwise.</summary>
	public int Slot { get; set; } = -1;

	public bool Connected => Slot >= 0;

	/// <summary>When the player last gave any input, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
	public long LastInput { get; private set; } = Environment.TickCount64;

	/// <summary>How many AFK warnings they've had since their last input.</summary>
	public int AfkWarnings { get; set; }

	public void MarkActive() {
		LastInput = Environment.TickCount64;
		AfkWarnings = 0;
	}

	public CCitadelPlayerController? Controller => Connected ? Players.FromSlot(Slot) : null;
}

/// <summary>A hero whose player left mid-match, parked in base until someone takes it over.</summary>
internal sealed record Vacancy(int Slot, ulong SteamId, int Team, Heroes Hero);
