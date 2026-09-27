using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;
using DeadworksManaged.Api.Utils;

namespace AutoMatch;

/// <summary>
/// Plays a Deadlock match the way matchmaking does, on a direct-connect server.
///
/// Warmup runs while the server fills: a sandbox where everyone picks a side and a hero on a draft board. Once enough
/// players are in and warmup has run its minimum (or everyone present readies up) the map reloads into the match
/// proper, which the engine runs as it would for a matchmade game: team intro, a pregame countdown in base where heroes
/// can still change, then the zipline launch. When a Patron falls the winner is announced and the server returns to
/// warmup.
///
/// Places on the teams (seats) belong to players, not to whoever loads in first: across every map reload a seat is
/// held for its owner until the load-in window ends. Everyone else waits in one queue, in order, and takes open spots
/// as they come up: a free place in warmup, or mid-match the hero a leaver left parked in base.
/// </summary>
public class AutoMatchPlugin : DeadworksPluginBase {
	public override string Name => "AutoMatch";

	[PluginConfig]
	public AutoMatchConfig Config { get; set; } = new();

	private const int Amber = 2;
	private const int Sapphire = 3;
	private const int Spectators = 1;

	// The team spectators sit on. The HUD's chat box keys off the local player's team: on Spectators it only offers
	// party chat, which never reaches the server.
	private int _spectatorTeam = Spectators;

	private enum Phase { Warmup, Match }

	private Phase _phase = Phase.Warmup;

	// Everyone with a place on a team, by player key. Survives map reloads.
	private readonly Dictionary<ulong, Seat> _seats = new();
	// Everyone without a seat who wants one, longest waiting first. Survives map reloads.
	private readonly List<ulong> _queue = new();
	// Fake clients by player key. A map reload drops them for good, so they are brought back by name.
	private readonly Dictionary<ulong, string> _botNames = new();
	// Each player's last side and hero, so a new seat puts them back where they were.
	private readonly Dictionary<ulong, (int Team, Heroes Hero)> _lastPicks = new();
	// The heroes each player picked on the board, and how much they want each. Survives map reloads.
	private readonly Dictionary<ulong, Dictionary<Heroes, HeroPick>> _picks = new();
	// Each player's heroes in the order they last clicked them, most recent last.
	private readonly Dictionary<ulong, List<Heroes>> _clickOrder = new();
	// Matches in a row each player was drafted below their best pick; ties go their way next time.
	private readonly Dictionary<ulong, int> _draftDebt = new();
	// What the draft gave each player, told to them once they've loaded into the match.
	private readonly Dictionary<ulong, string> _draftNotes = new();
	// Players who have voted to forfeit this match.
	private readonly HashSet<ulong> _forfeitVotes = new();
	// Admin panels whose restart button has had its first click, by slot, with when (Environment.TickCount64).
	private readonly Dictionary<int, long> _restartArmed = new();
	// Heroes parked by their leavers, keyed by the slot of the controller holding them.
	private readonly Dictionary<int, Vacancy> _vacancies = new();
	// Stand-in bots holding a kicked bot's parked hero, by slot, with the name to kick them by once it's taken.
	private readonly Dictionary<int, string> _standIns = new();
	// Mid-match joiners choosing a hero, with the timer that picks for them.
	private readonly Dictionary<int, IHandle> _latePicks = new();
	// Mid-match joiners owed souls to match their team once their hero is up.
	private readonly HashSet<ulong> _catchUp = new();
	// The currencies each hero pawn on this map has had its starting amount of.
	private readonly HashSet<(uint Pawn, ECurrencyType Type)> _startingGrants = new();
	private readonly HashSet<int> _hud = new();
	private readonly DraftBoard _board = new();
	private AdminStore _admins = null!;
	private readonly AdminPanel _adminPanel = new();
	// Players reloading into the map being loaded, whose reconnect is the same session rather than a fresh join.
	private HashSet<ulong> _carried = new();
	// Players in by slot whose game hasn't yet shown it has the client bootstrap. They take no part until it does.
	private readonly Dictionary<int, ulong> _awaitingBootstrap = new();
	private readonly HashSet<string> _changedConVars = new();
	private Dictionary<string, string>? _conVarDefaults;

	private IHandle? _matchFound;
	private int _matchFoundLeft;
	// Warmup still owed before a full server may start the match.
	private int _warmupLeft;
	private IHandle? _loadIn;
	private IHandle? _emptyReset;
	private bool _changingLevel;

	private int TeamSize => Config.PlayersToStart / 2;

	private bool MatchLive => _phase == Phase.Match && GameRules.GameState >= EGameState.GameInProgress;

	// ─── Lifecycle ─────────────────────────────────────────────────────────

	public override void OnLoad(bool isReload) {
		var panel = UI.Panel(DraftBoard.PanelId);
		panel.On(DraftBoard.EvHero, e => {
			MarkActive(e.Caller.Slot);
			if (int.TryParse(e.ArgAt(0), out var id)) PickHero(e.Caller, (Heroes)id);
		});
		panel.On(DraftBoard.EvSide, e => {
			MarkActive(e.Caller.Slot);
			if (int.TryParse(e.ArgAt(0), out var team)) ChangeSide(e.Caller, team);
		});
		panel.On(DraftBoard.EvReady, e => {
			MarkActive(e.Caller.Slot);
			ToggleReady(e.Caller);
		});
		panel.On(DraftBoard.EvClose, e => {
			MarkActive(e.Caller.Slot);
			CloseBoard(e.Caller);
		});

		_admins = new AdminStore(this.GetConfigPath() is { } configPath
			? Path.Combine(Path.GetDirectoryName(configPath)!, "admins.json")
			: null, () => Config.Owners);
		var admin = UI.Panel(AdminPanel.PanelId);
		admin.On(AdminPanel.EvSpectate, e => {
			if (IsAdmin(e.Caller)) SetForcedSpectator(e.Caller, e.ArgAt(0) == "1");
		});
		admin.On(AdminPanel.EvSlots, e => {
			if (!IsOwner(e.Caller) || !int.TryParse(e.ArgAt(0), out var delta)) return;
			_admins.SetReservedSlots(_admins.ReservedSlots + delta);
			RefreshAdminPanels();
		});
		admin.On(AdminPanel.EvPromote, e => {
			if (IsOwner(e.Caller) && ulong.TryParse(e.ArgAt(0), out var steamId)) Promote(steamId);
		});
		admin.On(AdminPanel.EvDemote, e => {
			if (IsOwner(e.Caller) && ulong.TryParse(e.ArgAt(0), out var steamId)) Demote(steamId);
		});
		admin.On(AdminPanel.EvClose, e => _adminPanel.Close(e.Caller.Slot));
		admin.On(AdminPanel.EvRestart, e => {
			if (IsAdmin(e.Caller)) RestartFromPanel(e.Caller);
		});

		// The game only gives dl_midtown its matchmade start: a countdown in base, then the zipline launch.
		GameRules.MatchStartOnAnyMap = true;

		// When the match intro starts, a client can send a message over the server's default 32 KB limit, and the server
		// drops anyone who does. Raise the limit, never lower it.
		const string recvLimit = "net_limit_sv_recv_max_message_size_kb";
		if (ConVar.Find(recvLimit) is { } limit && limit.GetInt() < 256)
			SetConVar(recvLimit, "256");

		Console.WriteLine($"[{Name}] loaded (reload={isReload}), {Config.PlayersToStart} players to start, " +
			$"{_admins.ReservedSlots} of {GlobalVars.MaxClients} slots reserved for admins");
		if (Config.Owners.Count == 0)
			Console.WriteLine($"[{Name}] no Owners in the config, so nobody can open the admin panel: add your SteamID64");

		// The roster went with the old instance, and the map is still running under settings this one never applied.
		// Start over from warmup, where everyone present is seated again as they load back in.
		if (isReload && Server.MapName.Length > 0) ReturnToWarmup();
	}

	public override void OnUnload() {
		ClosePanels();
		GameRules.SetWaitingForPlayersRoster(0, 0);
		GameRules.MatchStartOnAnyMap = false;
		foreach (var name in _changedConVars)
			RestoreConVar(name);
	}

	public override void OnStartupServer() {
		_changingLevel = false;
		_vacancies.Clear();
		_standIns.Clear();
		_awaitingBootstrap.Clear();
		_forfeitVotes.Clear();
		_restartArmed.Clear();
		_latePicks.Clear();
		// Places given up for good: a bot that was kicked, an admin who stepped out to watch.
		foreach (var gone in _seats.Values
			         .Where(seat => seat.Bot ? !_botNames.ContainsKey(seat.SteamId) : IsForcedSpectator(seat.SteamId))
			         .ToList())
			_seats.Remove(gone.SteamId);
		_catchUp.Clear();
		_startingGrants.Clear();
		// A panel still up from the last map would come back once each player's game rebuilds its UI after the load,
		// with nothing on this map aware of it to take it down. That covers one opened after ChangeLevel closed the rest,
		// and a map change the plugin didn't start.
		ClosePanels();
		_matchFound = _loadIn = _emptyReset = null;
		HeroPool.Reset();
		foreach (var seat in _seats.Values) {
			seat.Slot = -1;
			seat.Ready = false;
		}

		Timer.Every(1.Seconds(), Tick).CancelOnMapChange();
		Timer.Once(1.Seconds(), RestoreBots).CancelOnMapChange();

		if (_phase == Phase.Warmup) BeginWarmup();
		else BeginMatch();

		// Seats are held for their owners while they load back in, and no longer.
		_loadIn = Timer.Once(Config.LoadInSeconds.Seconds(), EndLoadIn).CancelOnMapChange();

		// A server started on another map moves to the configured one.
		if (Config.Map.Length > 0 && !Server.MapName.Equals(Config.Map, StringComparison.OrdinalIgnoreCase)) {
			Console.WriteLine($"[{Name}] on {Server.MapName}, moving to {Config.Map}");
			Timer.Once(1.Seconds(), ChangeLevel).CancelOnMapChange();
		}
	}

	private void BeginWarmup() {
		SetConVar("citadel_match_intro_force_enabled", "0");
		SetConVar("citadel_trooper_spawn_enabled", "0");
		SetConVar("citadel_npc_spawn_enabled", "0");
		SetConVar("citadel_player_spawn_time_max_respawn_time", Config.WarmupRespawnSeconds.ToString());
		SetConVar("citadel_allow_purchasing_anywhere", "1");
		GameRules.SetWaitingForPlayersRoster(0, 0);
		_warmupLeft = Config.MinWarmupSeconds;
		Console.WriteLine($"[{Name}] warmup on {Server.MapName}, holding {_seats.Count} seats, {_queue.Count} in line");
	}

