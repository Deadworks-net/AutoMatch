using DeadworksManaged.Api.UI;

namespace AutoMatch;

/// <summary>
/// Deadlock's own palette and faces (citadel_base_styles.css), shared by the plugin's panels. Inline styles can't see
/// its @define names, so these are the values behind them.
/// </summary>
internal static class Theme {
	public const string OffWhite = "#FFEFD7";
	public const string OffBlack = "#10130D";
	public const string Faded = "#FFEFD799";
	public const string Gold = "#FFED79";
	public const string Blurple = "#5865F2"; // Discord's brand colour, for Discord links

	public const string Block = "VALVEPulp";      // @block: headings
	public const string Serif = "Reaver";         // @serif: button faces
	public const string Sans = "Retail Demo";     // @sans: body text

	/// <summary>A dark card with the game's hairline border, for a panel's main body.</summary>
	public static (string, string)[] Card => [
		("background-color", $"gradient( linear, 0% 0%, 0% 100%, from( #1C1D17F2 ), to( {OffBlack}F2 ) )"),
		("border", $"1px solid {OffWhite}26"),
		("border-radius", "3px"),
		("box-shadow", "0px 8px 32px 0px #000000CC"),
	];

	/// <summary>The game's primary button: an off-white slab with dark serif lettering.</summary>
	public static UIButton SolidButton(string id, string text, string evt, params string[] args) =>
		UI.Button(id, text)
			.OnClick(evt, args)
			.WithStyles(("padding", "8px 22px"), ("background-color", OffWhite), ("border-radius", "3px"),
				("vertical-align", "center"))
			.WithTransition("background-color", "0.12s")
			.WithHoverStyles(("background-color", "#FFFFFF"))
			.WithPressStyles(("background-color", Gold))
			.WithTextStyles(("font-family", Serif), ("font-size", "22px"), ("color", OffBlack),
				("horizontal-align", "center"));

	/// <summary>A quieter outlined button for secondary actions.</summary>
	public static UIButton GhostButton(string id, string text, string evt, params string[] args) =>
		UI.Button(id, text)
			.OnClick(evt, args)
			.WithStyles(("padding", "6px 18px"), ("background-color", "#00000055"), ("border", $"1px solid {OffWhite}55"),
				("border-radius", "3px"), ("vertical-align", "center"))
			.WithTransition("background-color", "0.12s")
			.WithHoverStyles(("background-color", $"{OffWhite}22"), ("border", $"1px solid {OffWhite}"))
			.WithPressStyles(("background-color", $"{OffWhite}44"))
			.WithTextStyles(("font-family", Serif), ("font-size", "17px"), ("color", OffWhite),
				("horizontal-align", "center"));
}
