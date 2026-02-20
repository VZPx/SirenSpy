using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace SirenSpy.Controllers.Gamespy
{
	[ApiController]
	[Route("{userId}")] // This captures the 3377430... part
	public class AgoraController : ControllerBase
	{
		private readonly ILogger<AgoraController> _logger;

		public AgoraController(ILogger<AgoraController> logger)
		{
			_logger = logger;
		}

		// 3377430763997974612/feed/get_channels_by_owner/0
		[HttpGet("feed/get_channels_by_owner/{id}")]
		public IActionResult GetChannels(string userId, string id)
		{
			_logger.LogInformation($"Agora called! id: {id}");

			switch (id)
			{
				case "6":
					_logger.LogInformation("Gangs was called");
					// Sending the hex byte 0x02 as a 3-byte buffer to match your JS alloc(3)
					byte[] gangBuf = new byte[] { 0x02, 0x00, 0x00 };
					return File(gangBuf, "application/x-hydra-binary");

				case "15":
					_logger.LogInformation("Attempting to read EULA");
					break;

				case "16":
					_logger.LogInformation("Attempting to read Terms of Service");
					break;

				case "17":
					_logger.LogInformation("Attempting to read Privacy Policy");
					break;

				default:
					_logger.LogWarning("Unable to read order Id");
					break;
			}

			// Return an empty success if no specific case is handled
			return File(new byte[] { 0x00, 0x00, 0x00, 0x00 }, "application/x-hydra-binary");
		}

		//3377430763997974612/challenge/list_open_challenges/1/16
		[HttpGet("challenge/list_open_challenges/{page}/{count}")]
		public IActionResult GetChallenges(string userId, int page, int count)
		{
			_logger.LogInformation($"Agora: User {userId} requested challenges page {page}");

			byte[] response = new byte[] { 0x00, 0x00, 0x00, 0x00 };
			return File(response, "application/x-hydra-binary");
		}
	}
}