	private void BeginMatch() {
		RestoreConVar("citadel_trooper_spawn_enabled");
		RestoreConVar("citadel_npc_spawn_enabled");
		RestoreConVar("citadel_player_spawn_time_max_respawn_time");
		RestoreConVar("citadel_allow_purchasing_anywhere");
		// Lets the engine run its own team intro and pregame countdown without a matchmaking lobby.
		SetConVar("citadel_match_intro_force_enabled", "1");
		SetConVar("citadel_pregame_wait_duration", Config.PreGameSeconds.ToString());
		UpdateLoadIn();
		Console.WriteLine($"[{Name}] match on {Server.MapName}, waiting for {_seats.Count} players to load in");
	}

	/// <summary>Whoever hasn't loaded back in by now loses their seat to the queue.</summary>
	private void EndLoadIn() {
		_loadIn = null;
		// Anyone not back by now comes back as a fresh join.
		_carried.Clear();
		var gone = _seats.Values.Where(seat => !seat.Connected && !HasParkedHero(seat)).ToList();
		foreach (var seat in gone)
			_seats.Remove(seat.SteamId);
		if (gone.Count > 0)
			Chat.PrintToChatAll($"{string.Join(", ", gone.Select(seat => seat.Name))} didn't make it back. Their places are open.");
		if (_phase == Phase.Match)
			GameRules.SetWaitingForPlayersRoster(0, 0);
		PromoteWaiting();
		CheckStart();
		Refresh();
	}

	private void Tick() {
		if (_phase == Phase.Match) {
			var state = GameRules.GameState;
			// The engine abandons a match whose lobby hasn't loaded in by a deadline. The load-in timer is ours.
			if (state == EGameState.WaitingForPlayersToJoin && GameRules.GameStateEndTime < GlobalVars.CurTime + 30f)
				GameRules.SetGameStateEndTime(GlobalVars.CurTime + 600f);
			if (state == EGameState.PreGameWait)
				_board.SetTimer(Clock(GameRules.GameStateEndTime - GlobalVars.CurTime));

			// Nobody is back yet while the roster loads in, which isn't the same as everyone having left.
			bool empty = !Players.GetAll().Any(controller => !controller.IsBot) && (MatchLive || _loadIn == null);
			if (empty && _emptyReset == null && !_changingLevel) {
				_emptyReset = Timer.Once(Config.EmptyServerSeconds.Seconds(), () => {
					_emptyReset = null;
					Console.WriteLine($"[{Name}] server empty, back to warmup");
					ReturnToWarmup();
				}).CancelOnMapChange();
			} else if (!empty && _emptyReset != null) {
				_emptyReset.Cancel();
				_emptyReset = null;
			}
		}

		if (_phase == Phase.Warmup && _warmupLeft > 0) {
			_warmupLeft--;
			// A full server counts down on the board and starts at zero.
			if (_seats.Values.Count(seat => seat.Connected) >= Config.PlayersToStart) {
				CheckStart();
				Refresh();
			}
		}

		foreach (var slot in _vacancies.Keys)
			if (Players.FromSlot(slot)?.GetHeroPawn() is { IsAlive: true } pawn && !pawn.IsFrozen)
				pawn.Freeze();

		KickAfk();
	}

	// ─── Joining and leaving ───────────────────────────────────────────────

	public override bool OnClientConnect(ClientConnectEvent args) {
		bool admin = _admins.IsAdmin(args.SteamId);
		// The last slots are kept free for admins. Bots count toward the rest, though they never connect through here.
		if (!admin && Players.GetAll().Count() >= PublicSlots) {
			Console.WriteLine($"[{Name}] turned {args.Name} away: the last {_admins.ReservedSlots} slot(s) are for admins");
			return false;
		}
		// An admin's session starts spectating, unless they're back for a place they still have. Reloading into the next
		// map is the same session, and keeps whatever they chose.
		if (admin && !_carried.Remove(args.SteamId) && !_seats.ContainsKey(args.SteamId))
			_admins.SetPlaying(args.SteamId, false);

		// The engine hands a slot's controller to a joining client only if the SteamIDs match. Give a parked hero's
		// controller to whoever is joining into its slot, and they arrive already in control of it. A hero is only
		// ever parked while no player is waiting, so this can't jump the queue. Bots are kept off parked slots.
		if (_phase == Phase.Match && _vacancies.TryGetValue(args.Slot, out var vacancy) && vacancy.SteamId != args.SteamId
		    && Players.FromSlot(args.Slot) is { } controller) {
			// Someone only here to watch mustn't inherit it, so it moves to a stand-in before their arrival replaces
			// the controller holding it.
			if (IsForcedSpectator(args.SteamId)) ParkOnStandIn(controller, vacancy);
			else controller.PlayerSteamId = args.SteamId;
		}
		return true;
	}

	private int PublicSlots => GlobalVars.MaxClients - _admins.ReservedSlots;

	public override void OnClientFullConnect(ClientFullConnectEvent args) {
		if (args.Controller is not { } controller) return;
		// A stand-in only holds a parked hero; it takes no part itself.
		if (_standIns.ContainsKey(controller.Slot)) return;
		var key = KeyOf(controller);
		if (controller.IsBot) _botNames[key] = controller.PlayerName;
		else if (_admins.IsAdmin(key)) _admins.Seen(key, controller.PlayerName);

		if (!controller.IsBot && Config.BootstrapCheckSeconds > 0 && !UI.HasClientBootstrap(controller.Slot)) {
			AwaitBootstrap(controller);
			return;
		}
		Arrive(controller);
	}

	/// <summary>A player (or bot) takes their part: their seat back, a spectator's view, or a place in line.</summary>
	private void Arrive(CCitadelPlayerController controller) {
		var key = KeyOf(controller);
		if (_seats.TryGetValue(key, out var seat)) {
			Rejoin(controller, seat);
		} else if (IsForcedSpectator(key)) {
			_queue.Remove(key);
			Spectate(controller);
		} else if (_phase == Phase.Match && _vacancies.TryGetValue(controller.Slot, out var vacancy)
		           && controller.GetHeroPawn() is { } pawn && (int)pawn.HeroID > 0) {
			// Arrived through the SteamID swap in OnClientConnect: the parked hero is already theirs.
			ClaimVacancy(controller, vacancy);
		} else {
			if (!_queue.Contains(key)) _queue.Add(key);
			PromoteWaiting();
			if (SeatOf(controller) == null) Spectate(controller);
		}

		CheckStart();
		Refresh();
		RefreshAdminPanels();
	}

	/// <summary>
	/// A player who leaves keeps their controller, hero and all, for a reconnect. A bot's hero is deleted in the
	/// cleanup that follows this, so keep a kicked bot's the same way: straight to a player who is waiting, or else
	/// parked on a stand-in bot until one joins.
	/// </summary>
	public override void OnClientDisconnecting(ClientDisconnectedEvent args) {
		if (_changingLevel || _phase != Phase.Match
		    || args.Reason != ENetworkDisconnectionReason.NetworkDisconnectKicked)
			return;
		if (args.Controller is not { IsBot: true } bot || _standIns.ContainsKey(bot.Slot)
		    || SeatInSlot(bot.Slot) is not { } seat || bot.GetHeroPawn() is not { } pawn || (int)pawn.HeroID <= 0)
			return;

		Chat.PrintToChatAll($"{seat.Name} left.");
		HandOver(bot, seat);
	}

	/// <summary>
	/// Pass <paramref name="holder"/>'s hero on without it leaving the match: straight to a player who is waiting, or
	/// else parked on a stand-in bot until one joins. A place that was left stays the leaver's to come back to; one given
	/// up (<paramref name="keepForOwner"/> false) goes with the hero.
	/// </summary>
	private bool HandOver(CCitadelPlayerController holder, Seat seat, bool keepForOwner = true) {
		var vacancy = new Vacancy(holder.Slot, seat.SteamId, seat.Team, seat.Hero);
		if (NextWaiting(playersOnly: true) is { } player) {
			if (ClaimVacancy(player, vacancy)) return true;
			_queue.Insert(0, KeyOf(player));
		}
		int standIn = ParkOnStandIn(holder, vacancy);
		if (standIn < 0) return false;
		seat.Slot = -1;
		if (!keepForOwner) {
			// Held for the stand-in now; a kicked bot's place is dropped on the next map, and so is this one.
			_seats.Remove(seat.SteamId);
			seat.SteamId = BotKey(_standIns[standIn]);
			seat.Bot = true;
			_seats[seat.SteamId] = seat;
			_vacancies[standIn] = _vacancies[standIn] with { SteamId = seat.SteamId };
		}
		return true;
	}

	/// <summary>
	/// Move a hero onto a new stand-in bot and park it there, frozen in base, as <paramref name="vacancy"/>. Returns the
	/// stand-in's slot, or -1.
	/// </summary>
	private int ParkOnStandIn(CCitadelPlayerController holder, Vacancy vacancy) {
		if (holder.GetHeroPawn() is not { } pawn) return -1;
		string name = $"{holder.PlayerName} (left)";
		int slot = Server.CreateFakeClient(name);
		if (slot < 0 || Players.FromSlot(slot) is not { } standIn || !standIn.TakeOverHero(holder)) {
			Console.WriteLine($"[{Name}] couldn't keep {holder.PlayerName}'s {HeroPool.NameOf(vacancy.Hero)}: no stand-in");
			return -1;
		}
		_vacancies.Remove(vacancy.Slot);
		_standIns[slot] = name;
		_vacancies[slot] = vacancy with { Slot = slot };
		Park(pawn);
		return slot;
	}

