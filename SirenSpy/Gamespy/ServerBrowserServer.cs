using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SirenSpy.Gamespy
{
	// GameSpy server browser (ServerBrowsing SDK v2), TCP 28910 on <gamename>.ms<N>.gamespy.com.
	// The game asks for the list of hosts registered through QR2 (matchmaking / server list), asks for a single host's
	// full info, and sends NAT negotiation cookies to a host it wants to join (forwarded through QR2).
	public class ServerBrowserServer : BackgroundService
	{
		public static int Port = 28910; // overridable for tests
		private const int DefaultQueryPort = 6500;

		// list request options (sb_internal.h)
		private const uint SendFieldsForAll = 1, NoServerList = 2, PushUpdates = 4, AlternateSourceIp = 8,
			SendGroups = 32, LimitResultCount = 128;

		// server entry flags
		private const byte UnsolicitedUdpFlag = 1, PrivateIpFlag = 2, ConnectNegotiateFlag = 4, IcmpIpFlag = 8,
			NonStandardPortFlag = 16, NonStandardPrivatePortFlag = 32, HasKeysFlag = 64, HasFullRulesFlag = 128;

		// ad-hoc messages server -> client
		private const byte PushServerMessage = 2, KeepAliveMessage = 3, DeleteServerMessage = 4;

		private readonly ServerRegistry _registry;
		private readonly Qr2Server _qr2;
		private readonly GameSpyOptions _options;
		private readonly ConcurrentDictionary<Session, byte> _sessions = new();

		public ServerBrowserServer(ServerRegistry registry, Qr2Server qr2, IOptions<GameSpyOptions> options)
		{
			_registry = registry;
			_qr2 = qr2;
			_options = options.Value;
			_registry.Updated += s => Push(s, deleted: false);
			_registry.Removed += s => Push(s, deleted: true);
		}

		private class Session
		{
			public required TcpClient Client;
			public required NetworkStream Stream;
			public GoaCrypt? Crypt;
			public string GameName = "";
			public uint Options;
			public readonly SemaphoreSlim WriteLock = new(1, 1);
			public IPEndPoint Remote => (IPEndPoint)Client.Client.RemoteEndPoint!;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			var listener = new TcpListener(IPAddress.Any, Port);
			try
			{
				listener.Start();
			}
			catch (SocketException ex)
			{
				Siren.Log($"[SB] Can't listen on TCP {Port}: {ex.Message}", ConsoleColor.Red);
				return;
			}
			Siren.Log($"[SB] Server browser listening on TCP {Port}", ConsoleColor.Green);

			try
			{
				while (!stoppingToken.IsCancellationRequested)
				{
					var client = await listener.AcceptTcpClientAsync(stoppingToken);
					_ = Task.Run(() => HandleClient(client, stoppingToken), stoppingToken);
				}
			}
			catch (OperationCanceledException) { }
			finally { listener.Stop(); }
		}

		private async Task HandleClient(TcpClient client, CancellationToken ct)
		{
			var session = new Session { Client = client, Stream = client.GetStream() };
			_sessions[session] = 0;
			var buffer = new List<byte>();
			var chunk = new byte[4096];
			try
			{
				while (!ct.IsCancellationRequested)
				{
					int n = await session.Stream.ReadAsync(chunk, ct);
					if (n == 0) break;
					buffer.AddRange(chunk.AsSpan(0, n).ToArray());

					// Requests: [u16 total length (big endian, includes itself)][type][payload]
					while (buffer.Count >= 3)
					{
						int len = BinaryPrimitives.ReadUInt16BigEndian(buffer.ToArray().AsSpan(0, 2));
						if (len < 3 || len > 65535) len = buffer.Count;  // be lenient with odd framing
						if (buffer.Count < len) break;
						var request = buffer.GetRange(0, len).ToArray();
						buffer.RemoveRange(0, len);
						await HandleRequest(session, request);
					}
				}
			}
			catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
			catch (Exception ex)
			{
				Siren.Log($"[SB] Error with {session.Remote}: {ex}", ConsoleColor.Red);
			}
			finally
			{
				_sessions.TryRemove(session, out _);
				client.Dispose();
			}
		}

		private async Task HandleRequest(Session session, byte[] request)
		{
			byte type = request[2];
			switch (type)
			{
				case 0x00:
					await ServerListRequest(session, request);
					break;

				case 0x01: // server info request: [ip 4][port 2]
				{
					var server = FindServer(request);
					if (server == null)
					{
						Siren.Log($"[SB] Info requested for unknown server", ConsoleColor.Yellow);
						return;
					}
					await SendAdHoc(session, BuildServerMessage(server, full: true));
					break;
				}

				case 0x02: // send message: [ip 4][port 2][message] -> forwarded to the host over QR2
				{
					var server = FindServer(request);
					var message = request[9..];
					if (server == null)
					{
						Siren.Log($"[SB] {session.Remote} sent a message to an unknown server {Address(request)}", ConsoleColor.Yellow);
						return;
					}
					Siren.Log($"[SB] {session.Remote} -> message for {server.Key("hostname")} ({Convert.ToHexString(message)})", ConsoleColor.Cyan);
					_qr2.SendClientMessage(server, message);
					break;
				}

				case 0x03: // keepalive reply
					break;

				default:
					Siren.Log($"[SB] Unhandled request 0x{type:X2} from {session.Remote}: {Convert.ToHexString(request)}", ConsoleColor.Yellow);
					break;
			}
		}

		// [len][0x00][request version][protocol version][encoding version][game version u32]
		// [for gamename\0][from gamename\0][client challenge 8][filter\0][\key1\key2...\0][options u32 BE]
		// [alternate source ip 4 if option 8][max results u32 if option 128]
		private async Task ServerListRequest(Session session, byte[] r)
		{
			int pos = 9;
			string forGame = ReadString(r, ref pos);
			string fromGame = ReadString(r, ref pos);
			byte[] clientChallenge = r[pos..(pos + 8)];
			pos += 8;
			string filter = ReadString(r, ref pos);
			var keys = ReadString(r, ref pos).Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
			uint options = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(pos));
			pos += 4;
			if ((options & AlternateSourceIp) != 0) pos += 4;
			int maxResults = int.MaxValue;
			if ((options & LimitResultCount) != 0 && pos + 4 <= r.Length)
				maxResults = (int)BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(pos));

			session.GameName = forGame;
			session.Options = options;

			var secretKey = _options.SecretKey(fromGame);
			if (secretKey.Length == 0) secretKey = _options.SecretKey(forGame);
			if (secretKey.Length == 0)
				Siren.Log($"[SB] No secret key configured for '{fromGame}', the game won't be able to read the list", ConsoleColor.Red);

			// Crypt header (sent in clear): [len ^ 0xEC][random][key len ^ 0xEA][server challenge]
			var serverChallenge = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(5)));
			var header = new List<byte> { 2 ^ 0xEC };
			header.AddRange(RandomNumberGenerator.GetBytes(2));
			header.Add((byte)(serverChallenge.Length ^ 0xEA));
			header.AddRange(serverChallenge);
			session.Crypt = GoaCrypt.ForServerList(clientChallenge, secretKey.Length > 0 ? secretKey : "x", serverChallenge);

			// Body: [client public ip][default query port] then keys, popular values, servers and the end marker
			var body = new List<byte>();
			body.AddRange(session.Remote.Address.MapToIPv4().GetAddressBytes());
			body.Add(DefaultQueryPort >> 8); body.Add(DefaultQueryPort & 0xFF);

			var servers = new List<GameServer>();
			if ((options & NoServerList) == 0)
			{
				servers = _registry.List(forGame)
					.Where(s => SbFilter.Matches(filter, s.Key))
					.Take(maxResults).ToList();

				body.Add((byte)keys.Count);
				foreach (var key in keys)
				{
					body.Add(0); // key type: string
					body.AddRange(Encoding.ASCII.GetBytes(key));
					body.Add(0);
				}
				body.Add(0); // no "popular values"

				foreach (var server in servers)
				{
					body.AddRange(ServerHeader(server, keys.Count > 0 ? HasKeysFlag : (byte)0));
					foreach (var key in keys)
					{
						body.Add(0xFF); // inline string (not a popular value index)
						body.AddRange(Encoding.UTF8.GetBytes(server.Key(key)));
						body.Add(0);
					}
				}
				body.AddRange(new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF }); // end of list
			}

			Siren.Log($"[SB] {session.Remote} list '{forGame}' filter=\"{filter}\" keys={string.Join(",", keys)} options=0x{options:X} -> {servers.Count} server(s)", ConsoleColor.Cyan);

			await session.WriteLock.WaitAsync();
			try
			{
				var packet = header.ToArray().Concat(session.Crypt.Encrypt(body.ToArray())).ToArray();
				await session.Stream.WriteAsync(packet);
			}
			finally { session.WriteLock.Release(); }
		}

		// [flags][public ip 4][public port 2 if non standard][private ip 4][private port 2]
		private static byte[] ServerHeader(GameServer server, byte flags)
		{
			var h = new List<byte> { 0 };
			h.AddRange(server.EndPoint.Address.MapToIPv4().GetAddressBytes());

			int port = server.EndPoint.Port;
			if (port != DefaultQueryPort)
			{
				flags |= NonStandardPortFlag;
				h.Add((byte)(port >> 8)); h.Add((byte)port);
			}

			// Peer hosts sit behind NAT: tell the game to NAT negotiate instead of contacting them directly
			if (server.Key("natneg") == "1") flags |= ConnectNegotiateFlag;

			if (IPAddress.TryParse(server.Key("localip0"), out var localIp))
			{
				flags |= PrivateIpFlag;
				h.AddRange(localIp.MapToIPv4().GetAddressBytes());
			}

			if (int.TryParse(server.Key("localport"), out var localPort) && localPort != DefaultQueryPort)
			{
				flags |= NonStandardPrivatePortFlag;
				h.Add((byte)(localPort >> 8)); h.Add((byte)localPort);
			}

			h[0] = flags;
			return h.ToArray();
		}

		// Ad-hoc message: [u16 len][type][...]. Full info = header with HAS_FULL_RULES then "key\0value\0" pairs.
		private static byte[] BuildServerMessage(GameServer server, bool full)
		{
			var m = new List<byte> { PushServerMessage };
			m.AddRange(ServerHeader(server, HasFullRulesFlag));
			foreach (var kv in server.Keys)
			{
				m.AddRange(Encoding.UTF8.GetBytes(kv.Key)); m.Add(0);
				m.AddRange(Encoding.UTF8.GetBytes(kv.Value)); m.Add(0);
			}
			return m.ToArray();
		}

		private async Task SendAdHoc(Session session, byte[] message)
		{
			if (session.Crypt == null) return; // a list request must come first to set up the cipher
			var framed = new byte[message.Length + 2];
			BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)framed.Length);
			message.CopyTo(framed, 2);

			await session.WriteLock.WaitAsync();
			try
			{
				await session.Stream.WriteAsync(session.Crypt.Encrypt(framed));
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
			finally { session.WriteLock.Release(); }
		}

		// Live updates for browsers that asked for them (PUSH_UPDATES)
		private void Push(GameServer server, bool deleted)
		{
			foreach (var session in _sessions.Keys)
			{
				if ((session.Options & PushUpdates) == 0 || session.Crypt == null) continue;
				if (!session.GameName.Equals(server.GameName, StringComparison.OrdinalIgnoreCase)) continue;

				byte[] message;
				if (deleted)
				{
					var m = new List<byte> { DeleteServerMessage };
					m.AddRange(server.EndPoint.Address.MapToIPv4().GetAddressBytes());
					m.Add((byte)(server.EndPoint.Port >> 8)); m.Add((byte)server.EndPoint.Port);
					message = m.ToArray();
				}
				else
				{
					message = BuildServerMessage(server, full: true);
				}
				_ = SendAdHoc(session, message);
			}
		}

		private GameServer? FindServer(byte[] r) =>
			r.Length >= 9 ? _registry.Find(new IPAddress(r[3..7]), BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(7))) : null;

		private static string Address(byte[] r) =>
			r.Length >= 9 ? $"{new IPAddress(r[3..7])}:{BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(7))}" : "?";

		private static string ReadString(byte[] data, ref int pos)
		{
			int end = Array.IndexOf(data, (byte)0, pos);
			if (end < 0) end = data.Length;
			var s = Encoding.ASCII.GetString(data, pos, end - pos);
			pos = end + 1;
			return s;
		}
	}
}
