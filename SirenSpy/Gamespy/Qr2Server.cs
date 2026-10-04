using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SirenSpy.Gamespy
{
	// GameSpy master server, UDP 27900:
	//  - availability check  "09 00 00 00 00 <gamename>\0"         -> FE FD 09 00 00 00 00
	//  - QR2 heartbeat       "03 <instance key> key\0value\0 ..."  -> FE FD 01 <instance key> <challenge>
	//  - challenge response  "01 <instance key> <response>"        -> FE FD 0A <instance key> (registered)
	//  - keepalive           "08 <instance key>"
	//  - client message ack  "07 <instance key> <message key>"
	// Registered hosts are listed by the server browser (TCP 28910), which can also ask us to forward a message
	// (NAT negotiation cookie) to a host: FE FD 06 <instance key> <message key> <message>.
	public class Qr2Server : BackgroundService
	{
		public static int Port = 27900; // overridable for tests

		private readonly ServerRegistry _registry;
		private readonly UdpClient _udp;

		public Qr2Server(ServerRegistry registry)
		{
			_registry = registry;
			_udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
			Console.WriteLine($"UDP Server listening on port {Port}...");
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				UdpReceiveResult packet;
				try
				{
					packet = await _udp.ReceiveAsync(stoppingToken);
				}
				catch (OperationCanceledException) { break; }
				catch (SocketException) { continue; } // ICMP port unreachable from a previous reply

				try
				{
					Handle(packet.Buffer, packet.RemoteEndPoint);
				}
				catch (Exception ex)
				{
					Siren.Log($"[QR2] Error handling packet from {packet.RemoteEndPoint}: {ex.Message}", ConsoleColor.Red);
				}
			}
		}

		private void Handle(byte[] data, IPEndPoint from)
		{
			if (data.Length == 0) return;

			switch (data[0])
			{
				case 0x03 when data.Length >= 5:
					Heartbeat(data, from);
					break;

				case 0x01 when data.Length >= 5: // challenge response
					Send(0x0A, data, from);
					if (_registry.Get(from) is { } s) Siren.Log($"[QR2] Host registered: {s}", ConsoleColor.Green);
					break;

				case 0x08: // keepalive
					_registry.Touch(from);
					break;

				case 0x07: // client message ack
					Siren.Log($"[QR2] {from} acknowledged client message", ConsoleColor.DarkGray);
					break;

				case 0x09: // availability check
					var gameName = ReadString(data, 5, out _);
					Siren.Log($"[QR2] Availability check for '{gameName}' from {from}", ConsoleColor.DarkGray);
					_udp.Send(new byte[] { 0xFE, 0xFD, 0x09, 0x00, 0x00, 0x00, 0x00 }, 7, from);
					break;

				default:
					Siren.Log($"[QR2] Unknown packet 0x{data[0]:X2} from {from}: {Convert.ToHexString(data)}", ConsoleColor.Yellow);
					break;
			}
		}

		private void Heartbeat(byte[] data, IPEndPoint from)
		{
			// key\0value\0 pairs until an empty key; player/team sections follow and aren't needed for listing
			var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int pos = 5;
			while (pos < data.Length)
			{
				var key = ReadString(data, pos, out pos);
				if (key.Length == 0) break;
				keys[key] = ReadString(data, pos, out pos);
			}

			// statechanged: 3 = new server, 1 = info changed, 2 = shutting down
			if (keys.TryGetValue("statechanged", out var state) && state == "2")
			{
				_registry.Remove(from);
				return;
			}

			var existing = _registry.Get(from);
			var server = new GameServer
			{
				GameName = keys.TryGetValue("gamename", out var g) ? g : existing?.GameName ?? "",
				InstanceKey = data[1..5],
				EndPoint = from,
				Keys = keys,
			};
			_registry.Upsert(server);
			Siren.Log($"[QR2] Heartbeat: {server}", ConsoleColor.Cyan);

			// Challenge = 6 random chars + "00" + public IP (8 hex) + public port (4 hex); the QR2 SDK reads its public
			// address from it, then answers with gs_encrypt(secret key, challenge)
			const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
			var random = new string(Enumerable.Range(0, 6).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
			var ip = Convert.ToHexString(from.Address.MapToIPv4().GetAddressBytes());
			Send(0x01, data, from, Encoding.ASCII.GetBytes($"{random}00{ip}{from.Port:X4}\0"));
		}

		// Forwards a server browser "send message" (e.g. a NAT negotiation cookie) to a registered host
		public void SendClientMessage(GameServer server, byte[] message)
		{
			var payload = new byte[4 + message.Length];
			RandomNumberGenerator.Fill(payload.AsSpan(0, 4)); // message key, echoed back in the ack
			message.CopyTo(payload, 4);
			var packet = new List<byte> { 0xFE, 0xFD, 0x06 };
			packet.AddRange(server.InstanceKey);
			packet.AddRange(payload);
			_udp.Send(packet.ToArray(), packet.Count, server.EndPoint);
			Siren.Log($"[QR2] Forwarded {message.Length} byte client message to {server.EndPoint}", ConsoleColor.Cyan);
		}

		// Master -> host packets: FE FD <type> <instance key copied from the host's packet> [payload]
		private void Send(byte type, byte[] request, IPEndPoint to, byte[]? payload = null)
		{
			var packet = new List<byte> { 0xFE, 0xFD, type };
			packet.AddRange(request.AsSpan(1, 4).ToArray());
			if (payload != null) packet.AddRange(payload);
			_udp.Send(packet.ToArray(), packet.Count, to);
		}

		private static string ReadString(byte[] data, int start, out int next)
		{
			int end = Array.IndexOf(data, (byte)0, start);
			if (end < 0) end = data.Length;
			next = end + 1;
			return start >= data.Length ? "" : Encoding.ASCII.GetString(data, start, end - start);
		}

		public override void Dispose()
		{
			_udp.Dispose();
			base.Dispose();
		}
	}
}