	public override void OnClientDisconnect(ClientDisconnectedEvent args) {
		if (_changingLevel) return;

		int slot = args.Slot;
		// A stand-in kicked while still holding its hero took the hero with it; that place opens.
		if (_standIns.Remove(slot) && _vacancies.Remove(slot, out var held)) {
			_seats.Remove(held.SteamId);
			PromoteWaiting();
			Refresh();
			return;
		}
		_awaitingBootstrap.Remove(slot);
		_restartArmed.Remove(slot);
		_board.Forget(slot);
		_adminPanel.Forget(slot);
		_hud.Remove(slot);
		if (_latePicks.Remove(slot, out var latePick)) latePick.Cancel();
		if (args.Controller is { } leaving) {
			_queue.Remove(KeyOf(leaving));
			_botNames.Remove(KeyOf(leaving));
		}

		if (SeatInSlot(slot) is { } seat) {
			seat.Slot = -1;
			if (_phase == Phase.Warmup) {
				_seats.Remove(seat.SteamId);
				PromoteWaiting();
			} else if (seat.Bot) {
				// The game deleted this bot's hero as it left (OnClientDisconnecting keeps a kicked one's); its place opens.
				_seats.Remove(seat.SteamId);
				PromoteWaiting();
			} else if (args.Controller?.GetHeroPawn() is { } pawn && (int)pawn.HeroID > 0) {
				_vacancies[slot] = new Vacancy(slot, seat.SteamId, seat.Team, seat.Hero);
				Park(pawn);
				Chat.PrintToChatAll($"{seat.Name} left.");
				PromoteWaiting();
			} else if (MatchLive) {
				_seats.Remove(seat.SteamId);
				PromoteWaiting();
			} else {
				UpdateLoadIn();
			}
			CheckStart();
		}

		// A leaver's controller stays in their slot, on their team, pointing at their hero. Unless that hero is parked
		// for someone to take over, delete both together: a controller on a team whose hero is gone crashes every client.
		if (args.Controller is { } left && !_vacancies.ContainsKey(slot)) {
			Console.WriteLine($"[{Name}] deleting the controller {left.PlayerName} left in slot {slot}");
			left.GetHeroPawn()?.Remove();
			left.Remove();
		}

		Refresh();
		RefreshAdminPanels();
	}

	/// <summary>
	/// The draft board and HUD need the Deadworks client bootstrap, which only the Deadworks launcher installs. Until a
	/// player's game shows it has it they take no part: no place, no spot in line. Once it does, they arrive as normal;
	/// if it hasn't within the check, they're told to use the launcher, three times, and then kicked.
	/// </summary>
	private void AwaitBootstrap(CCitadelPlayerController controller) {
		int slot = controller.Slot;
		ulong key = KeyOf(controller);
		_awaitingBootstrap[slot] = key;
		int waited = 0, warnings = 0;
		Timer.Sequence(step => {
			if (!_awaitingBootstrap.TryGetValue(slot, out var awaiting) || awaiting != key
			    || Players.FromSlot(slot) is not { } player || KeyOf(player) != key) {
				if (_awaitingBootstrap.GetValueOrDefault(slot) == key) _awaitingBootstrap.Remove(slot);
				return step.Done();
			}
			if (UI.HasClientBootstrap(slot)) {
				_awaitingBootstrap.Remove(slot);
				Arrive(player);
				return step.Done();
			}
			if (waited++ < Config.BootstrapCheckSeconds) return step.Wait(1.Seconds());
			if (warnings++ < 3) {
				if (warnings == 1) Console.WriteLine($"[{Name}] {player.PlayerName} has no client bootstrap");
				player.HudAnnounce("DEADWORKS LAUNCHER REQUIRED", "Connect to this server with the Deadworks launcher from deadworks.net");
				return step.Wait((warnings < 3 ? 6 : 5).Seconds());
			}
			_awaitingBootstrap.Remove(slot);
			Console.WriteLine($"[{Name}] kicking {player.PlayerName}: no client bootstrap");
			player.Kick();
			return step.Done();
		}).CancelOnMapChange();
	}

	/// <summary>A seat's owner is back, after a map reload or a reconnect.</summary>
	private void Rejoin(CCitadelPlayerController controller, Seat seat) {
		seat.Slot = controller.Slot;
		seat.Name = controller.PlayerName;
		seat.Bot = controller.IsBot;
		seat.MarkActive();
		_queue.Remove(seat.SteamId);

		if (_phase == Phase.Warmup) {
			controller.ChangeTeam(seat.Team);
			controller.SelectHero(seat.Hero);
			Welcome(controller);
			return;
		}

		if (controller.GetHeroPawn() is { } pawn && (int)pawn.HeroID > 0) {
			// Back in their own slot mid-match: the engine gave them their controller, hero and all.
			if (_vacancies.Remove(controller.Slot)) pawn.Unfreeze();
			HideHud(controller.Slot);
		} else if (_vacancies.Values.FirstOrDefault(vacancy => vacancy.SteamId == seat.SteamId) is { } own) {
			ClaimVacancy(controller, own);
		} else if (!MatchLive) {
			controller.ChangeTeam(seat.Team);
			controller.AssignedLane = seat.Lane;
			controller.SelectHero(seat.Hero);
			UpdateLoadIn();
			HideHud(controller.Slot);
			if (_draftNotes.Remove(seat.SteamId, out var note)) Chat.PrintToChat(controller, note);
		} else {
			// Nothing of theirs is left in the match; they wait like anyone else.
			_seats.Remove(seat.SteamId);
			_queue.Add(seat.SteamId);
			PromoteWaiting();
			if (SeatOf(controller) == null) Spectate(controller);
		}
	}

	/// <summary>
	/// A map reload disconnects fake clients for good, where players reconnect by themselves. Bring back every bot with a
	/// seat or a place in line, under its old name, so it reclaims them the way a returning player would.
	/// </summary>
	private void RestoreBots() {
		foreach (var (key, name) in _botNames.ToList()) {
			if (!_seats.ContainsKey(key) && !_queue.Contains(key)) {
				_botNames.Remove(key);
			} else if (!Players.GetAll().Any(controller => KeyOf(controller) == key) && Server.CreateFakeClient(name) < 0) {
				Console.WriteLine($"[{Name}] no free slot to bring back {name}");
				return;
			}
		}
	}

	/// <summary>
	/// Hand open places to whoever has waited longest: a parked hero first, then a team with room. Only a player takes
	/// over a parked hero. Its owner may be on the way back, and a bot is only standing in until players come.
	/// </summary>
	private void PromoteWaiting() {
		while (true) {
			if (_vacancies.Values.FirstOrDefault() is { } vacancy && NextWaiting(playersOnly: true) is { } player) {
				if (!ClaimVacancy(player, vacancy)) {
					// The hero couldn't be moved; free its place instead and let them have that.
					_seats.Remove(vacancy.SteamId);
					_queue.Insert(0, KeyOf(player));
				}
				continue;
			}
			int team = OpenTeam(0);
			if (team == 0 || NextWaiting() is not { } controller) return;
			GiveSeat(controller, team);
		}
	}

	private CCitadelPlayerController? NextWaiting(bool playersOnly = false) {
		for (int i = 0; i < _queue.Count; i++) {
			var key = _queue[i];
			var controller = Players.GetAll().FirstOrDefault(candidate => KeyOf(candidate) == key);
			if (controller == null) continue; // still loading in after a map reload; keeps their place
			if (playersOnly && controller.IsBot) continue;
			_queue.RemoveAt(i);
			return controller;
		}
		return null;
	}

	/// <summary>Give a waiting player a new place on <paramref name="team"/>.</summary>
	private void GiveSeat(CCitadelPlayerController controller, int team) {
		var key = KeyOf(controller);
		var hero = PreferredHero(key, controller.IsBot);
		var seat = new Seat {
			SteamId = key,
			Name = controller.PlayerName,
			Team = team,
			Hero = hero,
			Slot = controller.Slot,
			Bot = controller.IsBot,
			Picks = PicksOf(key),
		};
		_seats[key] = seat;
		_lastPicks[key] = (team, hero);

		// A spectator's observer pawn would otherwise linger beside the hero.
		if (controller.GetHeroPawn() == null) controller.Pawn?.Remove();
		controller.ChangeTeam(team);

		if (_phase == Phase.Warmup) {
			controller.SelectHero(hero);
			Welcome(controller);
			return;
		}

		seat.Lane = QuietestLane(team);
		controller.AssignedLane = seat.Lane;
		if (!MatchLive || seat.Bot) {
			// Heroes are locked in before the match, so a place given now comes with the best of their picks still free.
			controller.SelectHero(hero);
			UpdateLoadIn();
			HideHud(controller.Slot);
			return;
		}

		// Joining a match already under way: pick a hero, then spawn with souls to match the team.
		MakeObserver(controller);
		_catchUp.Add(key);
		HideHud(controller.Slot);
		_board.Open(controller, seat, LatePickModel(), Config.LateJoinPickSeconds);
		int slot = controller.Slot;
		_latePicks[slot] = Timer.Once(Config.LateJoinPickSeconds.Seconds(), () => ConfirmLatePick(slot)).CancelOnMapChange();
	}

	private void ConfirmLatePick(int slot) {
		if (_latePicks.Remove(slot, out var timer)) timer.Cancel();
		_board.Close(slot);
		if (SeatInSlot(slot) is { } seat && Players.FromSlot(slot) is { } controller) {
			controller.Pawn?.Remove();
			controller.SelectHero(seat.Hero);
			Chat.PrintToChatAll($"{seat.Name} joins {TeamName(seat.Team)} as {HeroPool.NameOf(seat.Hero)}.");
		}
	}

	private void Welcome(CCitadelPlayerController controller) {
		if (controller.IsBot) return;
		ShowHud(controller);
		OpenBoard(controller);
		Chat.PrintToChat(controller, $"Warmup: the match starts at {Config.PlayersToStart} players. With fewer, everyone " +
			"readying up starts it early. Pick your side and the heroes you want on the board (!hero reopens it).");
	}

	/// <summary>
	/// <see cref="CCitadelPlayerController.MakeObserver"/>, clearing the controller's link to the hero it deletes: a
	/// controller on a team whose hero is gone crashes every client. Needed on Deadworks v0.4.17, whose MakeObserver
	/// leaves the link behind.
	/// </summary>
	private static void MakeObserver(CCitadelPlayerController controller) {
		var hero = controller.GetHeroPawn();
		bool deletesHero = hero != null && controller.Pawn?.EntityHandle == hero.EntityHandle;
		controller.MakeObserver();
		if (deletesHero) HeroPawnHandle.Set(controller.Handle, CBaseEntity.InvalidEntityHandle);
	}

	private static readonly SchemaAccessor<uint> HeroPawnHandle = new("CCitadelPlayerController"u8, "m_hHeroPawn"u8);

	private void Spectate(CCitadelPlayerController controller) {
		MakeObserver(controller);
		controller.ChangeTeam(_spectatorTeam);
		ShowHud(controller);
		if (IsForcedSpectator(KeyOf(controller))) {
			Chat.PrintToChat(controller, "You're spectating as an admin. Open /admin when you want to play.");
			return;
		}
		int place = _queue.IndexOf(KeyOf(controller)) + 1;
		Chat.PrintToChat(controller, _phase == Phase.Warmup
			? $"The lobby is full, so you're spectating. You're #{place} in line for the next open place."
			: $"The match is full, so you're spectating. You're #{place} in line to take over the next hero someone leaves.");
	}

