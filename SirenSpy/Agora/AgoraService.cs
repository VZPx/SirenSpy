using System.Text.Json;

namespace SirenSpy.Agora
{
	public record AgoraRequest(string Prefix, string Service, string Method, List<HValue> Args, string PsnTicket)
	{
		public HValue? Arg(int i) => i < Args.Count ? Args[i] : null;
		public long LongArg(int i, long fallback = 0)
		{
			try { return Arg(i)?.AsLong() ?? fallback; } catch { return fallback; }
		}
		public string StringArg(int i, string fallback = "") => Arg(i)?.AsString() ?? fallback;
	}

	// Implementation of the Agora services used by Gotham City Impostors.
	// Response shapes were reversed from the callbacks in the PS3 EBOOT (addresses in comments are callback code).
	public class AgoraService
	{
		private static readonly HValue[] Empty = Array.Empty<HValue>();

		private readonly AgoraStore _store;
		private readonly Dictionary<string, Func<AgoraRequest, HValue[]>> _handlers;

		public AgoraService()
		{
			_store = new AgoraStore(Path.Combine(AppContext.BaseDirectory, "agora_data.json"));

			_handlers = new(StringComparer.OrdinalIgnoreCase)
			{
				["profile/get_by_platform_account_id"] = ProfileGetByPlatformAccountId,
				["profile/create"] = ProfileCreate,
				["profile/update"] = ProfileUpdate,
				["profile/get"] = ProfileGet,
				["profile/get_by_name"] = ProfileGetByName,
				["profile/convert_platform_account_ids_to_guids"] = ProfileConvertPlatformAccountIds,

				["feed/get_channels_by_owner"] = FeedGetChannelsByOwner,
				["feed/create_channel"] = FeedCreateChannel,
				["feed/add_item"] = FeedAddItem,
				["feed/get_items_by_channel"] = FeedGetItemsByChannel,
				["feed/get_item_guids_by_channel"] = FeedGetItemGuidsByChannel,
				["feed/get_item"] = FeedGetItem,

				["clan/get_member"] = ClanGetMember,
				["clan/get"] = ClanGet,
				["clan/get_clans_for_members"] = ClanGetClansForMembers,

				["challenge/list_open_challenges"] = ChallengeListOpen,

				["match/create"] = _ => Empty,           // cb 0x935010 ignores the result
				["event_log/create_event"] = _ => Empty, // no callback (store purchase log)
			};
		}

		public HValue[] Dispatch(AgoraRequest req)
		{
			if (_handlers.TryGetValue($"{req.Service}/{req.Method}", out var handler))
				return handler(req);

			Siren.Log($"[Agora] Unimplemented: {req.Service}/{req.Method}", ConsoleColor.Yellow);
			return Empty;
		}

		#region profile

		// The game sends names like "TTV_Keroyz=d003ca44": the online id followed by a PS3 stack address that
		// changes every call, so only the part before '=' identifies the player.
		private static string NameKey(string name)
		{
			int eq = name.IndexOf('=');
			return eq >= 0 ? name[..eq] : name;
		}

		private AgoraStore.ProfileRecord? FindProfile(AgoraStore.StoreData d, string key)
		{
			if (string.IsNullOrEmpty(key)) return null;
			key = NameKey(key);
			return d.Profiles.FirstOrDefault(p => p.PlatformAccountId.Equals(key, StringComparison.OrdinalIgnoreCase))
				?? d.Profiles.FirstOrDefault(p => NameKey(p.Name).Equals(key, StringComparison.OrdinalIgnoreCase))
				?? d.Profiles.FirstOrDefault(p => p.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase));
		}

		// args: [UTF8 platform_account_id]
		// cb 0xc33fe8 / 0xc347e0: [ HashMap{ "guid": Int64, "turbine_issued_awards": HashMap } ]
		// An empty result makes the game call profile/create on its next save.
		private HValue[] ProfileGetByPlatformAccountId(AgoraRequest req)
		{
			var pacct = req.StringArg(0);
			var profile = _store.Locked(d => d.Profiles.FirstOrDefault(p => p.PlatformAccountId == pacct));
			if (profile == null)
			{
				Siren.Log($"[Agora] No profile for '{pacct}' yet, game will create one", ConsoleColor.Yellow);
				return Empty;
			}

			return new HValue[]
			{
				new HMap
				{
					{ "guid", profile.Guid },
					// item name -> count of rewards the player should own (granted by 0xc2aa50)
					{ "turbine_issued_awards", new HMap() },
				}
			};
		}

