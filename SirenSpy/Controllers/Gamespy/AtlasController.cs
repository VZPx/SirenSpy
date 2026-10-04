using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace SirenSpy.Controllers.Gamespy
{
	// GameSpy ATLAS (competition / stats). Hosting a match (e.g. Initiation) calls CreateSession and
	// the game drops back to the menu if it fails. Response layout matches the parser in the EBOOT
	// (0x1c8d18..): <XResponse><XResult><result>0</result>...</XResult></XResponse>.
	[ApiController]
	[Route("/ATLAS/")]
	public class AtlasController : ControllerBase
	{
		private const string SubmissionNs = "http://gamespy.net/atlas/services/submissionservice/";
		private const string DataNs = "http://gamespy.net/atlas/services/dataservice/2010/11";

		[HttpPost("SubmissionService/2.0/SubmissionService.asmx")]
		public Task<IActionResult> Submission() => Handle(SubmissionNs);

		[HttpPost("DataService/2.0/GameConfig.asmx")]
		[HttpPost("DataService/2.0/StatisticsService.svc")]
		public Task<IActionResult> Data() => Handle(DataNs);

		private async Task<IActionResult> Handle(string ns)
		{
			// SubmitReport carries a binary report, so read raw bytes and only decode for logging
			byte[] body;
			try
			{
				using var ms = new MemoryStream();
				await Request.Body.CopyToAsync(ms);
				body = ms.ToArray();
			}
			catch (Exception ex) when (ex is OperationCanceledException or IOException)
			{
				return new EmptyResult();
			}

			var action = Request.Headers["SOAPAction"].ToString().Trim('"');
			var method = action[(action.LastIndexOf('/') + 1)..];
			var text = Encoding.UTF8.GetString(body);

			Siren.Log($"[ATLAS] {method} ({body.Length} bytes)", ConsoleColor.Cyan);

			switch (method)
			{
				case "CreateSession":
				case "CreateMatchlessSession":
				{
					// csid = session id, ccid = this client's connection id (both read as strings, max 255)
					var csid = Guid.NewGuid().ToString();
					var ccid = Guid.NewGuid().ToString();
					Siren.Log($"[ATLAS]    session {csid}", ConsoleColor.DarkCyan);
					return Soap(method, ns, $"<result>0</result><csid>{csid}</csid><ccid>{ccid}</ccid>");
				}

				case "SetReportIntention":
				{
					// Echo the connection id the game sent (it reads <ccid> back)
					var ccid = Between(text, "ccid>", "</") ?? Guid.NewGuid().ToString();
					return Soap(method, ns, $"<result>0</result><ccid>{ccid}</ccid>");
				}

				case "SubmitReport":
					return Soap(method, ns, "<result>0</result>");

				default:
					Siren.Log($"[ATLAS] Unimplemented {method}\n{(text.Length > 4000 ? text[..4000] : text)}", ConsoleColor.Yellow);
					return Soap(method, ns, "<result>0</result>");
			}
		}

		// Value of the first element whose name ends with `open` (namespace prefix agnostic)
		private static string? Between(string s, string open, string close)
		{
			int i = s.IndexOf(open, StringComparison.Ordinal);
			if (i < 0) return null;
			i += open.Length;
			int j = s.IndexOf(close, i, StringComparison.Ordinal);
			return j < 0 ? null : s[i..j];
		}

		private ContentResult Soap(string method, string ns, string inner)
		{
			var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
				"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
				$"<soap:Body><{method}Response xmlns=\"{ns}\"><{method}Result>{inner}</{method}Result></{method}Response></soap:Body></soap:Envelope>";
			return Content(xml, "text/xml; charset=utf-8");
		}
	}
}