	/// <summary>Hand a parked hero to <paramref name="controller"/>, who may already own its controller.</summary>
	private bool ClaimVacancy(CCitadelPlayerController controller, Vacancy vacancy) {
		_vacancies.Remove(vacancy.Slot);
		_queue.Remove(KeyOf(controller));

		bool alreadyOwned = controller.Slot == vacancy.Slot;
		var from = alreadyOwned ? null : Players.FromSlot(vacancy.Slot);
		if (!alreadyOwned && (from == null || !controller.TakeOverHero(from))) {
			Console.WriteLine($"[{Name}] could not hand slot {vacancy.Slot}'s hero to {controller.PlayerName}");
			return false;
		}
		// Taken over while the stand-in is still here, so kicking it now deletes nothing.
		if (_standIns.Remove(vacancy.Slot, out var standIn)) {
			Server.ExecuteCommand($"kick \"{standIn}\"");
		} else if (from != null && !Players.IsConnected(vacancy.Slot)) {
			// A leaver's controller only stayed to hold their hero. It has none now, and left in place it keeps their
			// portrait, name and stats in the top bar beside whoever took the hero over.
			Console.WriteLine($"[{Name}] deleting the controller {from.PlayerName} left in slot {vacancy.Slot}");
			from.Remove();
		}

		var leaver = _seats.GetValueOrDefault(vacancy.SteamId)?.Name ?? "a leaver";
		_seats.Remove(vacancy.SteamId);
		var seat = new Seat {
			SteamId = KeyOf(controller),
			Name = controller.PlayerName,
			Team = vacancy.Team,
			Hero = vacancy.Hero,
			Lane = controller.AssignedLane,
			Slot = controller.Slot,
			Bot = controller.IsBot,
			Picks = PicksOf(KeyOf(controller)),
		};
		_seats[seat.SteamId] = seat;

		controller.GetHeroPawn()?.Unfreeze();
		HideHud(controller.Slot);
		controller.HudAnnounce($"YOU ARE {HeroPool.NameOf(vacancy.Hero).ToUpperInvariant()}",
			$"Taking over from {leaver} for {TeamName(vacancy.Team)}");
		Chat.PrintToChatAll($"{seat.Name} takes over {leaver}'s {HeroPool.NameOf(vacancy.Hero)}.");
		Console.WriteLine($"[{Name}] {seat.Name} took over {leaver}'s {HeroPool.NameOf(vacancy.Hero)}");
		return true;
	}

	/// <summary>Send a leaver's hero home and keep it there, untouchable, until someone takes it over.</summary>
	private static void Park(CCitadelPlayerPawn pawn) {
		if (pawn.IsAlive) pawn.ForceRespawn();
		// Frozen next tick, once the respawn has put it in base; Tick keeps it that way.
	}

	private bool HasParkedHero(Seat seat) => _vacancies.Values.Any(vacancy => vacancy.SteamId == seat.SteamId);

	// ─── Board actions ─────────────────────────────────────────────────────

	/// <summary>
	/// A click on a hero. In warmup it steps the hero through picked, priority, high priority and back to not picked,
	/// and puts the player on it to try out. Joining a match in progress, it's the hero they join as.
	/// </summary>
	private void PickHero(CCitadelPlayerController controller, Heroes hero) {
		if (SeatOf(controller) is not { } seat || !HeroPool.IsPickable(hero)) return;

		if (_latePicks.ContainsKey(controller.Slot)) {
			if (IsTaken(hero, seat)) {
				Chat.PrintToChat(controller, $"Someone already has {HeroPool.NameOf(hero)}.");
				return;
			}
			seat.Hero = hero;
			_lastPicks[seat.SteamId] = (seat.Team, hero);
			ConfirmLatePick(controller.Slot);
			Refresh();
			return;
		}
		if (_phase != Phase.Warmup || _matchFound != null) {
			Chat.PrintToChat(controller, "Heroes are locked in for this match.");
			return;
		}

		var pick = seat.Picks.GetValueOrDefault(hero).Next();
		if (pick == HeroPick.None) seat.Picks.Remove(hero);
		else seat.Picks[hero] = pick;
		if (!_clickOrder.TryGetValue(seat.SteamId, out var order)) _clickOrder[seat.SteamId] = order = new();
		order.Remove(hero);
		order.Add(hero);

		if (WarmupHero(seat, hero) is { } play && play != seat.Hero) {
			seat.Hero = play;
			_lastPicks[seat.SteamId] = (seat.Team, play);
			controller.SelectHero(play);
		}
		if (seat.Picks.Count < Config.MinHeroPicks) seat.Ready = false;
		CheckStart();
		Refresh();
	}

	private void ChangeSide(CCitadelPlayerController controller, int team) {
		if (_phase != Phase.Warmup || team is not (Amber or Sapphire)) return;
		if (SeatOf(controller) is not { } seat || seat.Team == team) return;
		if (TeamCount(team) >= TeamSize) {
			Chat.PrintToChat(controller, $"{TeamName(team)} is full.");
			return;
		}

		seat.Team = team;
		seat.Ready = false;
		if (IsTaken(seat.Hero, seat)) seat.Hero = PreferredHero(seat.SteamId, seat.Bot, except: seat);
		_lastPicks[seat.SteamId] = (team, seat.Hero);

		controller.ChangeTeam(team);
		// Back to the new team's base, then onto the hero if it had to change.
		controller.GetHeroPawn()?.ForceRespawn();
		if (controller.GetHeroPawn() is not { } pawn || pawn.HeroID != seat.Hero)
			controller.SelectHero(seat.Hero);
		CheckStart();
		Refresh();
	}

	private void ToggleReady(CCitadelPlayerController controller) {
		if (_phase != Phase.Warmup || SeatOf(controller) is not { } seat) return;
		if (!seat.Ready && seat.Picks.Count < Config.MinHeroPicks) {
			Chat.PrintToChat(controller, $"Pick at least {Config.MinHeroPicks} heroes before readying up.");
			return;
		}
		seat.Ready = !seat.Ready;
		CheckStart();
		Refresh();
	}

	private void CloseBoard(CCitadelPlayerController controller) {
		if (_latePicks.ContainsKey(controller.Slot)) {
			ConfirmLatePick(controller.Slot);
			return;
		}
		_board.Close(controller.Slot);
		Refresh();
	}

	private void OpenBoard(CCitadelPlayerController controller) {
		if (_phase == Phase.Warmup) _board.Open(controller, SeatOf(controller), WarmupModel());
		else if (!_latePicks.ContainsKey(controller.Slot)) Chat.PrintToChat(controller, "Heroes are locked in for this match.");
	}

	/// <summary>
	/// The hero a player plays in warmup after clicking <paramref name="clicked"/>: always one of their top priority
	/// picks. A hero just brought up to the top takes over; otherwise they stay on their current hero while it's still
	/// at the top, and only when it isn't do they move to their most recently clicked top pick. Heroes someone else is
	/// playing are skipped. Null to stay as they are.
	/// </summary>
	private Heroes? WarmupHero(Seat seat, Heroes clicked) {
		if (seat.Picks.Count == 0) return null;
		var top = seat.Picks.Values.Max();
		if (seat.Picks.GetValueOrDefault(clicked) == top && !IsTaken(clicked, seat)) return clicked;
		if (seat.Picks.GetValueOrDefault(seat.Hero) == top) return null;
		var order = _clickOrder.GetValueOrDefault(seat.SteamId) ?? [];
		return seat.Picks.Where(pick => pick.Value == top && !IsTaken(pick.Key, seat))
			.OrderByDescending(pick => order.IndexOf(pick.Key))
			.Select(pick => (Heroes?)pick.Key)
			.FirstOrDefault();
	}

	/// <summary>A player's picks, the same dictionary every seat of theirs holds.</summary>
	private Dictionary<Heroes, HeroPick> PicksOf(ulong key) {
		if (!_picks.TryGetValue(key, out var picks)) _picks[key] = picks = new();
		return picks;
	}

	/// <summary>
	/// The hero a new place starts on: the player's most wanted pick that nobody has (a random one among equals), else
	/// their last hero if nobody has it, else any hero nobody has.
	/// </summary>
	private Heroes PreferredHero(ulong key, bool bot, Seat? except = null) {
		bool Free(Heroes hero) => HeroPool.IsPickable(hero) && !IsTaken(hero, except) && (!bot || HeroPool.IsBotPlayable(hero));
		if (_picks.TryGetValue(key, out var picks)
		    && picks.Where(pick => Free(pick.Key)).GroupBy(pick => pick.Value).MaxBy(tier => tier.Key) is { } best) {
			var heroes = best.Select(pick => pick.Key).ToList();
			return heroes[Random.Shared.Next(heroes.Count)];
		}
		if (_lastPicks.TryGetValue(key, out var last) && last.Hero != 0 && Free(last.Hero)) return last.Hero;
		return HeroPool.Random(_seats.Values.Where(seat => seat != except).Select(seat => seat.Hero), bot);
	}

	/// <summary>Every seat's hero from their picks, all different, or null when the picks don't allow it.</summary>
	private (List<Seat> Seats, Heroes[] Heroes)? Draft() {
		var seats = _seats.Values.ToList();
		var entries = seats.Select(seat => new DraftEntry(seat.Picks, seat.Bot, _draftDebt.GetValueOrDefault(seat.SteamId)))
			.ToList();
		return HeroDraft.Assign(entries, HeroPool.All, Random.Shared) is { } heroes ? (seats, heroes) : null;
	}

	/// <summary>Players who haven't picked enough heroes yet.</summary>
	private int ShortOfPicks() => _seats.Values.Count(seat => !seat.Bot && seat.Picks.Count < Config.MinHeroPicks);

	/// <summary>What stops a match from starting as things stand, or null.</summary>
	private string? PickProblem() {
		int missing = ShortOfPicks();
		if (missing > 0)
			return $"Waiting for {missing} {(missing == 1 ? "player" : "players")} to pick at least {Config.MinHeroPicks} heroes.";
		return Draft() == null ? "The heroes couldn't be handed out." : null;
	}

	/// <summary>Warmup again after a start fell through, long enough for picks to change.</summary>
	private int RestartWarmup() => _warmupLeft = Math.Max(Config.MinWarmupSeconds, 30);