		// args: [HashMap data]  (platform_account_id, console_id_in_wbid, name, display_name + every profile variable)
		// cb 0xc2a79c: [ Int64 guid ]
		private HValue[] ProfileCreate(AgoraRequest req)
		{
			var data = req.Arg(0) as HMap ?? new HMap();
			var pacct = data["platform_account_id"]?.AsString() ?? "";

			long guid = 0;
			_store.Mutate(d =>
			{
				// The game only creates when the lookup failed, but don't duplicate a profile we already know.
				var profile = d.Profiles.FirstOrDefault(p => pacct.Length > 0 && p.PlatformAccountId == pacct);
				if (profile == null)
				{
					profile = new AgoraStore.ProfileRecord { Guid = _store.NewGuid(d) };
					d.Profiles.Add(profile);
				}
				ApplyProfileData(profile, data, merge: false);
				guid = profile.Guid;
			});

			Siren.Log($"[Agora] Created profile {guid} for '{pacct}'", ConsoleColor.Green);
			return new HValue[] { new HInt64(guid) };
		}

		// args: [UTF8 guid ("%lld"), HashMap data] - no callback
		private HValue[] ProfileUpdate(AgoraRequest req)
		{
			var guid = req.LongArg(0);
			var data = req.Arg(1) as HMap;
			if (data == null) return Empty;

			_store.Mutate(d =>
			{
				var profile = d.Profiles.FirstOrDefault(p => p.Guid == guid);
				if (profile == null)
				{
					// Unknown guid (e.g. the data file was wiped) - recreate it under the guid the game has cached.
					profile = new AgoraStore.ProfileRecord { Guid = guid };
					d.Profiles.Add(profile);
					d.NextGuid = Math.Max(d.NextGuid, guid + 1);
				}
				ApplyProfileData(profile, data, merge: true);
			});
			return Empty;
		}

		private static void ApplyProfileData(AgoraStore.ProfileRecord profile, HMap data, bool merge)
		{
			var stored = merge ? profile.GetData() : new HMap();
			foreach (var kv in data.Entries)
			{
				if (kv.Key is HUtf8 key) stored[key.Value] = kv.Value;
				else stored.Add(kv.Key, kv.Value);
			}
			profile.SetData(stored);

			if (data["platform_account_id"] is HUtf8 pacct && pacct.Value.Length > 0) profile.PlatformAccountId = pacct.Value;
			if (data["name"] is HUtf8 name && name.Value.Length > 0) profile.Name = name.Value;
			if (data["display_name"] is HUtf8 dn && dn.Value.Length > 0) profile.DisplayName = dn.Value;
		}

		// args: [Int64 guid]
		// cb 0xc30324: [ HashMap ] - reads back the keys uploaded by create/update (calling cards, stats, showcases...)
		// Always return a HashMap: on anything else the game enqueues clan/get_member with an uninitialised guid.
		private HValue[] ProfileGet(AgoraRequest req)
		{
			var guid = req.LongArg(0);
			var profile = _store.Locked(d => d.Profiles.FirstOrDefault(p => p.Guid == guid));

			var map = profile?.GetData() ?? new HMap();
			map["guid"] = new HInt64(guid);
			return new HValue[] { map };
		}

		// args: [UTF8 name]
		// cb 0xc31af8 / 0xc338bc: [ HashMap{ "guid": Int64 } ], empty when not found
		private HValue[] ProfileGetByName(AgoraRequest req)
		{
			var profile = _store.Locked(d => FindProfile(d, req.StringArg(0)));
			if (profile == null) return Empty;
			return new HValue[] { new HMap { { "guid", profile.Guid } } };
		}

		// args: [Array[UTF8 platform_account_id...]]
		// cb 0xc2fe80: [ HashMap{ <pacct UTF8>: Int64 guid } ]  (a HashMap, even empty, lets the clan follow-up run)
		private HValue[] ProfileConvertPlatformAccountIds(AgoraRequest req)
		{
			var ids = req.Args.SelectMany(a => a is HArray arr ? arr.Items : new List<HValue> { a })
				.Select(a => a.AsString()).ToList();

			var result = new HMap();
			_store.Locked(d =>
			{
				foreach (var id in ids)
				{
					var p = d.Profiles.FirstOrDefault(p => p.PlatformAccountId == id);
					if (p != null) result.Add(id, new HInt64(p.Guid));
				}
				return 0;
			});
			return new HValue[] { result };
		}

		#endregion

		#region feed

		// Channel owners used by the game:
		//   profile guid  - player's friend feed ("profile.<guid>", written by the game via add_item)
		//   clan guid     - gang news
		//   1, 2, 7, 8    - global news slots
		//   6             - Turf Warz standings
		//   9..95         - legal docs per country (EULA/TOS/PP). Returning nothing makes the game use its bundled text.
		//   TitleStorageChannelOwner - title storage (base64 game content), only if EnableTitleStorage is set
		private const int MaxItemsPerChannel = 100;

