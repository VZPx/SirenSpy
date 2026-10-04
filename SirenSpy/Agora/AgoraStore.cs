using System.Text.Json;

namespace SirenSpy.Agora
{
	// Very small persistent store for Agora data. Hydra values are kept in their wire form (base64) so whatever the
	// game uploads is echoed back with the exact same types.
	public class AgoraStore
	{
		public class ProfileRecord
		{
			public long Guid { get; set; }
			public string PlatformAccountId { get; set; } = "";
			public string Name { get; set; } = "";
			public string DisplayName { get; set; } = "";
			public string Data { get; set; } = ""; // base64 hydra HashMap uploaded by the game

			public HMap GetData() => Data.Length == 0 ? new HMap() : (Hydra.Deserialize(Convert.FromBase64String(Data)).FirstOrDefault() as HMap ?? new HMap());
			public void SetData(HMap map) => Data = Convert.ToBase64String(Hydra.Serialize(map));
		}

		public class ChannelRecord
		{
			public long Guid { get; set; }
			public long Owner { get; set; }
			public string Name { get; set; } = "";
		}

		public class ItemRecord
		{
			public long Guid { get; set; }
			public long Channel { get; set; }
			public string Type { get; set; } = "";
			public uint Created { get; set; }
			public string Output { get; set; } = "";         // base64 hydra value uploaded by the game (add_item)
			public JsonElement? OutputJson { get; set; }      // or hand-written output, converted with JsonToHydra

			public HValue GetOutput()
			{
				if (Output.Length > 0) return Hydra.Deserialize(Convert.FromBase64String(Output)).FirstOrDefault() ?? new HMap();
				if (OutputJson is JsonElement json) return AgoraService.JsonToHydra(json);
				return new HMap();
			}
			public void SetOutput(HValue v) => Output = Convert.ToBase64String(Hydra.Serialize(v));
		}

		public class ClanRecord
		{
			public long Guid { get; set; }
			public string Name { get; set; } = "";
			public string Tag { get; set; } = ""; // shown in game, max 4 chars
			public List<long> Members { get; set; } = new(); // profile guids
		}

		public class ChallengeRecord
		{
			public long Guid { get; set; }
			public JsonElement Attributes { get; set; } // converted with AgoraService.JsonToHydra
		}

		public class StoreData
		{
			public long NextGuid { get; set; } = 1000;
			public List<ProfileRecord> Profiles { get; set; } = new();
			public List<ChannelRecord> Channels { get; set; } = new();
			public List<ItemRecord> Items { get; set; } = new();
			public List<ClanRecord> Clans { get; set; } = new();
			public List<ChallengeRecord> Challenges { get; set; } = new();
		}

		private readonly string _path;
		private readonly object _lock = new();
		public StoreData Data { get; private set; } = new();

		public AgoraStore(string path)
		{
			_path = path;
			try
			{
				if (File.Exists(_path))
				{
					Data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_path)) ?? new StoreData();
					return;
				}
			}
			catch (Exception ex)
			{
				Siren.Log($"[Agora] Failed to load {_path}: {ex.Message}", ConsoleColor.Red);
				return; // don't overwrite a file we couldn't read
			}

			Mutate(Seed);
			Siren.Log($"[Agora] Created {_path}", ConsoleColor.Green);
		}

		// Default content for the system channels the game reads (owners are hardcoded in the EBOOT).
		private void Seed(StoreData d)
		{
			void AddChannel(long owner, string name, string type, string outputJson)
			{
				var ch = new ChannelRecord { Guid = NewGuid(d), Owner = owner, Name = name };
				d.Channels.Add(ch);
				d.Items.Add(new ItemRecord
				{
					Guid = NewGuid(d),
					Channel = ch.Guid,
					Type = type,
					Created = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
					OutputJson = JsonDocument.Parse(outputJson).RootElement.Clone(),
				});
			}

			// Global news (owner 1, also 2/7/8). Text is looked up as "<region>-<lang>" then "<lang>".
			AddChannel(1, "news", "news", """{ "en": "Welcome to SirenSpy! Gotham City Impostors is back online." }""");

			// Turf Warz standings (owner 6): output["Rep_<ReputationType>"] read as a number.
			AddChannel(6, "turf_warz", "turf_warz", """
				{ "Rep_Docks": 0, "Rep_AceChemical": 0, "Rep_CrimeAlley": 0, "Rep_AmusementMile": 0, "Rep_GothamPower": 0, "Rep_Freelance": 0 }
				""");
		}

		public T Locked<T>(Func<StoreData, T> fn)
		{
			lock (_lock) return fn(Data);
		}

		public void Mutate(Action<StoreData> fn)
		{
			lock (_lock)
			{
				fn(Data);
				File.WriteAllText(_path, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
			}
		}

		public long NewGuid(StoreData d) => d.NextGuid++;
	}
}