	/// <summary>Lock in everyone's hero for the match from their picks. False when the picks don't allow it.</summary>
	private bool DraftHeroes() {
		if (Draft() is not var (seats, heroes)) return false;
		for (int i = 0; i < seats.Count; i++) {
			var seat = seats[i];
			seat.Hero = heroes[i];
			if (seat.Bot) continue;
			var got = seat.Picks.GetValueOrDefault(seat.Hero);
			var best = seat.Picks.Values.Max();
			_draftDebt[seat.SteamId] = got < best ? _draftDebt.GetValueOrDefault(seat.SteamId) + 1 : 0;
			string name = HeroPool.NameOf(seat.Hero);
			_draftNotes[seat.SteamId] = got == HeroPick.None
				? $"Your picks clashed with everyone else's, so you got a random hero: {name}."
				: $"You're {name}, one of your {got.Describe()} picks.";
			Console.WriteLine($"[{Name}] draft: {seat.Name} -> {name} ({got}; best pick {best}, owed {_draftDebt[seat.SteamId]})");
		}
		return true;
	}

	// ─── Admin ─────────────────────────────────────────────────────────────

	private bool IsAdmin(CCitadelPlayerController controller) => !controller.IsBot && _admins.IsAdmin(controller.PlayerSteamId);

	/// <summary>An admin watching by choice: never given a place, through map reloads too, until they ask to play.</summary>
	private bool IsForcedSpectator(ulong key) => _admins.IsAdmin(key) && !_admins.WantsToPlay(key);

	private bool IsOwner(CCitadelPlayerController controller) =>
		!controller.IsBot && _admins.IsOwner(controller.PlayerSteamId);

	/// <summary>Switch an admin between watching and playing.</summary>
	private void SetForcedSpectator(CCitadelPlayerController admin, bool forced) {
		var key = KeyOf(admin);
		if (forced == IsForcedSpectator(key)) return;
		if (forced) {
			if (SeatOf(admin) is { } seat && !GiveUpSeat(admin, seat)) {
				Chat.PrintToChat(admin, "Your hero couldn't be handed on just now, so you're still playing.");
				return;
			}
			_admins.SetPlaying(key, false);
			_queue.Remove(key);
			Spectate(admin);
			PromoteWaiting();
		} else {
			_admins.SetPlaying(key, true);
			if (!_queue.Contains(key)) _queue.Add(key);
			PromoteWaiting();
			// Seated: the board or HUD takes it from here. Otherwise they wait in line like anyone else.
			if (SeatOf(admin) != null) _adminPanel.Close(admin.Slot);
			else Spectate(admin);
		}
		CheckStart();
		Refresh();
		RefreshAdminPanels();
	}

	/// <summary>Give up a place by choice. In warmup it just opens; in a match the hero stays on for the next player.</summary>
	private bool GiveUpSeat(CCitadelPlayerController holder, Seat seat) {
		_board.Close(holder.Slot);
		if (_phase == Phase.Match && holder.GetHeroPawn() is { } pawn && (int)pawn.HeroID > 0) {
			Chat.PrintToChatAll($"{seat.Name} is spectating now.");
			return HandOver(holder, seat, keepForOwner: false);
		}
		if (_latePicks.Remove(holder.Slot, out var latePick)) latePick.Cancel();
		_seats.Remove(seat.SteamId);
		if (_phase == Phase.Warmup) holder.GetHeroPawn()?.Remove();
		return true;
	}

	private void Promote(ulong steamId) {
		if (Players.GetAll().FirstOrDefault(c => !c.IsBot && c.PlayerSteamId == steamId) is not { } player) return;
		_admins.Add(steamId, player.PlayerName);
		Chat.PrintToChat(player, "You're an admin now. Open /admin for the admin panel.");
		RefreshAdminPanels();
	}

	private void Demote(ulong steamId) {
		if (_admins.IsOwner(steamId)) return;
		var player = Players.GetAll().FirstOrDefault(c => !c.IsBot && c.PlayerSteamId == steamId);
		// Out of admin spectating first, into the line like anyone else.
		if (player != null) SetForcedSpectator(player, false);
		_admins.SetPlaying(steamId, false);
		_admins.Remove(steamId);
		if (player != null) {
			_adminPanel.Close(player.Slot);
			Chat.PrintToChat(player, "You're no longer an admin.");
		}
		RefreshAdminPanels();
	}

	/// <summary>
	/// The admin panel's restart button: the first click arms it for a few seconds, the second sends everyone back to
	/// warmup, ending any match in progress.
	/// </summary>
	private void RestartFromPanel(CCitadelPlayerController admin) {
		const int confirmMs = 5000;
		int slot = admin.Slot;
		if (_restartArmed.Remove(slot, out var armedAt) && Environment.TickCount64 - armedAt < confirmMs) {
			Chat.PrintToChatAll($"{admin.PlayerName} sent everyone back to warmup.");
			Console.WriteLine($"[{Name}] {admin.PlayerName} restarted to warmup from the admin panel");
			ReturnToWarmup();
			return;
		}
		long now = Environment.TickCount64;
		_restartArmed[slot] = now;
		RefreshAdminPanels();
		Timer.Once(confirmMs.Milliseconds(), () => {
			// Only disarm this click, not a later one.
			if (_restartArmed.TryGetValue(slot, out var at) && at == now) {
				_restartArmed.Remove(slot);
				RefreshAdminPanels();
			}
		}).CancelOnMapChange();
	}

	private AdminModel AdminModelFor(CCitadelPlayerController viewer) {
		var humans = Players.GetAll().Where(c => !c.IsBot).ToList();
		var online = humans.Select(c => c.PlayerSteamId).ToHashSet();
		return new AdminModel(
			IsForcedSpectator(KeyOf(viewer)),
			IsOwner(viewer),
			_admins.ReservedSlots,
			GlobalVars.MaxClients,
			_admins.All.Select(admin => (admin.SteamId, admin.Name, online.Contains(admin.SteamId), _admins.IsOwner(admin.SteamId)))
				.ToList(),
			humans.Where(c => !_admins.IsAdmin(c.PlayerSteamId)).Select(c => (c.PlayerSteamId, c.PlayerName)).ToList(),
			_restartArmed.ContainsKey(viewer.Slot));
	}

	private void RefreshAdminPanels() {
		foreach (var slot in _adminPanel.OpenSlots.ToList()) {
			if (Players.FromSlot(slot) is { } viewer && IsAdmin(viewer)) _adminPanel.Show(slot, AdminModelFor(viewer));
			else _adminPanel.Close(slot);
		}
	}

	// ─── Starting and ending ───────────────────────────────────────────────

	private void CheckStart() {
		if (_phase != Phase.Warmup) return;
		var here = _seats.Values.Where(seat => seat.Connected).ToList();
		// Nobody starts a match while someone's held place is still loading back in.
		bool holding = _seats.Values.Any(seat => !seat.Connected);
		bool full = here.Count >= Config.PlayersToStart;
		bool allReady = here.Count >= Config.MinPlayersToReadyUp
			&& here.Count(seat => seat.Team == Amber) == here.Count(seat => seat.Team == Sapphire)
			&& here.All(seat => seat.Ready || seat.Bot)
			&& here.Any(seat => !seat.Bot);
		// A full lobby starts when warmup runs out; readying up only starts a smaller one early.
		bool go = !holding && (full ? _warmupLeft <= 0 : allReady);
		// Every start waits for everyone to have a hero of their own picks to get. Until then warmup carries on: a full
		// lobby's clock starts over, and a smaller one readies up again once it's sorted.
		if (go && PickProblem() is { } problem) {
			go = false;
			if (full) RestartWarmup();
			else foreach (var seat in here) seat.Ready = false;
			Chat.PrintToChatAll(problem + (full ? $" Warmup goes on for {Clock(_warmupLeft)}." : " Ready up again once it's sorted."));
		}

		if (go && _matchFound == null) {
			_matchFoundLeft = Config.MatchFoundSeconds;
			Announce("MATCH FOUND", full ? "The server is full" : "Everyone is ready");
			_matchFound = Timer.Sequence(step => {
				if (_matchFoundLeft <= 0) {
					LaunchMatch();
					return step.Done();
				}
				Refresh();
				_matchFoundLeft--;
				return step.Wait(1.Seconds());
			}).CancelOnMapChange();
		} else if (!go && _matchFound != null) {
			_matchFound.Cancel();
			_matchFound = null;
			Chat.PrintToChatAll("Match start cancelled.");
		}
	}

	private void LaunchMatch() {
		_matchFound = null;
		foreach (var gone in _seats.Values.Where(seat => !seat.Connected).ToList())
			_seats.Remove(gone.SteamId);
		if (!DraftHeroes()) {
			// The roster changed during the countdown in a way the picks can't cover.
			RestartWarmup();
			Chat.PrintToChatAll(PickProblem() ?? "The heroes couldn't be handed out. Warmup goes on.");
			Refresh();
			return;
		}
		AssignLanes(Amber);
		AssignLanes(Sapphire);
		foreach (var seat in _seats.Values)
			_lastPicks[seat.SteamId] = (seat.Team, seat.Hero);

		Console.WriteLine($"[{Name}] launching match: " + string.Join(", ",
			_seats.Values.Select(seat => $"{seat.Name}={TeamName(seat.Team)}/{seat.Hero}/{seat.Lane}")));
		_phase = Phase.Match;
		ChangeLevel();
	}

	private void ReturnToWarmup() {
		foreach (var seat in _seats.Values)
			_lastPicks[seat.SteamId] = (seat.Team, seat.Hero);
		_phase = Phase.Warmup;
		ChangeLevel();
	}

	private void ChangeLevel() {
		ClosePanels();
		_changingLevel = true;
		// Everyone here reloads into the next map with it; their reconnect isn't a fresh join.
		_carried = Players.GetAll().Where(c => !c.IsBot).Select(c => c.PlayerSteamId).ToHashSet();
		Server.ExecuteCommand($"changelevel {(Config.Map.Length > 0 ? Config.Map : Server.MapName)}");
	}

	/// <summary>
	/// Load the heroes in play with the map. With no lobby the game preloads none, and one loaded on demand while a player
	/// is still loading in can reach their game before it has the hero, which crashes it. A map change puts everyone back
	/// on their seat's hero as they load in, so those are the ones to have ready, along with what anyone in line would
	/// get: their top picks, or their last hero.
	/// </summary>
	public override void OnPrecacheResources() {
		var heroes = _seats.Values.Select(seat => seat.Hero).ToHashSet();
		foreach (var key in _queue) {
			if (_picks.TryGetValue(key, out var picks) && picks.Count > 0) {
				var top = picks.Values.Max();
				heroes.UnionWith(picks.Where(pick => pick.Value == top).Select(pick => pick.Key));
			}
			if (_lastPicks.TryGetValue(key, out var last)) heroes.Add(last.Hero);
		}
		heroes.RemoveWhere(hero => !HeroPool.IsPickable(hero));
		foreach (var hero in heroes)
			Precache.AddHero(hero);
		if (heroes.Count > 0)
			Console.WriteLine($"[{Name}] preloading {heroes.Count} heroes: {string.Join(", ", heroes.Select(HeroPool.NameOf))}");
	}

