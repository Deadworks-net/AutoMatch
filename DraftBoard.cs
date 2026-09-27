using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;
using static AutoMatch.Theme;

namespace AutoMatch;

/// <summary>What the board shows, rebuilt by the plugin whenever something on it changes.</summary>
internal sealed record BoardModel(
	IReadOnlyList<Seat> Amber,
	IReadOnlyList<Seat> Sapphire,
	int TeamSize,
	string Status,
	string Hint,
	bool CanChangeSide,
	bool CanReady,
	// Tiles show the viewer's picks (warmup) rather than who has which hero (joining mid-match).
	bool ShowPicks = false,
	// Clicking a tile does something; false once heroes are locked in.
	bool CanPick = true,
	// Shown beside the ready button, to say what readying does.
	string ReadyNote = "",
	// Heroes a player has to pick before they can ready up; 0 for no minimum.
	int MinPicks = 0,
	// The server's own Discord to ask for feedback in, beside the Deadworks one; an empty invite for none.
	(string Name, string Invite) Feedback = default);

/// <summary>
/// The full-screen hero picker: both rosters either side of a hero grid, with side, ready and close buttons. Each
/// viewer gets their own copy, because which heroes are taken depends on the viewer's team.
/// </summary>
internal sealed class DraftBoard {
	public const string PanelId = "am_draft";

	public const string EvHero = "hero";
	public const string EvSide = "side";
	public const string EvReady = "ready";
	public const string EvClose = "close";

	// Team colours; the shared palette and faces are in Theme.
	private const string AmberColor = "#D4860B";
	private const string AmberBright = "#FBDCA0";
	private const string AmberDarker = "#6F3806";
	private const string SapphireColor = "#4D75C3";
	private const string SapphireBright = "#BDCBFF";
	private const string SapphireDarker = "#243265";

	// A pick's tier, in the game's own roster colours and icons (citadel_hero_card.css).
	private const string SelectedColor = "#C3DCD2";
	private const string PriorityColor = "#92EAC7";
	private const string HighPriorityColor = "#33E59F";
	private const string PriorityIcon = "s2r://panorama/images/main_menu/pick_screen/priority_png.vtex";
	private const string HighPriorityIcon = "s2r://panorama/images/main_menu/pick_screen/priority_high_png.vtex";
	private const string CheckIcon = "s2r://panorama/images/icons/icon_checkmark.vsvg";

	private enum Tile { Free, Taken, Mine, Selected, Priority, HighPriority }

	private readonly HashSet<int> _open = new();
	private readonly Dictionary<int, Dictionary<Heroes, Tile>> _tiles = new();

	public bool IsOpen(int slot) => _open.Contains(slot);

	public IEnumerable<int> OpenSlots => _open;

	/// <summary>Show the board to <paramref name="viewer"/> and free their cursor to use it.</summary>
	public void Open(CCitadelPlayerController viewer, Seat? seat, BoardModel model, int countdownSeconds = 0) {
		var panel = UI.Panel(PanelId);
		panel.BuildLayout(viewer.Recipients, Layout(model, countdownSeconds > 0));
		panel.RequestCursor(viewer.Recipients);
		if (countdownSeconds > 0)
			panel.Animate(viewer.Recipients, "barFill", "width", "0%", $"{countdownSeconds}s");

		_open.Add(viewer.Slot);
		// A fresh tree shows every tile as free, so only the others need patching.
		_tiles[viewer.Slot] = HeroPool.All.ToDictionary(info => info.Hero, _ => Tile.Free);
		Push(viewer.Slot, seat, model);
	}

	public void Close(int slot) {
		if (!_open.Remove(slot)) return;
		_tiles.Remove(slot);
		UI.Panel(PanelId).DestroyLayout(RecipientFilter.Single(slot));
	}

	public void CloseAll() {
		foreach (var slot in _open.ToList())
			Close(slot);
	}

	/// <summary>Forget a disconnected viewer; the channel drops their panels itself.</summary>
	public void Forget(int slot) {
		_open.Remove(slot);
		_tiles.Remove(slot);
	}

	/// <summary>Bring every open board up to date. <paramref name="seatOf"/> maps a viewer's slot to their seat.</summary>
	public void Refresh(BoardModel model, Func<int, Seat?> seatOf) {
		foreach (var slot in _open)
			Push(slot, seatOf(slot), model);
	}

	public void SetTimer(string text) {
		foreach (var slot in _open)
			UI.Panel(PanelId).Set(RecipientFilter.Single(slot), "timer", text);
	}

