using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SirenSpy.Gamespy
{
	// GameSpy identities, one per PSN/RPCN account. The PS3 login sends the NP ticket; we give every account a stable
	// userid/profileid so Sake records, ATLAS sessions and matchmaking can tell players apart.
	public static class GameSpyPlayers
	{
		public class Player
		{
			public ulong AccountId { get; set; }
			public string OnlineId { get; set; } = "";
			public int UserId { get; set; }
			public int ProfileId { get; set; }
		}

		private class StoreData
		{
			public int NextId { get; set; } = 100000;
			public List<Player> Players { get; set; } = new();
		}

		// Used when a login can't be matched to a ticket (e.g. the server restarted between the two auth calls).
		// These were the ids the server originally hardcoded, so existing saves stay reachable.
		public static readonly Player Fallback = new() { UserId = 11111, ProfileId = 22222, OnlineId = "Jackalus" };

		private static readonly string StorePath = Path.Combine(AppContext.BaseDirectory, "gamespy_players.json");
		private static readonly object Lock = new();
		private static readonly Dictionary<string, Player> Tokens = new();
		private static StoreData? _data;

		private static StoreData Data
		{
			get
			{
				if (_data == null)
				{
					try { _data = File.Exists(StorePath) ? JsonSerializer.Deserialize<StoreData>(File.ReadAllText(StorePath)) : null; }
					catch (Exception ex) { Siren.Log($"[GameSpy] Failed to load {StorePath}: {ex.Message}", ConsoleColor.Red); }
					_data ??= new StoreData();
				}
				return _data;
			}
		}

		// LoginPs3CertWithGameId: identify the player from the NP ticket and hand out an auth token for it
		public static (Player player, string token) LoginWithTicket(string ticketBase64)
		{
			var (accountId, onlineId) = ParseTicket(ticketBase64);
			lock (Lock)
			{
				Player player;
				if (accountId == 0)
				{
					player = Fallback;
				}
				else
				{
					player = Data.Players.FirstOrDefault(p => p.AccountId == accountId)!;
					if (player == null)
					{
						bool first = Data.Players.Count == 0;
						player = new Player { AccountId = accountId, OnlineId = onlineId };
						if (first)
						{
							// The first account keeps the old shared ids so its existing profile/saves carry over
							player.UserId = Fallback.UserId;
							player.ProfileId = Fallback.ProfileId;
						}
						else
						{
							player.UserId = Data.NextId++;
							player.ProfileId = Data.NextId++;
						}
						Data.Players.Add(player);
						Siren.Log($"[GameSpy] New player {onlineId} (account {accountId}) -> profile {player.ProfileId}", ConsoleColor.Green);
					}
					if (onlineId.Length > 0) player.OnlineId = onlineId;
					File.WriteAllText(StorePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
				}

				var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
				Tokens[token] = player;
				return (player, token);
			}
		}

		// LoginRemoteAuthWithGameId: the game sends back the token we issued
		public static Player ByToken(string token)
		{
			lock (Lock) return Tokens.TryGetValue(token, out var p) ? p : Fallback;
		}

		public static Player? ByProfileId(int profileId)
		{
			lock (Lock) return Data.Players.FirstOrDefault(p => p.ProfileId == profileId);
		}

		// NP ticket: [u32 version][u32 size] then a section [u16 type][u16 len] of items [u16 type][u16 len][data].
		// Item types: 1 = u32, 2 = u64 (the account id), 4 = binary (online id is the 0x20 byte one), 7 = time, 8 = string.
		public static (ulong accountId, string onlineId) ParseTicket(string ticketBase64)
		{
			try
			{
				var t = Convert.FromBase64String(ticketBase64.Trim());
				int pos = 8 + 4; // header + section header
				ulong accountId = 0;
				string onlineId = "";
				while (pos + 4 <= t.Length)
				{
					int type = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(pos));
					int len = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(pos + 2));
					pos += 4;
					if (pos + len > t.Length) break;
					if (type == 2 && len == 8 && accountId == 0)
						accountId = BinaryPrimitives.ReadUInt64BigEndian(t.AsSpan(pos));
					else if (type == 4 && len == 0x20 && onlineId.Length == 0)
						onlineId = Encoding.ASCII.GetString(t, pos, len).TrimEnd('\0');
					if (type == 0x3002) break; // signature section
					pos += len;
				}
				return (accountId, onlineId);
			}
			catch (Exception ex)
			{
				Siren.Log($"[GameSpy] Could not parse NP ticket: {ex.Message}", ConsoleColor.Yellow);
				return (0, "");
			}
		}
	}
}