	/// <summary>
	/// Take down every panel the plugin is showing. Forgetting one isn't enough: the UI layer keeps each player's panels
	/// and puts them back whenever their game rebuilds its UI, which it does after every map load.
	/// </summary>
	private void ClosePanels() {
		_board.CloseAll();
		foreach (var slot in _hud.ToList())
			HideHud(slot);
		foreach (var slot in _adminPanel.OpenSlots.ToList())
			_adminPanel.Close(slot);
	}

	private void AssignLanes(int team) {
		var seats = _seats.Values.Where(seat => seat.Team == team).OrderBy(_ => Random.Shared.Next()).ToList();
		for (int i = 0; i < seats.Count; i++)
			seats[i].Lane = Config.Lanes[i % Config.Lanes.Count];
	}

	private LaneColor QuietestLane(int team) => Config.Lanes
		.OrderBy(lane => _seats.Values.Count(seat => seat.Team == team && seat.Lane == lane))
		.First();

	/// <summary>Hold the engine in WaitingForPlayersToJoin until the roster has loaded back in.</summary>
	private void UpdateLoadIn() {
		if (_phase != Phase.Match || GameRules.GameState > EGameState.WaitingForPlayersToJoin) return;
		int total = _seats.Count;
		int here = _seats.Values.Count(seat => seat.Connected);
		if (total == 0 || here >= total)
			GameRules.SetWaitingForPlayersRoster(0, 0);
		else
			GameRules.SetWaitingForPlayersRoster((uint)here, (uint)total);
	}

	public override void OnGameStateChanged(EGameState state) {
		if (_phase != Phase.Match) return;
		switch (state) {
			case EGameState.GameInProgress:
				_board.CloseAll();
				foreach (var seat in _seats.Values)
					if (seat.Connected) HideHud(seat.Slot);
				break;
			case EGameState.PostGame:
				int winner = GameRules.WinningTeam;
				Announce(winner is Amber or Sapphire ? $"{TeamName(winner).ToUpperInvariant()} WINS" : "MATCH OVER",
					$"Back to warmup in {Config.PostGameSeconds} seconds");
				Timer.Once(Config.PostGameSeconds.Seconds(), ReturnToWarmup).CancelOnMapChange();
				break;
		}
		Refresh();
	}

	public override bool OnGameStateChanging(EGameState currentState, EGameState newState) {
		// End is where a matchmade server lets everyone go: clients disconnect themselves on seeing it, and one doing so
		// during a map change crashes. Refused in every phase, since the engine's PostGame timer can still ask for it
		// while the plugin is changing the map back to warmup.
		if (newState == EGameState.End)
			return false;
		// Warmup is a sandbox on a live map; nothing ends it but the plugin.
		if (_phase == Phase.Warmup)
			return newState is not (EGameState.PostGame or EGameState.Abandoned);
		// Missing players are the load-in timer's call, not the engine's.
		return !(currentState == EGameState.WaitingForPlayersToJoin && newState == EGameState.Abandoned);
	}

	// ─── AFK ───────────────────────────────────────────────────────────────

	public override void OnProcessUsercmds(ProcessUsercmdsEvent args) {
		if (args.Usercmds.Any(IsInput)) MarkActive(args.PlayerSlot);
	}

	public override HookResult OnChatMessage(ChatMessage message) {
		MarkActive(message.SenderSlot);
		return HookResult.Continue;
	}

	private static bool IsInput(CCitadelUserCmdPB cmd) {
		if (cmd.Base is not { } move) return false;
		return move.Mousedx != 0 || move.Mousedy != 0 || cmd.ViewDeltaX.Any(d => d != 0) || cmd.ViewDeltaY.Any(d => d != 0)
		       || move.Forwardmove != 0 || move.Leftmove != 0 || move.Upmove != 0
		       || (move.ButtonsPb?.Buttonstate2 ?? 0) != 0; // buttons pressed or released this command
	}

	private void MarkActive(int slot) => SeatInSlot(slot)?.MarkActive();

	private int AfkSeconds(Seat seat) => (int)((Environment.TickCount64 - seat.LastInput) / 1000);

	/// <summary>
	/// Kick players who hold a place without playing it. Leaving through a kick is like leaving any other way: mid-match
	/// the hero is parked for the next in line, and stays the leaver's to reclaim until someone takes it.
	/// </summary>
	private void KickAfk() {
		// Loading in, the intro and the result screen give nobody anything to do.
		bool counting = Config.AfkKickSeconds > 0 && !_changingLevel
		                && (_phase == Phase.Warmup || GameRules.GameState is EGameState.PreGameWait or EGameState.GameInProgress);
		var idle = new List<(CCitadelPlayerController Controller, Seat Seat)>();
		foreach (var seat in _seats.Values) {
			if (seat.Bot || seat.Controller is not { } controller) continue;
			if (!counting) {
				seat.MarkActive();
				continue;
			}
			int left = Config.AfkKickSeconds - AfkSeconds(seat);
			if (left <= 0) {
				idle.Add((controller, seat));
			} else if (left <= 30 && seat.AfkWarnings < 2) {
				seat.AfkWarnings = 2;
				controller.HudAnnounce("ARE YOU STILL THERE?", $"Move within {left} seconds or you'll be kicked for being AFK");
			} else if (left <= 60 && seat.AfkWarnings < 1) {
				seat.AfkWarnings = 1;
				Chat.PrintToChat(controller, $"You'll be kicked for being AFK in {left} seconds unless you move.");
			}
		}
		foreach (var (controller, seat) in idle) {
			Console.WriteLine($"[{Name}] kicking {seat.Name}: no input for {AfkSeconds(seat)}s");
			Chat.PrintToChatAll($"{seat.Name} was kicked for being AFK.");
			controller.Kick(ENetworkDisconnectionReason.NetworkDisconnectKickedIdle);
		}
	}

	// ─── Hooks ─────────────────────────────────────────────────────────────

	public override void OnPawnHeroInitialized(CCitadelPlayerPawn pawn) {
		if (pawn.Controller is not { } controller) return;
		var key = KeyOf(controller);

		if (_phase == Phase.Warmup && Config.WarmupSouls > 0) {
			Timer.NextTick(() => {
				if (!pawn.IsValid) return;
				int missing = Config.WarmupSouls - pawn.GetCurrency(ECurrencyType.EGold);
				if (missing > 0) pawn.ModifyCurrency(ECurrencyType.EGold, missing, ECurrencySource.ECheats, silent: true);
			});
		} else if (_phase == Phase.Match && _catchUp.Remove(key) && SeatOf(controller) is { } seat) {
			var mates = _seats.Values
				.Where(other => other.Team == seat.Team && other != seat && other.Controller != null)
				.Select(other => other.Controller!.PlayerDataGlobal.GoldNetWorth)
				.ToList();
			int souls = mates.Count > 0 ? (int)mates.Average() : 0;
			if (souls > 0)
				Timer.NextTick(() => {
					if (pawn.IsValid) pawn.ModifyCurrency(ECurrencyType.EGold, souls, ECurrencySource.ECheats, silent: true);
				});
		}
	}

	/// <summary>
	/// Changing hero sets the same pawn up again as the new hero, starting souls and ability points included, so
	/// swapping back and forth in base before the launch would pile them up. Each pawn gets its starting amounts once.
	/// </summary>
	public override HookResult OnModifyCurrency(ModifyCurrencyEvent args) {
		if (args.Source != ECurrencySource.EStartingAmount || _startingGrants.Add((args.Pawn.EntityHandle, args.CurrencyType)))
			return HookResult.Continue;
		Console.WriteLine($"[{Name}] {args.Pawn.Controller?.PlayerName} changed hero, no second starting {args.CurrencyType} ({args.Amount})");
		return HookResult.Stop;
	}

	public override HookResult OnTakeDamage(TakeDamageEvent args) {
		// Warmup shares the map with the real objectives' spawn state, so nothing but heroes can be hurt.
		if (_phase == Phase.Warmup && !args.Entity.Is<CCitadelPlayerPawn>())
			return HookResult.Stop;
		if (_phase == Phase.Match && args.Entity.Is<CCitadelPlayerPawn>()
		    && _vacancies.Keys.Any(slot => Players.FromSlot(slot)?.GetHeroPawn()?.EntityHandle == args.Entity.EntityHandle))
			return HookResult.Stop;
		return HookResult.Continue;
	}

	/// <summary>
	/// A matchmade server takes "game over" as its cue to sign the match out and retire: after it, loading the next
	/// map ends the process. The event is server-only; players still see the result through the game rules.
	/// </summary>
	[GameEventHandler("gameover_msg")]
	public HookResult OnGameOver(GameEvent args) => _phase == Phase.Match ? HookResult.Stop : HookResult.Continue;

	public override HookResult OnClientConCommand(ClientConCommandEvent args) =>
		args.Command is "selecthero" or "changeteam" or "jointeam" ? HookResult.Stop : HookResult.Continue;

	// ─── Commands ──────────────────────────────────────────────────────────

	[Command("hero", Description = "Open the hero picker, or step a hero's pick up a level: !hero haze")]
	public void CmdHero(CCitadelPlayerController caller, params string[] name) {
		if (name.Length == 0) {
			OpenBoard(caller);
			return;
		}
		if (HeroPool.Find(string.Join(' ', name)) is { } info) PickHero(caller, info.Hero);
		else Chat.PrintToChat(caller, $"No hero called '{string.Join(' ', name)}'.");
	}

	[Command("ready", Description = "Ready up in warmup; with fewer than the players a match needs, everyone ready starts it early")]
	public void CmdReady(CCitadelPlayerController caller) => ToggleReady(caller);

	[Command("side", Description = "Switch sides during warmup: !side king or !side archmother")]
	public void CmdSide(CCitadelPlayerController caller, params string[] side) {
		int team = ParseTeam(string.Join(' ', side));
		if (team == 0) Chat.PrintToChat(caller, "Use !side king (The Hidden King) or !side archmother (The Archmother).");
		else ChangeSide(caller, team);
	}