	private void Push(int slot, Seat? seat, BoardModel model) {
		var update = UI.Panel(PanelId).Build()
			.Set("status", model.Status)
			.Set("hint", model.Hint)
			.Set("amberHead", $"THE HIDDEN KING   {model.Amber.Count}/{model.TeamSize}")
			.Set("sapphireHead", $"THE ARCHMOTHER   {model.Sapphire.Count}/{model.TeamSize}");

		for (int i = 0; i < model.TeamSize; i++) {
			update.Set($"amber{i}", RowText(model.Amber, i, seat));
			update.Set($"sapphire{i}", RowText(model.Sapphire, i, seat));
		}

		// A player has to pick enough heroes before they can ready up.
		int picked = seat?.Picks.Count ?? 0;
		int missing = Math.Max(0, model.MinPicks - picked);
		update.Set("pickCount", !model.ShowPicks || seat == null ? ""
			: missing > 0 ? $"{picked} / {model.MinPicks} HEROES PICKED" : $"{picked} HEROES PICKED");
		update.SetStyle("pickCount", "color", missing > 0 ? OffWhite : Faded);

		bool canReady = model.CanReady && seat != null;
		bool ready = seat?.Ready == true;
		update.Set("readyNote", !canReady ? ""
				: missing > 0 ? $"Pick {missing} more {(missing == 1 ? "hero" : "heroes")} to ready up"
				: model.ReadyNote)
			.Set("readyLabel", ready ? "READY" : "READY UP")
			.SetStyle("ready", "background-color", ready ? Gold : OffWhite)
			.SetStyle("ready", "visibility", canReady && missing == 0 ? "visible" : "collapse");

		SideButton(update, "joinAmber", 2, model.Amber.Count, model, seat);
		SideButton(update, "joinSapphire", 3, model.Sapphire.Count, model, seat);

		var tiles = _tiles[slot];
		foreach (var info in HeroPool.All) {
			var state = TileFor(info.Hero, seat, model);
			if (tiles.TryGetValue(info.Hero, out var sent) && sent == state) continue;
			tiles[info.Hero] = state;
			int id = (int)info.Hero;
			update.SetStyle($"h{id}", "opacity", state == Tile.Taken ? "0.25" : "1");
			update.SetStyle($"h{id}", "border", $"2px solid {BorderOf(state)}");
			update.SetStyle($"hk{id}", "visibility", state == Tile.Selected ? "visible" : "collapse");
			update.SetStyle($"hs{id}", "visibility", state is Tile.Priority or Tile.HighPriority ? "visible" : "collapse");
			if (state is Tile.Priority or Tile.HighPriority)
				update.SetStyle($"hs{id}", "background-image",
					$"url(\"{(state == Tile.Priority ? PriorityIcon : HighPriorityIcon)}\")");
		}

		update.SendTo(RecipientFilter.Single(slot));
	}

	private static void SideButton(UIUpdate update, string id, int team, int count, BoardModel model, Seat? seat) {
		bool mine = seat?.Team == team;
		bool full = count >= model.TeamSize;
		update.Set(id + "Label", mine ? "YOUR TEAM" : full ? "FULL" : "JOIN");
		update.SetStyle(id, "visibility", model.CanChangeSide && seat != null ? "visible" : "collapse");
		update.SetStyle(id, "opacity", mine || full ? "0.4" : "1");
	}

	private static string BorderOf(Tile state) => state switch {
		Tile.Mine => Gold,
		Tile.Selected => SelectedColor,
		Tile.Priority => PriorityColor,
		Tile.HighPriority => HighPriorityColor,
		_ => "#00000000",
	};

	private static Tile TileFor(Heroes hero, Seat? seat, BoardModel model) {
		if (seat == null) return Tile.Free;
		if (model.ShowPicks)
			return seat.Picks.GetValueOrDefault(hero) switch {
				HeroPick.HighPriority => Tile.HighPriority,
				HeroPick.Priority => Tile.Priority,
				HeroPick.Selected => Tile.Selected,
				_ => Tile.Free,
			};
		if (seat.Hero == hero) return Tile.Mine;
		return model.Amber.Concat(model.Sapphire).Any(other => other != seat && other.Hero == hero) ? Tile.Taken : Tile.Free;
	}

	private static string RowText(IReadOnlyList<Seat> team, int index, Seat? viewer) {
		if (index >= team.Count) return "open";
		var seat = team[index];
		var you = seat == viewer ? " (you)" : "";
		var state = !seat.Connected ? "  -  loading" : seat.Ready ? "  -  ready" : "";
		return $"{HeroPool.NameOf(seat.Hero).ToUpperInvariant()}   {Trim(seat.Name, 16)}{you}{state}";
	}

	private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

	// ─── Layout ────────────────────────────────────────────────────────────

