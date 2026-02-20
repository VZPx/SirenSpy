using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<UdpServer>();
//builder.Services.AddSingleton<TCPServer>(provider => new TCPServer(443));

builder.WebHost.ConfigureKestrel(options =>
{
	// HTTP (Port 80)
	options.ListenLocalhost(80);

	// HTTPS (Port 443)
	options.ListenLocalhost(443);
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

	context.Request.EnableBuffering();
	var body = string.Empty;
	if (context.Request.Body.CanSeek)
	{
		using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
		{
			body = await reader.ReadToEndAsync();
			context.Request.Body.Position = 0;
		}
	}

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


var udpServer = app.Services.GetRequiredService<UdpServer>();
udpServer.Start();

//var tcpServer = app.Services.GetRequiredService<TCPServer>();
//_ = tcpServer.Start();

app.UseAuthorization();
app.MapControllers();
app.Run();

/*private readonly int[] _ports = { 6500, 28910, 29900, 29901, 29910, 28900, 27900, 27901, 29920,
	6667, 80, 10086};*/ //gamespy ports

public class UdpServer
{
	private const int Port = 27900;
	private UdpClient udpServer;

	public UdpServer()
	{
		udpServer = new UdpClient(Port);
		Console.WriteLine($"UDP Server listening on port {Port}...");
	}

	public void Start()
	{
		Task.Run(async () =>
		{
			try
			{
				while (true)
				{
					IPEndPoint clientEndPoint = new IPEndPoint(IPAddress.Any, Port);
					byte[] requestData = udpServer.Receive(ref clientEndPoint);
					Console.WriteLine($"[UDP SERVER]\nReceived request from {clientEndPoint.Address}:{clientEndPoint.Port}\n");
					Console.Write("Request: ");
					foreach (byte b in requestData)
					{
						Console.Write($"{b:X2} ");
					}
					Console.WriteLine("\n");


					string gameName = ParseGameName(requestData);
					Console.WriteLine($"Game name requested: {gameName}");

					SendAvailabilityResponse(clientEndPoint);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error: {ex.Message}");
			}
		});
	}

	private string ParseGameName(byte[] requestData)
	{
		// Make sure we have enough data (at least 5 bytes to start parsing)
		if (requestData.Length < 5)
		{
			Console.WriteLine("Not enough data to extract game name.");
			return string.Empty;
		}

		//Extract the game name starting from byte 5 until the null terminator
		int startIndex = 5;
		int gameNameLength = Array.IndexOf(requestData, (byte)0, startIndex) - startIndex;
		if (gameNameLength <= 0)
		{
			Console.WriteLine("No valid game name found.");
			return string.Empty;
		}

		string gameName = Encoding.ASCII.GetString(requestData, startIndex, gameNameLength);

		return gameName;
	}


	public static readonly byte[] ResponsePrefix = { 0xfe, 0xfd, 0x09, 0x00, 0x00, 0x00 };

	private void SendAvailabilityResponse(IPEndPoint clientEndPoint)
	{
		// Response data: 3 bytes header and a 4-byte status integer
		byte[] response = new byte[7];

		// Header: 0xFE, 0xFD, 0x09
		response[0] = 0xFE;
		response[1] = 0xFD;
		response[2] = 0x09;

		int status = 0x00000000; //server online

		// Convert bytes (big-endian)
		byte[] statusBytes = BitConverter.GetBytes(status);
		Array.Copy(statusBytes, 0, response, 3, 4);

		udpServer.Send(response, response.Length, clientEndPoint);
		Console.WriteLine("Sent GameSpy availability response.\n");
		Console.Write("Response: ");
		foreach (byte b in response)
		{
			Console.Write($"{b:X2} ");
		}
		Console.WriteLine("\n");
	}

	public static void Main(string[] args)
	{
		UdpServer server = new UdpServer();
		server.Start();
	}
}

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