	[Command("ff", Description = "Vote to forfeit the match; your team forfeits once everyone on it has voted")]
	public void CmdForfeit(CCitadelPlayerController caller) {
		if (!MatchLive || GameRules.WinningTeam is Amber or Sapphire) {
			Chat.PrintToChat(caller, "There's no match to forfeit.");
			return;
		}
		if (SeatOf(caller) is not { } seat || seat.Team is not (Amber or Sapphire)) {
			Chat.PrintToChat(caller, "Only players in the match can vote to forfeit.");
			return;
		}

		bool newVote = _forfeitVotes.Add(seat.SteamId);
		// Everyone on the team who's here to vote: players, not bots, and not anyone who left.
		var voters = _seats.Values.Where(other => other.Team == seat.Team && other.Connected && !other.Bot).ToList();
		int votes = voters.Count(voter => _forfeitVotes.Contains(voter.SteamId));

		if (votes >= voters.Count) {
			Chat.PrintToChatAll($"{TeamName(seat.Team)} forfeits.");
			Console.WriteLine($"[{Name}] {TeamName(seat.Team)} forfeits");
			GameRules.WinningTeam = seat.Team == Amber ? Sapphire : Amber;
			return;
		}
		if (!newVote) {
			Chat.PrintToChat(caller, $"You've voted to forfeit; {votes} of {voters.Count} on your team have.");
			return;
		}
		foreach (var voter in voters)
			if (voter.Controller is { } teammate)
				Chat.PrintToChat(teammate, $"{seat.Name} wants to forfeit ({votes}/{voters.Count}). Type /ff to agree.");
	}

	[Command("admin", Description = "Open the admin panel (admins only)")]
	public void CmdAdmin(CCitadelPlayerController caller) {
		if (!IsAdmin(caller)) {
			Chat.PrintToChat(caller, "That's for admins.");
			return;
		}
		_board.Close(caller.Slot);
		_adminPanel.Open(caller, AdminModelFor(caller));
	}

	[Command("am_specteam", Description = "Put spectators on another team, to find one the chat box allows: dw_am_specteam <0|1|2|3>",
		ConsoleOnly = true)]
	public void CmdSpecTeam(CCitadelPlayerController? caller, int team) {
		RequireAdmin(caller);
		if (team is < 0 or > 3) return;
		_spectatorTeam = team;
		foreach (var controller in Players.GetAll().Where(c => !c.IsBot && SeatOf(c) == null))
			controller.ChangeTeam(team);
		Reply(caller, $"spectators now sit on team {team}");
	}

	[Command("am_start", Description = "Start the match now with whoever is here", ConsoleOnly = true)]
	public void CmdStart(CCitadelPlayerController? caller) {
		RequireAdmin(caller);
		if (_phase != Phase.Warmup) {
			Reply(caller, $"not in warmup");
			return;
		}
		_matchFound?.Cancel();
		LaunchMatch();
	}

	[Command("am_warmup", Description = "Abandon the match and go back to warmup", ConsoleOnly = true)]
	public void CmdWarmup(CCitadelPlayerController? caller) {
		RequireAdmin(caller);
		ReturnToWarmup();
	}

	[Command("am_end", Description = "End the match with a winner: dw_am_end king or dw_am_end archmother", ConsoleOnly = true)]
	public void CmdEnd(CCitadelPlayerController? caller, string team) {
		RequireAdmin(caller);
		if (!MatchLive) {
			Reply(caller, $"no match in progress");
			return;
		}
		int winner = ParseTeam(team);
		if (winner == 0) Reply(caller, $"which team? king or archmother");
		else GameRules.WinningTeam = winner;
	}

	[Command("am_killpatron", Description = "Win the match for a team the real way, by destroying the enemy shrines and Patron: " +
		"dw_am_killpatron king", ConsoleOnly = true)]
	public void CmdKillPatron(CCitadelPlayerController? caller, string team) {
		RequireAdmin(caller);
		int winner = ParseTeam(team);
		if (!MatchLive || winner == 0) {
			Reply(caller, $"needs a match in progress and a team: king or archmother");
			return;
		}
		int loser = winner == Amber ? Sapphire : Amber;
		int attempts = 0;
		Timer.Sequence(step => {
			if (GameRules.GameState != EGameState.GameInProgress || ++attempts > 30) return step.Done();
			// The Patron can't be hurt while its shrines stand, and fights on in a second phase.
			foreach (var name in new[] { "destroyable_building", "npc_boss_tier3" })
				foreach (var target in Entities.ByDesignerName(name))
					if (target.TeamNum == loser && target.IsAlive)
						target.Hurt(1_000_000f);
			return step.Wait(1.Seconds());
		}).CancelOnMapChange();
	}

	[Command("am_lane", Description = "Set your lane before the zipline launch: 1 yellow, 4 blue, 6 purple", ConsoleOnly = true, Hidden = true)]
	public void CmdLane(CCitadelPlayerController caller, int lane) {
		RequireAdmin(caller);
		SetLane(caller, (LaneColor)lane);
	}

	[Command("am_setlane", Description = "Set a player's lane before the zipline launch: dw_am_setlane <slot> <1|4|6>",
		ConsoleOnly = true)]
	public void CmdSetLane(CCitadelPlayerController? caller, int slot, int lane) {
		RequireAdmin(caller);
		if (Players.FromSlot(slot) is { } controller) SetLane(controller, (LaneColor)lane);
	}

	private void SetLane(CCitadelPlayerController controller, LaneColor lane) {
		if (MatchLive || SeatOf(controller) is not { } seat) return;
		seat.Lane = lane;
		controller.AssignedLane = lane;
		Console.WriteLine($"[{Name}] {controller.PlayerName} -> lane {lane}");
	}

	[Command("am_status", Description = "Print the roster and the queue", ConsoleOnly = true)]
	public void CmdStatus(CCitadelPlayerController? caller) {
		RequireAdmin(caller);
		Reply(caller, $"phase {_phase}, state {GameRules.GameState}, clock {GameRules.GameClock:0}, " +
			$"{_seats.Count} seats, {_queue.Count} in line, {_vacancies.Count} parked, max clients {GlobalVars.MaxClients}" +
			(_phase == Phase.Warmup && _warmupLeft > 0 ? $", {_warmupLeft}s of warmup left" : ""));
		foreach (var seat in _seats.Values.OrderBy(seat => seat.Team)) {
			var pawn = seat.Controller?.GetHeroPawn();
			var where = pawn == null ? "no hero" : $"{pawn.HeroID} at {pawn.Position.X:0},{pawn.Position.Y:0},{pawn.Position.Z:0} " +
				$"abilities {Unlocked(pawn)}/4, unlocks {pawn.GetCurrency(ECurrencyType.EAbilityUnlocks)}, AP {pawn.GetCurrency(ECurrencyType.EAbilityPoints)}";
			Reply(caller, $"  {TeamName(seat.Team),-15} slot {seat.Slot,2}  {seat.Lane,-7} {seat.Hero,-12} {where}  " +
				$"{(seat.Ready ? "ready " : "")}{(seat.Bot ? "" : $"idle {AfkSeconds(seat)}s ")}{seat.Name}");
		}
		foreach (var vacancy in _vacancies.Values) {
			var pawn = Players.FromSlot(vacancy.Slot)?.GetHeroPawn();
			var where = pawn == null ? "no hero"
				: $"{pawn.HeroID} at {pawn.Position.X:0},{pawn.Position.Y:0},{pawn.Position.Z:0}{(pawn.IsFrozen ? " frozen" : "")}";
			Reply(caller, $"  parked   slot {vacancy.Slot,2}  {vacancy.Hero,-12} {where}  {TeamName(vacancy.Team)}" +
				(_standIns.TryGetValue(vacancy.Slot, out var standIn) ? $", held by {standIn}" : ""));
		}
		// Only the server's own slots: on the v0.4.17 API, GetAllControllers also reads map entities past them.
		foreach (var controller in Players.GetAllControllers().Where(controller => controller.Slot < GlobalVars.MaxClients))
			Reply(caller, $"  controller slot {controller.Slot,2} team {controller.TeamNum} " +
				$"{(Players.IsConnected(controller.Slot) ? "connected" : "gone")} " +
				$"hero {controller.GetHeroPawn()?.HeroID.ToString() ?? "none"}  {controller.PlayerName}");
		foreach (var pawn in Entities.ByClass<CCitadelPlayerPawn>())
			Reply(caller, $"  pawn {pawn.HeroID,-12} team {pawn.TeamNum} controller " +
				$"{pawn.Controller?.Slot.ToString() ?? "none"}{(pawn.IsAlive ? "" : " dead")}");
		for (int i = 0; i < _queue.Count; i++) {
			var controller = Players.GetAll().FirstOrDefault(candidate => KeyOf(candidate) == _queue[i]);
			Reply(caller, $"  #{i + 1} in line: {controller?.PlayerName ?? "(loading)"} {_queue[i]}");
		}
		foreach (var controller in Players.GetAll().Where(c => IsForcedSpectator(KeyOf(c))))
			Reply(caller, $"  spectating as admin: {controller.PlayerName}");
		Reply(caller, $"  {Players.GetAll().Count()}/{GlobalVars.MaxClients} connected, {_admins.ReservedSlots} reserved for admins");
	}

	/// <summary>The debug commands are for admins, in their game console, and for the server console.</summary>
	private void RequireAdmin(CCitadelPlayerController? caller) {
		if (caller != null && !IsAdmin(caller)) throw new CommandException("That's for admins.");
	}

	/// <summary>A debug command's answer, in the console of whoever ran it and in the server log.</summary>
	private void Reply(CCitadelPlayerController? caller, string text) {
		caller?.PrintToConsole(text);
		Console.WriteLine($"[{Name}] {text}");
	}

	private static int Unlocked(CCitadelPlayerPawn pawn) =>
		new[] { EAbilitySlot.Signature1, EAbilitySlot.Signature2, EAbilitySlot.Signature3, EAbilitySlot.Signature4 }
			.Count(slot => pawn.AbilityComponent.GetAbilityBySlot(slot)?.IsUnlocked == true);

	[Command("am_kick", Description = "Kick a player as the AFK kick would: dw_am_kick <slot>", ConsoleOnly = true)]
	public void CmdKick(CCitadelPlayerController? caller, int slot) {
		RequireAdmin(caller);
		if (Players.FromSlot(slot) is not { } controller) {
			Reply(caller, $"nobody in slot {slot}");
			return;
		}
		Reply(caller, $"kicking {controller.PlayerName} (slot {slot})");
		controller.Kick(ENetworkDisconnectionReason.NetworkDisconnectKickedIdle);
	}