	private static UINode Layout(BoardModel model, bool withCountdown) {
		var board = UI.Vertical("board").WithStyles(
			("horizontal-align", "center"),
			("vertical-align", "center"),
			("width", "1400px"),
			("padding", "18px 22px"))
			.WithStyles(Theme.Card);

		board.Add(Header());
		board.Add(UI.Container("rule").WithStyles(("width", "100%"), ("height", "1px"), ("margin-top", "12px"),
			("background-color", $"{OffWhite}33")));
		if (withCountdown)
			board.Add(UI.Container("barTrack")
				.WithStyles(("width", "100%"), ("height", "4px"), ("margin-top", "8px"), ("background-color", $"{OffWhite}14"))
				.Add(UI.Container("barFill").WithStyles(("width", "100%"), ("height", "100%"), ("background-color", Gold))));

		board.Add(UI.Horizontal("body").WithStyles(("width", "100%"), ("margin-top", "16px")).Add(
			TeamColumn("amber", "THE HIDDEN KING", AmberColor, AmberBright, AmberDarker, 2, model.TeamSize),
			Grid(),
			TeamColumn("sapphire", "THE ARCHMOTHER", SapphireColor, SapphireBright, SapphireDarker, 3, model.TeamSize)));

		board.Add(UI.Horizontal("footer").WithStyles(("width", "100%"), ("margin-top", "14px")).Add(
			UI.Label("pickCount", "").WithStyles(("font-family", Block), ("font-size", "18px"), ("color", OffWhite),
				("letter-spacing", "1px"), ("margin-right", "18px"), ("vertical-align", "center")),
			UI.Label("hint", "").WithStyles(("font-family", Sans), ("font-size", "16px"), ("color", Faded),
				("vertical-align", "center"), ("width", "fill-parent-flow(1.0)")),
			GhostButton("close", "CLOSE", EvClose)));
		board.Add(DiscordCta(model.Feedback));

		return UI.Container("root")
			.WithStyles(("width", "100%"), ("height", "100%"), ("background-color", $"{OffBlack}B3"))
			.Add(board);
	}

	/// <summary>
	/// Where to give feedback, styled like the Discord note in the Deadworks server browser. Clicking a link copies it.
	/// </summary>
	private static UINode DiscordCta((string Name, string Invite) feedback) {
		UINode Text(string id, string text) => UI.Label(id, text).WithStyles(("font-family", Sans), ("font-size", "15px"),
			("color", Faded), ("vertical-align", "center"));
		UINode Link(string id, string text, string url) => UI.Button(id, text).CopyOnClick(url)
			.WithStyles(("padding", "1px 3px"), ("border-radius", "2px"), ("vertical-align", "center"))
			.WithHoverStyle("background-color", $"{Blurple}33")
			.WithPressStyle("background-color", $"{Blurple}66")
			.WithTextStyles(("font-family", Sans), ("font-size", "15px"), ("color", Blurple));
		var strip = UI.Horizontal("discord").WithStyles(("width", "100%"), ("margin-top", "10px"), ("padding", "8px 12px"),
			("background-color", $"{Blurple}14"), ("border", $"1px solid {Blurple}59"));
		if (feedback.Invite is { Length: > 0 } invite) {
			var name = feedback.Name.Length > 0 ? $"the {feedback.Name} Discord" : "our Discord";
			strip.Add(
				Text("discordPre", $"Consider joining {name} to give feedback: "),
				Link("discordServer", invite, invite),
				Text("discordMid", " and the Deadworks Discord at "));
		} else {
			strip.Add(Text("discordPre", "Consider joining the Deadworks Discord to give feedback: "));
		}
		return strip.Add(
			Link("discordDeadworks", "deadworks.net/discord", "https://deadworks.net/discord"),
			Text("discordNote", "   (click a link to copy it)"));
	}

	private static UINode Header() => UI.Horizontal("header").WithStyle("width", "100%").Add(
		UI.Label("title", "AUTOMATCH").WithStyles(("font-family", Block), ("font-size", "34px"), ("color", OffWhite),
			("letter-spacing", "2px"), ("vertical-align", "center")),
		UI.Label("status", "").WithStyles(("font-family", Sans), ("font-size", "19px"), ("color", Faded),
			("text-transform", "uppercase"), ("letter-spacing", "1px"), ("margin-left", "26px"),
			("vertical-align", "center"), ("width", "fill-parent-flow(1.0)")),
		UI.Label("timer", "").WithStyles(("font-family", Block), ("font-size", "30px"), ("color", Gold),
			("margin-right", "20px"), ("vertical-align", "center")),
		UI.Label("readyNote", "").WithStyles(("font-family", Sans), ("font-size", "15px"), ("color", Faded),
			("width", "220px"), ("text-align", "right"), ("margin-right", "14px"), ("vertical-align", "center")),
		SolidButton("ready", "READY UP", EvReady));

