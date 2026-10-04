using Microsoft.AspNetCore.Mvc;
using SirenSpy.Agora;

namespace SirenSpy.Controllers.Agora
{
	// Agora "hydra" web API used by Gotham City Impostors.
	//
	// The game's Agora SDK builds URLs as  http(s)://<BaseServer>/<prefix>/<service>/<method>[/<arg>...]
	// When every argument is a scalar it is URL-escaped into the path (GET); otherwise every argument is packed,
	// in order, as a typed hydra value into the request body (POST). Responses are a sequence of typed hydra values
	// that the game receives as an array ("results"). HTTP 200 means success, anything else is treated as an error.
	[ApiController]
	public class AgoraController : ControllerBase
	{
		private readonly ILogger<AgoraController> _logger;
		private readonly AgoraService _agora;

		public AgoraController(ILogger<AgoraController> logger, AgoraService agora)
		{
			_logger = logger;
			_agora = agora;
		}

		[AcceptVerbs("GET", "POST", "PUT")]
		[Route("{prefix}/{service}/{method}/{**path}")]
		public async Task<IActionResult> Handle(string prefix, string service, string method, string? path)
		{
			var args = new List<HValue>();

			if (!string.IsNullOrEmpty(path))
			{
				foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
					args.Add(new HUtf8(Uri.UnescapeDataString(part)));
			}

			byte[] body;
			try
			{
				using var ms = new MemoryStream();
				await Request.Body.CopyToAsync(ms);
				body = ms.ToArray();
			}
			catch (Exception ex) when (ex is OperationCanceledException or IOException)
			{
				// The game closed the connection mid-request (e.g. it crashed); nothing to answer
				return new EmptyResult();
			}

			if (body.Length > 0)
			{
				try
				{
					args.AddRange(Hydra.Deserialize(body));
				}
				catch (Exception ex)
				{
					_logger.LogWarning($"[Agora] Failed to parse body of {service}/{method}: {ex.Message}\n{Convert.ToHexString(body)}");
				}
			}

			var ticket = Request.Headers["x-psn-ticket"].ToString();
			var req = new AgoraRequest(prefix, service, method, args, ticket);

			Siren.Log($"[Agora] {Request.Method} {service}/{method} ({args.Count} args)", ConsoleColor.Cyan);
			for (int i = 0; i < args.Count; i++)
				Siren.Log($"    arg[{i}] = {args[i]}", ConsoleColor.DarkCyan);

			HValue[] results;
			try
			{
				results = _agora.Dispatch(req);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"[Agora] Handler for {service}/{method} threw");
				results = Array.Empty<HValue>();
			}

			foreach (var r in results)
				Siren.Log($"    => {r}", ConsoleColor.DarkGreen);

			// File() sets Content-Length, which the client checks against the received size.
			return File(Hydra.Serialize(results), Hydra.ContentType);
		}
	}
}