	[Command("am_heroes", Description = "List the pickable heroes and their portraits", ConsoleOnly = true)]
	public void CmdHeroes(CCitadelPlayerController? caller) {
		RequireAdmin(caller);
		foreach (var info in HeroPool.All)
			Reply(caller, $"  {info.Hero,-12} {info.Name,-14} bot {(info.BotPlayable ? "yes" : "no "),-3} {info.Image}");
		Reply(caller, $"{HeroPool.All.Count} pickable heroes");
	}

	private int _botCount;

	[Command("am_bots", Description = "Add fake clients: dw_am_bots 3", ConsoleOnly = true)]
	public void CmdBots(CCitadelPlayerController? caller, int count = 1) {
		RequireAdmin(caller);
		// A new client can land in the slot of a player's parked hero and inherit it, which is for players only. A
		// stand-in's slot stays occupied, so heroes parked on stand-ins are safe.
		if (_vacancies.Keys.Any(slot => !_standIns.ContainsKey(slot))) {
			Reply(caller, $"a player's hero is parked; no bots until it's taken over or the match ends");
			return;
		}
		// Bots stay out of the slots reserved for admins.
		int room = PublicSlots - Players.GetAll().Count();
		if (count > room) {
			Reply(caller, $"room for {Math.Max(0, room)} more outside the {_admins.ReservedSlots} reserved for admins");
			count = Math.Max(0, room);
		}
		for (int i = 0; i < count; i++) {
			// Bots are told apart by name, so never reuse one that is here or due back after a map reload.
			string name;
			do name = $"Bot {++_botCount}";
			while (_botNames.ContainsValue(name) || Players.GetAll().Any(controller => controller.PlayerName == name));
			if (Server.CreateFakeClient(name) < 0) {
				Reply(caller, $"no free slot for a bot");
				return;
			}
		}
	}

	[Command("am_takeover", Description = "Hand a seated player's hero to someone else, mid-match as when a leaver's hero is " +
		"taken over: dw_am_takeover <to slot> <from slot>. The one handing it over goes to the back of the line.",
		ConsoleOnly = true)]
	public void CmdTakeover(CCitadelPlayerController? caller, int to, int from) {
		RequireAdmin(caller);
		if (to == from || Players.FromSlot(to) is not { } taker || Players.FromSlot(from) is not { } giver
		    || SeatInSlot(from) is not { } seat || giver.GetHeroPawn() == null) {
			Reply(caller, $"needs a seated player with a hero in slot {from} and someone else in slot {to}");
			return;
		}
		if (!ClaimVacancy(taker, new Vacancy(from, seat.SteamId, seat.Team, seat.Hero))) return;
		_queue.Add(KeyOf(giver));
		Spectate(giver);
		PromoteWaiting();
		Refresh();
	}

	// ─── Board and HUD content ─────────────────────────────────────────────

	private void Refresh() {
		var model = _phase == Phase.Warmup ? WarmupModel() : MatchLive ? LatePickModel() : PreGameModel();
		_board.Refresh(model, SeatInSlot);

		foreach (var slot in _hud) {
			string line;
			if (SeatInSlot(slot) != null) {
				line = _phase == Phase.Warmup ? model.Status + "   -   !hero to pick" : model.Status;
			} else if (Players.FromSlot(slot) is { } controller && IsForcedSpectator(KeyOf(controller))) {
				line = "Spectating as admin";
			} else {
				int place = Players.FromSlot(slot) is { } viewer ? _queue.IndexOf(KeyOf(viewer)) + 1 : 0;
				line = place > 0 ? $"Spectating   -   #{place} in line" : "Spectating";
			}
			DraftBoard.SetHud(RecipientFilter.Single(slot), line);
		}
	}

	private BoardModel WarmupModel() {
		int here = _seats.Values.Count(seat => seat.Connected);
		// Bots count as ready, as they do for starting.
		int ready = _seats.Values.Count(seat => seat.Connected && (seat.Ready || seat.Bot));
		bool full = here >= Config.PlayersToStart;
		var status = _matchFound != null
			? $"Match found   -   heroes locked in   -   starting in {_matchFoundLeft}"
			: full
				? $"Next match in {Clock(_warmupLeft)}"
				: $"Warmup   -   {here}/{Config.PlayersToStart} players" + (ready > 0 ? $"   -   {ready} ready" : "");
		var hint = _matchFound != null
			? "Heroes are locked in. Everyone gets a different hero, from their own picks where they can."
			: $"Pick at least {Config.MinHeroPicks} heroes: click once to pick, again for priority, again for high priority, " +
			  "again to clear. You warm up as your top pick. At the start everyone gets a different hero from their picks, top " +
			  "priorities first; ties are random, and anyone whose picks all clash gets a random hero.";
		return new BoardModel(TeamSeats(Amber), TeamSeats(Sapphire), TeamSize, status, hint,
			CanChangeSide: _matchFound == null, CanReady: !full && _matchFound == null,
			ShowPicks: true, CanPick: _matchFound == null,
			ReadyNote: $"Starts the match early, with fewer than {Config.PlayersToStart} players, once everyone is ready",
			MinPicks: Config.MinHeroPicks, Feedback: Feedback);
	}

	private BoardModel PreGameModel() {
		var status = GameRules.GameState == EGameState.PreGameWait
			? "Heroes locked in   -   zipline launch when the timer runs out"
			: $"Loading in   -   {_seats.Values.Count(seat => seat.Connected)}/{_seats.Count}";
		return new BoardModel(TeamSeats(Amber), TeamSeats(Sapphire), TeamSize, status, "Heroes are locked in for this match.",
			CanChangeSide: false, CanReady: false, CanPick: false, Feedback: Feedback);
	}

	private (string Name, string Invite) Feedback => (Config.FeedbackDiscordName, Config.FeedbackDiscord);

	private BoardModel LatePickModel() => new(TeamSeats(Amber), TeamSeats(Sapphire), TeamSize,
		"Joining the match in progress",
		$"Pick a hero. You'll spawn with souls to match your team. A hero is picked for you in {Config.LateJoinPickSeconds} seconds.",
		CanChangeSide: false, CanReady: false, Feedback: Feedback);

	private void ShowHud(CCitadelPlayerController controller) {
		if (controller.IsBot || !_hud.Add(controller.Slot)) return;
		DraftBoard.ShowHud(controller.Recipients, "");
	}

	private void HideHud(int slot) {
		if (_hud.Remove(slot)) DraftBoard.HideHud(RecipientFilter.Single(slot));
	}

	private static void Announce(string title, string description) =>
		NetMessages.Send(new CCitadelUserMsg_HudGameAnnouncement {
			TitleLocstring = title,
			DescriptionLocstring = description,
		}, RecipientFilter.All);

	// ─── Roster helpers ────────────────────────────────────────────────────

	/// <summary>
	/// Seats are keyed by SteamID. Fake clients all report 0, so each is keyed by its name instead, which carries over
	/// when <see cref="RestoreBots"/> brings it back after a map reload. Its slot may not.
	/// </summary>
	private static ulong KeyOf(CCitadelPlayerController controller) =>
		controller.PlayerSteamId != 0 ? controller.PlayerSteamId : BotKey(controller.PlayerName);

	private static ulong BotKey(string name) {
		uint hash = 2166136261; // FNV-1a
		foreach (char c in name) hash = (hash ^ c) * 16777619;
		return 0xB070_0000_0000_0000UL | hash;
	}

	private Seat? SeatOf(CCitadelPlayerController controller) =>
		_seats.GetValueOrDefault(KeyOf(controller)) is { } seat && seat.Slot == controller.Slot ? seat : null;

	private Seat? SeatInSlot(int slot) => _seats.Values.FirstOrDefault(seat => seat.Slot == slot);

	/// <summary>A team's seats, including ones held for players still loading back in.</summary>
	private List<Seat> TeamSeats(int team) => _seats.Values
		.Where(seat => seat.Team == team)
		.OrderBy(seat => seat.Name, StringComparer.OrdinalIgnoreCase)
		.ToList();

	private int TeamCount(int team) => _seats.Values.Count(seat => seat.Team == team);

	/// <summary>Someone other than <paramref name="except"/> already has the hero. Each hero is in a match once.</summary>
	private bool IsTaken(Heroes hero, Seat? except) => _seats.Values.Any(seat => seat != except && seat.Hero == hero);

	/// <summary><paramref name="preferred"/> if it has room, else the emptier team with room, else 0.</summary>
	private int OpenTeam(int preferred) {
		if (preferred is Amber or Sapphire && TeamCount(preferred) < TeamSize) return preferred;
		int amber = TeamCount(Amber), sapphire = TeamCount(Sapphire);
		if (amber >= TeamSize && sapphire >= TeamSize) return 0;
		if (amber >= TeamSize) return Sapphire;
		if (sapphire >= TeamSize) return Amber;
		return amber <= sapphire ? Amber : Sapphire;
	}

	private static string TeamName(int team) => team == Amber ? "The Hidden King" : team == Sapphire ? "The Archmother" : "Spectators";

	/// <summary>Reads a team from what a player typed: amber, hidden king, sapphire, archmother...</summary>
	private static int ParseTeam(string text) {
		text = text.ToLowerInvariant();
		if (text.Contains("sapph") || text.Contains("arch") || text.Contains("mother")) return Sapphire;
		if (text.Contains("amber") || text.Contains("hidden") || text.Contains("king")) return Amber;
		return 0;
	}

	private static string Clock(float seconds) {
		int s = Math.Max(0, (int)Math.Ceiling(seconds));
		return $"{s / 60}:{s % 60:00}";
	}

	private void SetConVar(string name, string value) {
		if (ConVar.Find(name) is not { } convar) {
			Console.WriteLine($"[{Name}] convar {name} not found");
			return;
		}
		_changedConVars.Add(name);
		convar.SetString(value);
	}

	/// <summary>
	/// Back to the game's own default. Not whatever value was there first: after a plugin reload during warmup,
	/// that would be warmup's.
	/// </summary>
	private void RestoreConVar(string name) {
		if (_conVarDefaults == null) {
			_conVarDefaults = new();
			foreach (var entry in Server.EnumerateConVars())
				_conVarDefaults.TryAdd(entry.Name, entry.DefaultValue);
		}
		if (_conVarDefaults.TryGetValue(name, out var value))
			ConVar.Find(name)?.SetString(value);
	}
}