	private static UINode TeamColumn(string key, string title, string color, string bright, string darker, int team, int size) {
		var column = UI.Vertical(key + "Column").WithStyles(("width", "320px"), ("padding", "12px 14px"),
			("background-color", $"gradient( linear, 0% 0%, 0% 100%, from( {darker}66 ), to( {darker}14 ) )"),
			("border-top", $"3px solid {color}"), ("border-radius", "3px"));
		column.Add(UI.Label(key + "Head", title).WithStyles(("font-family", Block), ("font-size", "24px"),
			("color", bright), ("letter-spacing", "1px"), ("margin-bottom", "10px")));
		for (int i = 0; i < size; i++)
			column.Add(UI.Label($"{key}{i}", "").WithStyles(("font-family", Sans), ("font-size", "16px"),
				("color", OffWhite), ("width", "100%"), ("padding", "6px 8px"), ("margin-bottom", "4px"),
				("background-color", "#00000040"), ("border-left", $"2px solid {color}")));
		var joinId = "join" + char.ToUpperInvariant(key[0]) + key[1..];
		column.Add(GhostButton(joinId, "JOIN", EvSide, team.ToString()).WithStyles(("margin-top", "12px"),
			("horizontal-align", "center")));
		return column;
	}

	private static UINode Grid() {
		var grid = UI.Container("grid").WithStyles(("width", "fill-parent-flow(1.0)"), ("flow-children", "right-wrap"),
			("margin", "0px 14px"));
		foreach (var info in HeroPool.All)
			grid.Add(HeroTile(info));
		return grid;
	}

	private static UINode HeroTile(HeroInfo info) {
		var id = (int)info.Hero;
		return UI.Button($"h{id}", "")
			.OnClick(EvHero, id.ToString())
			.WithStyles(("width", "76px"), ("height", "98px"), ("margin", "3px"), ("flow-children", "down"),
				("background-color", $"{OffWhite}0D"), ("border", "2px solid #00000000"), ("border-radius", "3px"))
			.WithTransition("background-color", "0.1s")
			.WithHoverStyles(("background-color", $"{OffWhite}2E"))
			.WithPressStyles(("background-color", $"{Gold}40"))
			.Add(
				// The portrait with the pick's tier icon over its corner: a check for picked, a star for priority.
				UI.Container($"hc{id}").WithStyles(("width", "68px"), ("height", "68px"), ("horizontal-align", "center"),
					("margin-top", "3px")).Add(
					UI.Image($"hi{id}", info.Image).WithStyles(("width", "100%"), ("height", "100%")),
					UI.Container($"hk{id}").WithStyles(("width", "22px"), ("height", "22px"), ("horizontal-align", "right"),
						("vertical-align", "top"), ("background-image", $"url(\"{CheckIcon}\")"), ("background-size", "contain"),
						("background-repeat", "no-repeat"), ("wash-color", SelectedColor), ("visibility", "collapse")),
					UI.Container($"hs{id}").WithStyles(("width", "24px"), ("height", "29px"), ("horizontal-align", "right"),
						("vertical-align", "top"), ("background-size", "contain"), ("background-repeat", "no-repeat"),
						("background-position", "center"), ("visibility", "collapse"))),
				UI.Label($"hn{id}", info.Name.ToUpperInvariant()).WithStyles(("font-family", Sans), ("width", "100%"),
					("font-size", "12px"), ("color", OffWhite), ("text-align", "center"), ("margin-top", "4px")));
	}

	// ─── Status line ───────────────────────────────────────────────────────
	// A one-line banner while the board is closed, so the lobby's state and how to reopen the picker stay on screen.

	public const string HudId = "am_hud";

	public static void ShowHud(RecipientFilter to, string text) {
		UI.Panel(HudId).BuildLayout(to, UI.Container("hudRoot")
			.WithStyles(("horizontal-align", "center"), ("vertical-align", "top"), ("margin-top", "96px"),
				("padding", "6px 18px"), ("background-color", $"{OffBlack}D9"), ("border", $"1px solid {OffWhite}33"),
				("border-radius", "3px"))
			.Add(UI.Label("line", text).WithStyles(("font-family", Sans), ("font-size", "17px"), ("color", OffWhite),
				("text-transform", "uppercase"), ("letter-spacing", "1px"))));
	}

	public static void SetHud(RecipientFilter to, string text) => UI.Panel(HudId).Set(to, "line", text);

	public static void HideHud(RecipientFilter to) => UI.Panel(HudId).DestroyLayout(to);
}
