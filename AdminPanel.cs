using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;
using static AutoMatch.Theme;

namespace AutoMatch;

/// <summary>What one admin sees on the panel.</summary>
internal sealed record AdminModel(
	bool ForcedSpectator,
	bool IsOwner,
	int ReservedSlots,
	int MaxClients,
	IReadOnlyList<(ulong SteamId, string Name, bool Online, bool Owner)> Admins,
	IReadOnlyList<(ulong SteamId, string Name)> Players,
	// The restart button was clicked once and is waiting for the confirming click.
	bool RestartArmed = false);

/// <summary>
/// The admin panel, opened with /admin: whether you're playing or spectating, and for owners, who else is an admin
/// and how many slots are held back for admins.
/// </summary>
internal sealed class AdminPanel {
	public const string PanelId = "am_admin";

	public const string EvSpectate = "spec";
	public const string EvSlots = "slots";
	public const string EvPromote = "promote";
	public const string EvDemote = "demote";
	public const string EvClose = "close";
	public const string EvRestart = "restart";

	private readonly HashSet<int> _open = new();

	public IEnumerable<int> OpenSlots => _open;

	public void Open(CCitadelPlayerController viewer, AdminModel model) {
		_open.Add(viewer.Slot);
		Show(viewer.Slot, model);
	}

	/// <summary>Rebuild an open panel; its lists change length, so it's redrawn whole.</summary>
	public void Show(int slot, AdminModel model) {
		if (!_open.Contains(slot)) return;
		var panel = UI.Panel(PanelId);
		panel.BuildLayout(RecipientFilter.Single(slot), Layout(model));
		panel.RequestCursor(RecipientFilter.Single(slot));
	}

	public void Close(int slot) {
		if (_open.Remove(slot))
			UI.Panel(PanelId).DestroyLayout(RecipientFilter.Single(slot));
	}

	/// <summary>Forget a viewer who left; the channel drops their panels itself.</summary>
	public void Forget(int slot) => _open.Remove(slot);

	// ─── Layout ────────────────────────────────────────────────────────────

	private static UINode Layout(AdminModel model) {
		var card = UI.Vertical("card").WithStyles(("horizontal-align", "center"), ("vertical-align", "center"),
			("width", "760px"), ("padding", "18px 22px")).WithStyles(Card);

		card.Add(UI.Horizontal("header").WithStyle("width", "100%").Add(
			UI.Label("title", "ADMIN").WithStyles(("font-family", Block), ("font-size", "32px"), ("color", OffWhite),
				("letter-spacing", "2px"), ("vertical-align", "center"), ("width", "fill-parent-flow(1.0)")),
			GhostButton("close", "CLOSE", EvClose)));
		card.Add(UI.Container("rule").WithStyles(("width", "100%"), ("height", "1px"), ("margin-top", "10px"),
			("background-color", $"{OffWhite}33")));

		card.Add(Section("you", "YOU"));
		card.Add(Row("youRow",
			model.ForcedSpectator
				? "Spectating. You won't be given a place, in warmup or a match, until you choose to play."
				: "Playing. You take a place like anyone else, or wait in line for one.",
			model.ForcedSpectator ? SolidButton("spec", "PLAY", EvSpectate, "0") : GhostButton("spec", "SPECTATE", EvSpectate, "1")));

		card.Add(Section("match", "MATCH"));
		card.Add(Row("restartRow",
			model.RestartArmed
				? "Click again to end any match in progress and send everyone back to warmup."
				: "Send everyone back to warmup now, ending any match in progress.",
			model.RestartArmed
				? SolidButton("restart", "CONFIRM", EvRestart)
				: GhostButton("restart", "RESTART TO WARMUP", EvRestart)));

		card.Add(Section("slots", "RESERVED SLOTS"));
		var slotsRow = Row("slotsRow",
			$"{model.ReservedSlots} of {model.MaxClients} slots held back for admins, {model.MaxClients - model.ReservedSlots} for everyone else.");
		if (model.IsOwner)
			slotsRow.Add(
				GhostButton("slotsDown", "-", EvSlots, "-1").WithStyle("margin-left", "8px"),
				GhostButton("slotsUp", "+", EvSlots, "1").WithStyle("margin-left", "6px"));
		card.Add(slotsRow);

		card.Add(Section("admins", "ADMINS"));
		foreach (var (steamId, name, online, owner) in model.Admins) {
			var label = $"{(name.Length > 0 ? name : steamId.ToString())}{(owner ? "   (owner)" : "")}{(online ? "" : "   (offline)")}";
			card.Add(model.IsOwner && !owner
				? Row($"admin{steamId}", label, GhostButton($"demote{steamId}", "REMOVE", EvDemote, steamId.ToString()))
				: Row($"admin{steamId}", label));
		}

		if (model.IsOwner) {
			card.Add(Section("players", "PLAYERS"));
			if (model.Players.Count == 0)
				card.Add(Row("nobody", "Nobody else is on."));
			foreach (var (steamId, name) in model.Players)
				card.Add(Row($"player{steamId}", name,
					GhostButton($"promote{steamId}", "MAKE ADMIN", EvPromote, steamId.ToString())));
		}

		return UI.Container("root")
			.WithStyles(("width", "100%"), ("height", "100%"), ("background-color", $"{OffBlack}99"))
			.Add(card);
	}

	private static UINode Section(string id, string title) =>
		UI.Label(id + "Head", title).WithStyles(("font-family", Block), ("font-size", "18px"), ("color", Gold),
			("letter-spacing", "1px"), ("margin-top", "16px"), ("margin-bottom", "6px"));

	private static UIContainer Row(string id, string text, params UINode[] buttons) {
		var row = UI.Horizontal(id).WithStyles(("width", "100%"), ("padding", "6px 8px"), ("margin-bottom", "4px"),
			("background-color", "#00000040"), ("border-left", $"2px solid {OffWhite}55"));
		row.Add(UI.Label(id + "Text", text).WithStyles(("font-family", Sans), ("font-size", "16px"), ("color", OffWhite),
			("vertical-align", "center"), ("width", "fill-parent-flow(1.0)")));
		foreach (var button in buttons)
			row.Add(button);
		return row;
	}
}
