using DeadworksManaged.Api;

namespace AutoMatch;

public class AutoMatchConfig : IConfig {
	/// <summary>
	/// The map warmup and matches are played on, e.g. dl_harbor. Empty keeps whatever map the server is on. Set
	/// <see cref="Lanes"/> to match it.
	/// </summary>
	public string Map { get; set; } = "";

	/// <summary>
	/// SteamID64s of the server's owners, e.g. 76561197960287930. Owners open the admin panel with !admin, where they
	/// make other players admins and hold slots back for them.
	/// </summary>
	public List<ulong> Owners { get; set; } = [];

	/// <summary>
	/// A Discord invite the hero picker asks players to join to give feedback, beside the Deadworks Discord. Empty
	/// shows only the Deadworks Discord.
	/// </summary>
	public string FeedbackDiscord { get; set; } = "";

	/// <summary>What to call <see cref="FeedbackDiscord"/>, e.g. "dl_harbor" for "the dl_harbor Discord".</summary>
	public string FeedbackDiscordName { get; set; } = "";

	/// <summary>Players it takes to start a match, split evenly between the teams.</summary>
	public int PlayersToStart { get; set; } = 8;

	/// <summary>Fewest players that can start a match early by all readying up.</summary>
	public int MinPlayersToReadyUp { get; set; } = 2;

	/// <summary>
	/// Seconds warmup lasts at least before a full server starts the match, so there is a chance to change sides and
	/// heroes, after a match as much as before the first. Everyone readying up starts it sooner.
	/// </summary>
	public int MinWarmupSeconds { get; set; } = 60;

	/// <summary>
	/// Heroes each player has to pick before they can ready up or a match can start. The more everyone picks, the more
	/// likely each of them gets a different hero from their own picks rather than a random one.
	/// </summary>
	public int MinHeroPicks { get; set; } = 3;

	/// <summary>
	/// Seconds a joining player's game gets to show it has the Deadworks client bootstrap, which the Deadworks launcher
	/// installs; without it they can't see the draft board. Until it does they take no part in the game. Past this they
	/// get three warnings to connect through the launcher, then a kick. 0 turns the check off.
	/// </summary>
	public int BootstrapCheckSeconds { get; set; } = 20;

	/// <summary>Seconds of "match found" countdown before the map reloads for the match.</summary>
	public int MatchFoundSeconds { get; set; } = 5;

	/// <summary>Seconds the match waits for everyone to load back in before starting without whoever is missing.</summary>
	public int LoadInSeconds { get; set; } = 90;

	/// <summary>Seconds in base, heroes still changeable, before the zipline launch.</summary>
	public int PreGameSeconds { get; set; } = 30;

	/// <summary>Seconds after the Patron falls before the server goes back to warmup.</summary>
	public int PostGameSeconds { get; set; } = 15;

	/// <summary>Seconds an empty server waits mid-match before going back to warmup.</summary>
	public int EmptyServerSeconds { get; set; } = 60;

	/// <summary>Souls every hero gets during warmup; enough reaches max level.</summary>
	public int WarmupSouls { get; set; } = 60000;

	/// <summary>Respawn time during warmup.</summary>
	public int WarmupRespawnSeconds { get; set; } = 3;

	/// <summary>Seconds someone joining a match in progress gets to pick before a hero is picked for them.</summary>
	public int LateJoinPickSeconds { get; set; } = 20;

	/// <summary>
	/// Seconds a player with a place can go without any input before they're kicked, warned a minute and half a minute
	/// before. Mid-match their hero is parked for whoever is next in line, as for anyone who leaves. 0 turns it off.
	/// </summary>
	public int AfkKickSeconds { get; set; } = 180;

	/// <summary>
	/// Lanes a team is spread across, in the order they fill, by the game's lane colour ids: 1 Yellow, 3 Green, 4 Blue,
	/// 6 Purple. Only list lanes the map has: dl_midtown has Yellow, Blue and Purple, dl_harbor Yellow and Blue. Four
	/// players on three lanes put two in the first lane listed.
	/// </summary>
	public List<LaneColor> Lanes { get; set; } = [LaneColor.Blue, LaneColor.Yellow, LaneColor.Purple];

	public void Validate() {
		Map = Map.Trim();
		Owners = Owners.Where(owner => owner != 0).Distinct().ToList();
		FeedbackDiscord = FeedbackDiscord.Trim();
		FeedbackDiscordName = FeedbackDiscordName.Trim();
		PlayersToStart = Math.Clamp(PlayersToStart, 2, 24) & ~1;
		MinPlayersToReadyUp = Math.Clamp(MinPlayersToReadyUp, 2, PlayersToStart);
		MinWarmupSeconds = Math.Max(0, MinWarmupSeconds);
		MinHeroPicks = Math.Clamp(MinHeroPicks, 1, 10);
		BootstrapCheckSeconds = BootstrapCheckSeconds <= 0 ? 0 : Math.Max(10, BootstrapCheckSeconds);
		MatchFoundSeconds = Math.Max(0, MatchFoundSeconds);
		LoadInSeconds = Math.Max(10, LoadInSeconds);
		PreGameSeconds = Math.Max(5, PreGameSeconds);
		PostGameSeconds = Math.Max(3, PostGameSeconds);
		EmptyServerSeconds = Math.Max(5, EmptyServerSeconds);
		WarmupSouls = Math.Max(0, WarmupSouls);
		WarmupRespawnSeconds = Math.Max(0, WarmupRespawnSeconds);
		LateJoinPickSeconds = Math.Max(5, LateJoinPickSeconds);
		AfkKickSeconds = AfkKickSeconds <= 0 ? 0 : Math.Max(60, AfkKickSeconds);
		Lanes = Lanes.Where(lane => lane != LaneColor.Invalid).ToList();
		if (Lanes.Count == 0)
			Lanes = [LaneColor.Blue, LaneColor.Yellow, LaneColor.Purple];
	}
}
