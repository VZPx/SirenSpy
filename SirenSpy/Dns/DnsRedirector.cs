using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SirenSpy.Dns
{
	public class DnsOptions
	{
		public bool Enabled { get; set; } = true;
		public string ListenAddress { get; set; } = "127.0.0.1";
		public int Port { get; set; } = 53;
		public string Upstream { get; set; } = "8.8.8.8";
		public string RedirectTo { get; set; } = "127.0.0.1";
		// Matches the domain itself and every subdomain (*.gamespy.com). Left empty here because the config binder
		// appends to lists instead of replacing them; DefaultRedirect is used when appsettings has none.
		public List<string> Redirect { get; set; } = new();
		public static readonly string[] DefaultRedirect = { "gamespy.com", "gamespy.net", "agoragames.com" };
	}

	// Minimal DNS server so no external redirector (Acrylic DNS) is needed: set RPCS3's DNS to 127.0.0.1 and
	// lookups for the redirected domains resolve to RedirectTo, everything else is forwarded to Upstream.
	public class DnsRedirector : BackgroundService
	{
		private const ushort TypeA = 1;
		private const ushort ClassIN = 1;
		private const uint Ttl = 60;

		private readonly DnsOptions _options;
		private readonly IPEndPoint _upstream;
		private readonly byte[] _redirectIp;

		public DnsRedirector(IConfiguration config)
		{
			_options = config.GetSection("Dns").Get<DnsOptions>() ?? new DnsOptions();
			if (_options.Redirect.Count == 0) _options.Redirect.AddRange(DnsOptions.DefaultRedirect);
			_upstream = new IPEndPoint(IPAddress.Parse(_options.Upstream), 53);
			_redirectIp = IPAddress.Parse(_options.RedirectTo).GetAddressBytes();
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (!_options.Enabled) return;

			UdpClient server;
			try
			{
				server = new UdpClient(new IPEndPoint(IPAddress.Parse(_options.ListenAddress), _options.Port));
			}
			catch (SocketException ex)
			{
				Siren.Log($"[DNS] Can't listen on {_options.ListenAddress}:{_options.Port} ({ex.Message}). Is another DNS server running?", ConsoleColor.Red);
				return;
			}

			Siren.Log($"[DNS] Listening on {_options.ListenAddress}:{_options.Port}, redirecting {string.Join(", ", _options.Redirect.Select(d => "*." + d))} to {_options.RedirectTo}", ConsoleColor.Green);

			using (server)
			{
				while (!stoppingToken.IsCancellationRequested)
				{
					UdpReceiveResult query;
					try
					{
						query = await server.ReceiveAsync(stoppingToken);
					}
					catch (OperationCanceledException) { break; }
					catch (SocketException) { continue; } // e.g. ICMP port unreachable from a previous reply

					_ = Task.Run(() => HandleQuery(server, query, stoppingToken), stoppingToken);
				}
			}
		}

		private async Task HandleQuery(UdpClient server, UdpReceiveResult query, CancellationToken ct)
		{
			try
			{
				var packet = query.Buffer;
				byte[]? response = null;

				bool parsed = TryParseQuestion(packet, out var name, out var qtype, out var questionEnd);
				if (parsed && ShouldRedirect(name))
				{
					Siren.Log($"[DNS] {name} (type {qtype}) -> {_options.RedirectTo}", ConsoleColor.Magenta);
					response = BuildRedirectResponse(packet, questionEnd, answer: qtype == TypeA);
				}
				else
				{
					// Logged too so unknown hosts the game talks to show up in the console
					Siren.Log($"[DNS] {(parsed ? name : "?")} (type {qtype}) -> forwarded to {_options.Upstream}", ConsoleColor.DarkGray);
					response = await Forward(packet, ct);
				}

				if (response != null)
					await server.SendAsync(response, response.Length, query.RemoteEndPoint);
			}
			catch (Exception ex)
			{
				Siren.Log($"[DNS] Error handling query: {ex.Message}", ConsoleColor.Red);
			}
		}

		private bool ShouldRedirect(string name)
		{
			name = name.TrimEnd('.');
			return _options.Redirect.Any(d =>
				name.Equals(d, StringComparison.OrdinalIgnoreCase) ||
				name.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
		}

		private async Task<byte[]?> Forward(byte[] packet, CancellationToken ct)
		{
			using var client = new UdpClient(_upstream.AddressFamily);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(3));
			try
			{
				await client.SendAsync(packet, packet.Length, _upstream);
				var result = await client.ReceiveAsync(timeout.Token);
				return result.Buffer;
			}
			catch (OperationCanceledException)
			{
				return BuildErrorResponse(packet, rcode: 2); // SERVFAIL
			}
		}

		// Reads the first question: QNAME labels, QTYPE, QCLASS. Queries never use compression in the question.
		private static bool TryParseQuestion(byte[] p, out string name, out ushort qtype, out int end)
		{
			name = ""; qtype = 0; end = 0;
			if (p.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4)) == 0) return false;

			var sb = new StringBuilder();
			int pos = 12;
			while (pos < p.Length && p[pos] != 0)
			{
				int len = p[pos];
				if ((len & 0xC0) != 0 || pos + 1 + len > p.Length) return false;
				if (sb.Length > 0) sb.Append('.');
				sb.Append(Encoding.ASCII.GetString(p, pos + 1, len));
				pos += 1 + len;
			}
			pos++; // terminating zero label
			if (pos + 4 > p.Length) return false;

			name = sb.ToString();
			qtype = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
			end = pos + 4;
			return true;
		}

		// Echoes the query's header and question; for A queries adds one answer pointing at RedirectTo.
		// Other types (AAAA etc.) get an empty NOERROR answer so the client falls back to the A record.
		private byte[] BuildRedirectResponse(byte[] query, int questionEnd, bool answer)
		{
			var resp = new byte[questionEnd + (answer ? 16 : 0)];
			Array.Copy(query, resp, questionEnd);

			// QR=1, keep opcode + RD, AA=1, RA=1, RCODE=0
			resp[2] = (byte)(0x80 | (query[2] & 0x79) | 0x04);
			resp[3] = 0x80;
			BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4), 1);                  // QDCOUNT
			BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(6), (ushort)(answer ? 1 : 0)); // ANCOUNT
			BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(8), 0);                  // NSCOUNT
			BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(10), 0);                 // ARCOUNT

			if (answer)
			{
				var a = resp.AsSpan(questionEnd);
				BinaryPrimitives.WriteUInt16BigEndian(a, 0xC00C);      // name: pointer to the question
				BinaryPrimitives.WriteUInt16BigEndian(a[2..], TypeA);
				BinaryPrimitives.WriteUInt16BigEndian(a[4..], ClassIN);
				BinaryPrimitives.WriteUInt32BigEndian(a[6..], Ttl);
				BinaryPrimitives.WriteUInt16BigEndian(a[10..], 4);     // RDLENGTH
				_redirectIp.CopyTo(a[12..]);
			}
			return resp;
		}

		private static byte[] BuildErrorResponse(byte[] query, byte rcode)
		{
			var resp = new byte[Math.Min(query.Length, 12)];
			Array.Copy(query, resp, resp.Length);
			if (resp.Length < 12) return resp;
			resp[2] = (byte)(0x80 | (query[2] & 0x79));
			resp[3] = (byte)(0x80 | rcode);
			Array.Clear(resp, 4, 8); // no sections
			return resp;
		}
	}
}
