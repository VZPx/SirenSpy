using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<SirenSpy.Agora.AgoraService>();
builder.Services.AddHostedService<SirenSpy.Dns.DnsRedirector>();

// GameSpy: QR2 master (UDP 27900), server browser (TCP 28910), NAT negotiation (UDP 27901)
builder.Services.Configure<SirenSpy.Gamespy.GameSpyOptions>(builder.Configuration.GetSection("GameSpy"));
builder.Services.AddSingleton<SirenSpy.Gamespy.ServerRegistry>();
builder.Services.AddSingleton<SirenSpy.Gamespy.Qr2Server>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SirenSpy.Gamespy.Qr2Server>());
builder.Services.AddHostedService<SirenSpy.Gamespy.ServerBrowserServer>();
builder.Services.AddHostedService<SirenSpy.Gamespy.NatNegServer>();
//builder.Services.AddSingleton<TCPServer>(provider => new TCPServer(443));

builder.WebHost.ConfigureKestrel(options =>
{
	// All interfaces so other players (LAN / internet) can reach the server, not only this PC
	// HTTP (Port 80)
	options.ListenAnyIP(80);

	// HTTPS (Port 443)
	options.ListenAnyIP(443);
});

var app = builder.Build();

app.Use(async (context, next) =>
{
	await next.Invoke();

	var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
	var requestedUrl = context.Request.Path.ToString();

	var method = context.Request.Method;
	var url = context.Request.Path.ToString();
	var host = context.Request.Host.ToString();
	var userAgent = context.Request.Headers["User-Agent"];

	var headers = context.Request.Headers;

	// The body was already consumed by the controller (they log it themselves). Reading it again here
	// throws "Reading is already in progress" when the game drops the connection mid-request.
	var body = string.Empty;

	logger.LogInformation($"\nReceived Request:\n{method} {url} HTTP/1.1\n" +
						   $"Host: {host}\n" +
						   $"User-Agent: {userAgent}\n" +
						   $"Connection: {context.Request.Headers["Connection"]}\n" +
						   $"Content-Length: {context.Request.Headers["Content-Length"]}\n" +
						   $"Content-Type: {context.Request.Headers["Content-Type"]}\n" +
						   $"SOAPAction: {context.Request.Headers["SOAPAction"]}\n" +
						   $"AccessKey: {context.Request.Headers["AccessKey"]}\n" +
						   $"GameID: {context.Request.Headers["GameID"]}\n" +
						   $"{body}\n\n");

	if (context.Response.StatusCode == 404)
	{
		logger.LogWarning($"404 Not Found: {requestedUrl}");

		context.Response.ContentType = "text/plain";
		await context.Response.WriteAsync($"404 Not Found: {requestedUrl}");
	}
	else
	{
		logger.LogInformation($"Success: {requestedUrl} - {context.Response.StatusCode}");
	}
});


//var tcpServer = app.Services.GetRequiredService<TCPServer>();
//_ = tcpServer.Start();

app.UseAuthorization();
app.MapControllers();
app.Run();

/*private readonly int[] _ports = { 6500, 28910, 29900, 29901, 29910, 28900, 27900, 27901, 29920,
	6667, 80, 10086};*/ //gamespy ports

public class TCPServer
{
	private TcpListener listener;

	public TCPServer(int port)
	{
		listener = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
	}

	public async Task Start()
	{
		listener.Start();
		Console.WriteLine($"TCP Server started on port {listener.LocalEndpoint}");

		while (true)
		{
			try
			{
				TcpClient client = await listener.AcceptTcpClientAsync();
				_ = Task.Run(() => HandleClient(client));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error: {ex.Message}");
			}
		}
	}

	private async Task HandleClient(TcpClient client)
	{
		using (client)
		{
			NetworkStream stream = client.GetStream();
			byte[] buffer = new byte[1024];
			int bytesRead;


			try
			{
				while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
				{
					Siren.WriteLine("Received raw bytes:", ConsoleColor.Green);
					for (int i = 0; i < bytesRead; i++)
					{
						Siren.Write($"{buffer[i]:X2} ", ConsoleColor.Green);
					}
					Console.WriteLine();
				}
			}
			catch (Exception ex)
			{
				Siren.WriteLine($"Client handling error: {ex.Message}", ConsoleColor.Green);
			}
		}

		Siren.WriteLine("Client disconnected.", ConsoleColor.Green);
	}
}

/*public enum ServerAvailability : uint
{
	Available = 0,
	Waiting = 1,
	PermanentUnavailable = 2,
	TemporarilyUnavailable = 3,
};*/



