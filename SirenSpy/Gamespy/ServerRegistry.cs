using System.Collections.Concurrent;
using System.Net;

namespace SirenSpy.Gamespy
{
	public class GameSpyOptions
	{
		// gamename -> secret key. Gotham City Impostors (PS3): "sirenps3" / "95GIS4", the key is built byte by
		// byte at 0x18c670 in the EBOOT and verified against the game's QR2 challenge responses.
		public Dictionary<string, string> Games { get; set; } = new();

		public string SecretKey(string gameName) =>
			Games.TryGetValue(gameName, out var key) ? key : "";
	}

	// A game host registered through QR2 (heartbeats on UDP 27900)
	public class GameServer
	{
		public string GameName { get; set; } = "";
		public byte[] InstanceKey { get; set; } = new byte[4];
		public IPEndPoint EndPoint { get; set; } = new(IPAddress.None, 0); // public address the heartbeats come from
		public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		public DateTime LastSeen { get; set; } = DateTime.UtcNow;

		public string Key(string name) => Keys.TryGetValue(name, out var v) ? v : "";
		public override string ToString() => $"{Key("hostname")} @ {EndPoint} ({Key("gametype")}/{Key("mapname")}, {Key("numplayers")}/{Key("maxplayers")})";
	}

	public class ServerRegistry
	{
		private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
		private readonly ConcurrentDictionary<string, GameServer> _servers = new();

		public event Action<GameServer>? Updated;
		public event Action<GameServer>? Removed;

		public void Upsert(GameServer server)
		{
			server.LastSeen = DateTime.UtcNow;
			_servers[server.EndPoint.ToString()] = server;
			Updated?.Invoke(server);
		}

		public void Touch(IPEndPoint endPoint)
		{
			if (_servers.TryGetValue(endPoint.ToString(), out var s)) s.LastSeen = DateTime.UtcNow;
		}

		public void Remove(IPEndPoint endPoint)
		{
			if (_servers.TryRemove(endPoint.ToString(), out var s))
			{
				Siren.Log($"[QR2] Server removed: {s}", ConsoleColor.DarkYellow);
				Removed?.Invoke(s);
			}
		}

		public GameServer? Get(IPEndPoint endPoint) => _servers.TryGetValue(endPoint.ToString(), out var s) ? s : null;

		// Server browser addresses a server by the ip/port it was listed with (public ip + public port)
		public GameServer? Find(IPAddress ip, int port) =>
			_servers.Values.FirstOrDefault(s => s.EndPoint.Address.Equals(ip) && s.EndPoint.Port == port)
			?? _servers.Values.FirstOrDefault(s => s.EndPoint.Address.Equals(ip));

		public List<GameServer> List(string gameName)
		{
			foreach (var s in _servers.Values.Where(s => DateTime.UtcNow - s.LastSeen > Timeout).ToList())
				Remove(s.EndPoint);
			return _servers.Values.Where(s => s.GameName.Equals(gameName, StringComparison.OrdinalIgnoreCase)).ToList();
		}
	}
}
