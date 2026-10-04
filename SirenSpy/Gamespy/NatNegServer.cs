using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SirenSpy.Gamespy
{
	// GameSpy NAT negotiation, UDP 27901 (natneg1/2/3.gamespy.com). A joining client and the host both send INIT
	// packets with the same cookie (delivered to the host through the server browser -> QR2 client message).
	// Once both sides checked in we send each a CONNECT with the other's address so they can punch through.
	//
	// Packet: [magic FD FC 1E 66 6A B2][version][type][cookie 4][payload]
	//   INIT (0):    [port type][client index][use game port][local ip 4][local port 2][gamename\0]
	//   CONNECT (5): [remote ip 4][remote port 2][got your data][finished (0 = no error)]
	public class NatNegServer : BackgroundService
	{
		public static int Port = 27901; // overridable for tests
		private static readonly byte[] Magic = { 0xFD, 0xFC, 0x1E, 0x66, 0x6A, 0xB2 };

		private const byte Init = 0, InitAck = 1, ErtTest = 2, ErtAck = 3, Connect = 5, ConnectAck = 6,
			AddressCheck = 10, AddressReply = 11, NatifyRequest = 12, Report = 13, ReportAck = 14,
			PreInit = 15, PreInitAck = 16;

		private class InitInfo
		{
			public required IPEndPoint Public;
			public required IPEndPoint Private;
			public bool UseGamePort;
			public DateTime Time = DateTime.UtcNow;
		}

		private class Negotiation
		{
			// [client index][port type] -> init (index 0 = joining client, 1 = host)
			public readonly Dictionary<(int index, int portType), InitInfo> Inits = new();
			public byte Version;
			public bool ConnectSent;
			public DateTime Created = DateTime.UtcNow;
		}

		private readonly ConcurrentDictionary<uint, Negotiation> _negotiations = new();
		private UdpClient? _udp;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			try
			{
				_udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
			}
			catch (SocketException ex)
			{
				Siren.Log($"[NatNeg] Can't listen on UDP {Port}: {ex.Message}", ConsoleColor.Red);
				return;
			}
			Siren.Log($"[NatNeg] Listening on UDP {Port}", ConsoleColor.Green);

			while (!stoppingToken.IsCancellationRequested)
			{
				UdpReceiveResult packet;
				try { packet = await _udp.ReceiveAsync(stoppingToken); }
				catch (OperationCanceledException) { break; }
				catch (SocketException) { continue; }

				try { Handle(packet.Buffer, packet.RemoteEndPoint); }
				catch (Exception ex) { Siren.Log($"[NatNeg] Error from {packet.RemoteEndPoint}: {ex.Message}", ConsoleColor.Red); }
			}
			_udp.Dispose();
		}

		private void Handle(byte[] p, IPEndPoint from)
		{
			if (p.Length < 12 || !p.AsSpan(0, 6).SequenceEqual(Magic)) return;

			byte version = p[6];
			byte type = p[7];
			uint cookie = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8));

			foreach (var old in _negotiations.Where(n => DateTime.UtcNow - n.Value.Created > TimeSpan.FromMinutes(2)).ToList())
				_negotiations.TryRemove(old.Key, out _);

			switch (type)
			{
				case Init when p.Length >= 21:
				{
					int portType = p[12], index = p[13];
					bool useGamePort = p[14] != 0;
					var local = new IPEndPoint(new IPAddress(p[15..19]), BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(19)));

					var neg = _negotiations.GetOrAdd(cookie, _ => new Negotiation());
					lock (neg)
					{
						neg.Version = version;
						neg.Inits[(index, portType)] = new InitInfo { Public = from, Private = local, UseGamePort = useGamePort };
					}
					Siren.Log($"[NatNeg] INIT cookie {cookie:X8} {(index == 1 ? "host" : "client")} port type {portType} from {from} (local {local})", ConsoleColor.Cyan);

					Reply(p, InitAck, from, CommonPayload(p[12], p[13], p[14], from));
					TryConnect(cookie, neg);
					break;
				}

				case AddressCheck:
				case NatifyRequest:
					// NAT type detection; answered from this single address
					Reply(p, type == AddressCheck ? AddressReply : ErtTest, from, CommonPayload(At(p, 12), At(p, 13), At(p, 14), from));
					break;

				case Report:
					Siren.Log($"[NatNeg] REPORT cookie {cookie:X8} from {from}: {(At(p, 14) == 1 ? "success" : "failed")}", At(p, 14) == 1 ? ConsoleColor.Green : ConsoleColor.Yellow);
					Reply(p, ReportAck, from, CommonPayload(At(p, 12), At(p, 13), At(p, 14), from));
					break;

				case PreInit:
					// [state][target cookie] -> ack as ready
					Reply(p, PreInitAck, from, new byte[] { 2 }.Concat(p.Length >= 17 ? p[13..17] : new byte[4]).ToArray());
					break;

				case ConnectAck:
				case ErtAck:
					break;

				default:
					Siren.Log($"[NatNeg] Unhandled packet type {type} from {from}: {Convert.ToHexString(p)}", ConsoleColor.Yellow);
					break;
			}
		}

		private void TryConnect(uint cookie, Negotiation neg)
		{
			InitInfo? client, host;
			lock (neg)
			{
				if (neg.ConnectSent) return;
				client = Primary(neg, 0);
				host = Primary(neg, 1);
				if (client == null || host == null) return;
				neg.ConnectSent = true;
			}

			// Give both sides a moment to finish sending their remaining INITs before telling them to connect
			_ = Task.Delay(500).ContinueWith(_ =>
			{
				SendConnect(cookie, neg.Version, to: client, peer: host);
				SendConnect(cookie, neg.Version, to: host, peer: client);
				Siren.Log($"[NatNeg] CONNECT cookie {cookie:X8}: client {client.Public} <-> host {host.Public}", ConsoleColor.Green);
			});
		}

		// The address used for the game connection: the game socket (port type 0) when "use game port" is set,
		// otherwise the first natneg socket (port type 1)
		private static InitInfo? Primary(Negotiation neg, int index)
		{
			var any = neg.Inits.Where(kv => kv.Key.index == index).Select(kv => kv.Value).FirstOrDefault();
			if (any == null) return null;
			int wanted = any.UseGamePort ? 0 : 1;
			return neg.Inits.TryGetValue((index, wanted), out var info) ? info
				: neg.Inits.Where(kv => kv.Key.index == index).OrderBy(kv => kv.Key.portType).First().Value;
		}

		private void SendConnect(uint cookie, byte version, InitInfo to, InitInfo peer)
		{
			// Both behind the same public IP (same PC / same LAN): hairpinning often fails, use the LAN address instead
			var target = peer.Public.Address.Equals(to.Public.Address) ? peer.Private : peer.Public;

			var p = new List<byte>(Magic) { version, Connect };
			var c = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(c, cookie);
			p.AddRange(c);
			p.AddRange(target.Address.MapToIPv4().GetAddressBytes());
			p.Add((byte)(target.Port >> 8)); p.Add((byte)target.Port);
			p.Add(1); // got your data
			p.Add(0); // finished: no error
			_udp?.Send(p.ToArray(), p.Count, to.Public);
		}

		// Reply with the request's header (version, cookie) and a new packet type
		private void Reply(byte[] request, byte type, IPEndPoint to, byte[] payload)
		{
			var p = new List<byte>(request[..12]);
			p[7] = type;
			p.AddRange(payload);
			_udp?.Send(p.ToArray(), p.Count, to);
		}

		// [port type][client index][use game port][public ip 4][public port 2]
		private static byte[] CommonPayload(byte portType, byte index, byte useGamePort, IPEndPoint from)
		{
			var b = new List<byte> { portType, index, useGamePort };
			b.AddRange(from.Address.MapToIPv4().GetAddressBytes());
			b.Add((byte)(from.Port >> 8)); b.Add((byte)from.Port);
			return b.ToArray();
		}

		private static byte At(byte[] p, int i) => i < p.Length ? p[i] : (byte)0;
	}
}