		private static HMap ItemMap(AgoraStore.ItemRecord item) => new()
		{
			{ "guid", item.Guid },
			{ "channel_guid", item.Channel },
			{ "type", item.Type },
			{ "created_at", new HDateTime(item.Created) },
			{ "output", item.GetOutput() },
		};

		// Items newest first, paged. The game always sends (page, count) = (1, 50) or (1, 1).
		private static IEnumerable<AgoraStore.ItemRecord> PageItems(AgoraStore.StoreData d, AgoraRequest req)
		{
			var channel = req.LongArg(0);
			var page = Math.Max(1, (int)req.LongArg(1, 1));
			var count = Math.Clamp((int)req.LongArg(2, MaxItemsPerChannel), 0, MaxItemsPerChannel);
			return d.Items.Where(i => i.Channel == channel)
				.OrderByDescending(i => i.Created).ThenByDescending(i => i.Guid)
				.Skip((page - 1) * count).Take(count).ToList();
		}

		// args: [Int64 owner]
		// every cb reads results[0] as Array and uses element 0 as the Int64 channel guid
		private HValue[] FeedGetChannelsByOwner(AgoraRequest req)
		{
			var owner = req.LongArg(0);
			var guids = _store.Locked(d => d.Channels.Where(c => c.Owner == owner).Select(c => c.Guid).ToList());
			return new HValue[] { new HArray(guids.Select(g => (HValue)new HInt64(g))) };
		}

		// args: [UTF8 name ("profile.<guid>"), Int64 owner]
		// cb 0xc282f0 ignores the result and then calls add_item
		private HValue[] FeedCreateChannel(AgoraRequest req)
		{
			var name = req.StringArg(0);
			var owner = req.LongArg(1);
			long guid = 0;
			_store.Mutate(d =>
			{
				var ch = d.Channels.FirstOrDefault(c => c.Name == name && c.Owner == owner);
				if (ch == null)
				{
					ch = new AgoraStore.ChannelRecord { Guid = _store.NewGuid(d), Owner = owner, Name = name };
					d.Channels.Add(ch);
				}
				guid = ch.Guid;
			});
			return new HValue[] { new HInt64(guid) };
		}

		// args: [UTF8 channel name, UTF8 type, HashMap output] - no callback
		// friend feed: type "friend_message", output { msg, playername, param1, param2 }
		private HValue[] FeedAddItem(AgoraRequest req)
		{
			var channelArg = req.Arg(0);
			var type = req.StringArg(1);
			var output = req.Arg(2) ?? new HMap();

			_store.Mutate(d =>
			{
				var ch = channelArg is HInt64 or HInt32
					? d.Channels.FirstOrDefault(c => c.Guid == channelArg.AsLong())
					: d.Channels.FirstOrDefault(c => c.Name == channelArg?.AsString());

				if (ch == null)
				{
					// add_item can arrive without create_channel when the game already cached the channel guid
					var name = channelArg?.AsString() ?? "";
					long owner = name.StartsWith("profile.") && long.TryParse(name["profile.".Length..], out var o) ? o : 0;
					ch = new AgoraStore.ChannelRecord { Guid = _store.NewGuid(d), Owner = owner, Name = name };
					d.Channels.Add(ch);
				}

				var item = new AgoraStore.ItemRecord
				{
					Guid = _store.NewGuid(d),
					Channel = ch.Guid,
					Type = type,
					Created = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
				};
				item.SetOutput(output);
				d.Items.Add(item);

				// keep channels bounded
				var old = d.Items.Where(i => i.Channel == ch.Guid).OrderByDescending(i => i.Created).ThenByDescending(i => i.Guid)
					.Skip(MaxItemsPerChannel).ToHashSet();
				d.Items.RemoveAll(old.Contains);
			});
			return Empty;
		}

		// args: [Int64 channel, Int32 page, Int32 count]
		// cb 0xc2e02c (news), 0xc2addc (turf warz), 0xdc4ef8 (title storage): [ Array[ HashMap{ "type", "output" } ] ]
		private HValue[] FeedGetItemsByChannel(AgoraRequest req)
		{
			var items = _store.Locked(d => PageItems(d, req).ToList());
			return new HValue[] { new HArray(items.Select(i => (HValue)ItemMap(i))) };
		}

		// args: [Int64 channel, Int32 page, Int32 count]
		// cb 0xc285a0: [ Array[ Int64 item_guid ] ]
		private HValue[] FeedGetItemGuidsByChannel(AgoraRequest req)
		{
			var items = _store.Locked(d => PageItems(d, req).ToList());
			return new HValue[] { new HArray(items.Select(i => (HValue)new HInt64(i.Guid))) };
		}

