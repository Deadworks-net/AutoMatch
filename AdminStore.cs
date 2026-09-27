using System.Text.Json;

namespace AutoMatch;

/// <summary>
/// Who the server's admins are and how many slots are held back for them. Owners come from the plugin's config;
/// everything else is set from the admin panel and kept in a file beside it.
/// </summary>
internal sealed class AdminStore {
	public const int MaxReservedSlots = 10;

	private sealed class Saved {
		public int ReservedSlots { get; set; } = 1;
		// SteamID to the name they last played under, for showing admins who aren't on.
		public Dictionary<ulong, string> Admins { get; set; } = new();
		// Admins who chose to play this session; the rest spectate. On disk so a plugin reload doesn't undo the choice.
		public HashSet<ulong> Playing { get; set; } = new();
	}

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	private readonly string? _path;
	// Read on every use, so a config reload takes effect straight away.
	private readonly Func<IReadOnlyCollection<ulong>> _owners;
	private Saved _saved = new();
	// Owners' names aren't saved, so they show once they've been on.
	private readonly Dictionary<ulong, string> _ownerNames = new();

	public AdminStore(string? path, Func<IReadOnlyCollection<ulong>> owners) {
		_path = path;
		_owners = owners;
		try {
			if (_path != null && File.Exists(_path))
				_saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path)) ?? new();
		} catch (Exception ex) {
			Console.WriteLine($"[AutoMatch] couldn't read {_path}: {ex.Message}");
		}
		_saved.ReservedSlots = Math.Clamp(_saved.ReservedSlots, 0, MaxReservedSlots);
	}

	public int ReservedSlots => _saved.ReservedSlots;

	public bool IsOwner(ulong steamId) => _owners().Contains(steamId);

	public bool IsAdmin(ulong steamId) => IsOwner(steamId) || _saved.Admins.ContainsKey(steamId);

	/// <summary>The owners first, then the rest by name.</summary>
	public IEnumerable<(ulong SteamId, string Name)> All =>
		_owners().Select(owner => (owner, _ownerNames.GetValueOrDefault(owner, ""))).Concat(_saved.Admins
			.Where(admin => !IsOwner(admin.Key))
			.OrderBy(admin => admin.Value, StringComparer.OrdinalIgnoreCase)
			.Select(admin => (admin.Key, admin.Value)));

	/// <summary>Keep an admin's name current, for when they're shown offline.</summary>
	public void Seen(ulong steamId, string name) {
		if (IsOwner(steamId)) _ownerNames[steamId] = name;
		else if (_saved.Admins.TryGetValue(steamId, out var known) && known != name) {
			_saved.Admins[steamId] = name;
			Save();
		}
	}

	public void Add(ulong steamId, string name) {
		if (IsOwner(steamId) || !_saved.Admins.TryAdd(steamId, name)) return;
		Save();
	}

	public void Remove(ulong steamId) {
		if (_saved.Admins.Remove(steamId)) Save();
	}

	public bool WantsToPlay(ulong steamId) => _saved.Playing.Contains(steamId);

	public void SetPlaying(ulong steamId, bool playing) {
		if (playing ? _saved.Playing.Add(steamId) : _saved.Playing.Remove(steamId)) Save();
	}

	public void SetReservedSlots(int count) {
		count = Math.Clamp(count, 0, MaxReservedSlots);
		if (count == _saved.ReservedSlots) return;
		_saved.ReservedSlots = count;
		Save();
	}

	private void Save() {
		if (_path == null) return;
		try {
			File.WriteAllText(_path, JsonSerializer.Serialize(_saved, Json));
		} catch (Exception ex) {
			Console.WriteLine($"[AutoMatch] couldn't save {_path}: {ex.Message}");
		}
	}
}