		// args: [Int64 item_guid]
		// cb 0xc28534: [ HashMap item ] (not wrapped in an array) - legal docs read item["output"]["en"]
		private HValue[] FeedGetItem(AgoraRequest req)
		{
			var guid = req.LongArg(0);
			var item = _store.Locked(d => d.Items.FirstOrDefault(i => i.Guid == guid));
			return item == null ? Empty : new HValue[] { ItemMap(item) };
		}

		#endregion

		#region clan

		// The game has no clan creation flow over Agora (add_profile_to_clan is unreferenced), so clans live in
		// agora_data.json ("Clans") and can be added by hand.
		private AgoraStore.ClanRecord? ClanOf(AgoraStore.StoreData d, long profileGuid) =>
			d.Clans.FirstOrDefault(c => c.Members.Contains(profileGuid));

		private static HMap ClanMap(AgoraStore.ClanRecord clan) => new()
		{
			{ "guid", clan.Guid },
			{ "name", clan.Name },
			{ "attributes", new HMap { { "tag", clan.Tag } } },
		};

		// args: [Int64 profile_guid]
		// cb 0xc33ebc / 0xc3019c: [ HashMap{ "clan_guid": Int64 } ]; no clan => HashMap without clan_guid
		private HValue[] ClanGetMember(AgoraRequest req)
		{
			var guid = req.LongArg(0);
			var clan = _store.Locked(d => ClanOf(d, guid));
			var map = new HMap { { "profile_guid", guid } };
			if (clan != null) map.Add("clan_guid", clan.Guid);
			return new HValue[] { map };
		}

		// args: [Int64 clan_guid]
		// cb 0xc2b810 / 0xc2bd84: [ HashMap{ "attributes": HashMap{ "tag": UTF8 (max 4 chars) } } ]
		private HValue[] ClanGet(AgoraRequest req)
		{
			var guid = req.LongArg(0);
			var clan = _store.Locked(d => d.Clans.FirstOrDefault(c => c.Guid == guid));
			return clan == null ? Empty : new HValue[] { ClanMap(clan) };
		}

		// args: [Array[Int64 profile_guid...]]
		// cb 0xc2b140: [ HashMap{ Int64 profile_guid: HashMap{ "attributes": { "tag": UTF8 } } } ]  (Int64 keys!)
		private HValue[] ClanGetClansForMembers(AgoraRequest req)
		{
			var guids = req.Args.SelectMany(a => a is HArray arr ? arr.Items : new List<HValue> { a })
				.Select(a => { try { return a.AsLong(); } catch { return 0L; } }).ToList();

			var result = new HMap();
			_store.Locked(d =>
			{
				foreach (var g in guids)
				{
					var clan = ClanOf(d, g);
					if (clan != null) result.Add(new HInt64(g), ClanMap(clan));
				}
				return 0;
			});
			return new HValue[] { result };
		}

		#endregion

		#region challenge

		// args: [1, 16]
		// cb 0xde5efc: [ Array[ HashMap{ "guid": Int64, "attributes": HashMap{...} } ] ]
		// Community challenges come from agora_data.json ("Challenges"), see README for the attribute format.
		private HValue[] ChallengeListOpen(AgoraRequest req)
		{
			var list = new HArray();
			_store.Locked(d =>
			{
				foreach (var c in d.Challenges.Take(16))
				{
					list.Add(new HMap
					{
						{ "guid", c.Guid },
						{ "attributes", JsonToHydra(c.Attributes) },
					});
				}
				return 0;
			});
			return new HValue[] { list };
		}

		#endregion

		// Converts hand-written JSON into hydra values: objects -> HashMap, arrays -> Array, strings -> UTF8,
		// integers -> Int32 (Int64 if they don't fit), other numbers -> Float64, booleans -> Bool.
		public static HValue JsonToHydra(JsonElement e)
		{
			switch (e.ValueKind)
			{
				case JsonValueKind.Object:
					var map = new HMap();
					foreach (var p in e.EnumerateObject()) map.Add(p.Name, JsonToHydra(p.Value));
					return map;
				case JsonValueKind.Array:
					return new HArray(e.EnumerateArray().Select(JsonToHydra));
				case JsonValueKind.String:
					return new HUtf8(e.GetString() ?? "");
				case JsonValueKind.Number:
					if (e.TryGetInt32(out var i)) return new HInt32(i);
					if (e.TryGetInt64(out var l)) return new HInt64(l);
					return new HFloat64(e.GetDouble());
				case JsonValueKind.True: return new HBool(true);
				case JsonValueKind.False: return new HBool(false);
				default: return HNone.Instance;
			}
		}
	}
}
