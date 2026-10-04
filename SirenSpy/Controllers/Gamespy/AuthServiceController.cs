using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace SirenSpy.Controllers.Gamespy
{
	[ApiController]
	[Route("/AuthService/")]
	public class AuthServiceController : ControllerBase
	{
		[HttpPost("AuthService.asmx")]
		public async Task<IActionResult> HandlePostRequestAsync()
		{
			var soapAction = Request.Headers["SOAPAction"].ToString();
			string requestBody;

			try
			{
				using var reader = new StreamReader(Request.Body, Encoding.UTF8);
				requestBody = await reader.ReadToEndAsync();
			}
			catch (Exception ex) when (ex is OperationCanceledException or IOException)
			{
				// The game closed the connection mid-request (e.g. it crashed); nothing to answer
				return new EmptyResult();
			}

			Console.WriteLine($"Received SOAPAction: {soapAction}");
			Console.WriteLine("Request Body:");
			Console.WriteLine(requestBody);

			if (soapAction.Contains("LoginPs3CertWithGameId"))
			{
				Siren.Log("Requesting gamespy token!!", ConsoleColor.Blue);
				return await LoginPs3CertWithGameId(requestBody);
			}
			else if (soapAction.Contains("LoginRemoteAuthWithGameId"))
			{
				Siren.Log("Calling LoginRemoteAuthWithGameId!", ConsoleColor.Magenta);
				return await LoginRemoteAuthWithGameId(requestBody);
			}


			return Ok();
		}


		public async Task<IActionResult> LoginPs3CertWithGameId(string requestBody)
		{
			// Identify the player from the PS3 NP ticket; the token comes back in LoginRemoteAuthWithGameId
			var ticket = System.Text.RegularExpressions.Regex.Match(requestBody, @"<(?:\w+:)?Value>([^<]*)</").Groups[1].Value;
			var (player, token) = SirenSpy.Gamespy.GameSpyPlayers.LoginWithTicket(ticket);
			Siren.Log($"[Auth] {player.OnlineId} (account {player.AccountId}) -> profile {player.ProfileId}", ConsoleColor.Blue);

			string xmlResponse = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<SOAP-ENV:Envelope xmlns:SOAP-ENV=""http://schemas.xmlsoap.org/soap/envelope/""
                   xmlns:SOAP-ENC=""http://schemas.xmlsoap.org/soap/encoding/""
                   xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
                   xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
                   xmlns:ns1=""http://gamespy.net/AuthService/"">
    <SOAP-ENV:Body>
        <ns1:LoginPs3CertWithGameIdResult>
            <ns1:responseCode>0</ns1:responseCode>
            <ns1:authToken>{token}</ns1:authToken>
            <ns1:partnerChallenge>22222222222222222222222222222222</ns1:partnerChallenge>
        </ns1:LoginPs3CertWithGameIdResult>
    </SOAP-ENV:Body>
</SOAP-ENV:Envelope>";

			Console.WriteLine("\nResponding with:\n");
			Console.WriteLine(xmlResponse);

			return Content(xmlResponse, "text/xml");
		}

		public async Task<IActionResult> LoginRemoteAuthWithGameId(string requestBody)
		{
			var doc0 = XDocument.Parse(requestBody);
			var authToken = doc0.Descendants().FirstOrDefault(e => e.Name.LocalName == "authtoken")?.Value ?? "";
			var player = SirenSpy.Gamespy.GameSpyPlayers.ByToken(authToken);
			int userId = player.UserId;
			int profileId = player.ProfileId;
			// GameSpy nicks are plain ASCII; PSN online ids already are
			string nick = player.OnlineId.Length > 0 ? player.OnlineId : "Player" + profileId;
			var SIGNATURE_PREFIX = "0001FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF003020300C06082A864886F70D020505000410";
			var doc = XDocument.Parse(requestBody);
			XNamespace ns1 = "http://gamespy.net/AuthService/";

			string version = doc.Descendants(ns1 + "version").FirstOrDefault()?.Value ?? "1";
			string partnerCode = doc.Descendants(ns1 + "partnercode").FirstOrDefault()?.Value ?? "0";
			string namespaceId = doc.Descendants(ns1 + "namespaceid").FirstOrDefault()?.Value ?? "0";

			string PEERKEYMODULUS = "aefb5064bbd1eb632fa8d57aab1c49366ce0ee3161cbef19f2b7971b63b811790ecbf6a47b34c55f65a0766b40c261c5d69c394cd320842dd2bccba883d30eae8fdba5d03b21b09bfc600dcb30b1b2f3fbe8077630b006dcb54c4254f14891762f72e7bbfe743eb8baf65f9e8c8d11ebe46f6b59e986b4c394cfbc2c8606e29f";
			string SERVERDATA = "95980bf5011ce73f2866b995a272420c36f1e8b4ac946f0b5bfe87c9fef0811036da00cfa85e77e00af11c924d425ec06b1dd052feab1250376155272904cbf9da831b0ce3d52964424c0a426b869e2c0ad11ffa3e70496e27ea250adb707a96b3496bff190eafc0b6b9c99db75b02c2a822bb1b5b3d954e7b2c0f9b1487e3e1";
			string PEERKEYEXPONENT = "000001";

			string hash = ComputeGameSpyHash(
	303, int.Parse(version), int.Parse(partnerCode), int.Parse(namespaceId),
	userId, profileId, 0,
	nick, nick, "",
	PEERKEYMODULUS, SERVERDATA
);

			DateTime now = DateTime.Now;

			string dateText = now.ToString("dddd, MMMM dd, yyyy h:mm:ss tt");

			byte[] textBytes = Encoding.UTF8.GetBytes(dateText);
			string base64Timestamp = Convert.ToBase64String(textBytes);

			string xmlResponse = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<SOAP-ENV:Envelope xmlns:SOAP-ENV=""http://schemas.xmlsoap.org/soap/envelope/""
                   xmlns:ns1=""http://gamespy.net/AuthService/"">
    <SOAP-ENV:Body>
        <ns1:LoginRemoteAuthWithGameIdResult>
            <ns1:responseCode>0</ns1:responseCode>
            <ns1:certificate>
                <ns1:length>303</ns1:length>
                <ns1:version>{version}</ns1:version>
                <ns1:partnercode>{partnerCode}</ns1:partnercode>
                <ns1:namespaceid>{namespaceId}</ns1:namespaceid>
                <ns1:userid>{userId}</ns1:userid>
                <ns1:profileid>{profileId}</ns1:profileid>
                <ns1:expiretime>0</ns1:expiretime>
                <ns1:profilenick>{nick}</ns1:profilenick>
                <ns1:uniquenick>{nick}</ns1:uniquenick>
                <ns1:cdkeyhash></ns1:cdkeyhash>
                <ns1:peerkeymodulus>{PEERKEYMODULUS}</ns1:peerkeymodulus>
                <ns1:peerkeyexponent>{PEERKEYEXPONENT}</ns1:peerkeyexponent>
                <ns1:serverdata>{SERVERDATA}</ns1:serverdata>
                <ns1:signature>{SIGNATURE_PREFIX + hash}</ns1:signature>
				<ns1:timestamp>{base64Timestamp}</ns1:timestamp>
            </ns1:certificate>
            <ns1:peerkeyprivate>{PEERKEYEXPONENT}</ns1:peerkeyprivate>
        </ns1:LoginRemoteAuthWithGameIdResult>
    </SOAP-ENV:Body>
</SOAP-ENV:Envelope>";

			Console.WriteLine("\nResponding with structured certificate...\n");
			return Content(xmlResponse, "text/xml");
		}

		private string ComputeGameSpyHash(
	int length, int version, int partnerCode, int namespaceId,
	int userId, int profileId, int expireTime,
	string profileNick, string uniqueNick, string cdKeyHash,
	string peerKeyModulus, string serverData)
		{
			using (MemoryStream ms = new MemoryStream())
			using (BinaryWriter writer = new BinaryWriter(ms))
			{
				// 1. Write the 4-byte integers (Little Endian)
				writer.Write(length);
				writer.Write(version);
				writer.Write(partnerCode);
				writer.Write(namespaceId);
				writer.Write(userId);
				writer.Write(profileId);
				writer.Write(expireTime);

				// 2. Write strings (ASCII encoded)
				writer.Write(Encoding.ASCII.GetBytes(profileNick));
				writer.Write(Encoding.ASCII.GetBytes(uniqueNick));
				writer.Write(Encoding.ASCII.GetBytes(cdKeyHash));

				// 3. Convert Hex Modulus to bytes and add it
				writer.Write(Convert.FromHexString(peerKeyModulus));

				// 4. Add the 0x01 separator byte
				writer.Write((byte)0x01);

				// 5. Convert Hex ServerData to bytes and add it
				writer.Write(Convert.FromHexString(serverData));

				// 6. Compute MD5
				byte[] hashBytes = MD5.HashData(ms.ToArray());

				// 7. Return as lowercase hex string (hexdigest)
				return Convert.ToHexString(hashBytes).ToLower();
			}
		}

	}
}

public static class Siren
{
	public static void Log(string message, ConsoleColor color)
	{
		ConsoleColor ogFColor = Console.ForegroundColor;
		//ConsoleColor ogBColor = Console.BackgroundColor;

		Console.ForegroundColor = color;
		Console.WriteLine(message);
		Console.ForegroundColor = ogFColor;
	}

	public static void Write(string message, ConsoleColor color)
	{
		ConsoleColor ogFColor = Console.ForegroundColor;
		//ConsoleColor ogBColor = Console.BackgroundColor;

		Console.ForegroundColor = color;
		Console.Write(message);
		Console.ForegroundColor = ogFColor;
	}

	public static void WriteLine(string message, ConsoleColor color)
	{
		ConsoleColor ogFColor = Console.ForegroundColor;
		//ConsoleColor ogBColor = Console.BackgroundColor;

		Console.ForegroundColor = color;
		Console.WriteLine(message);
		Console.ForegroundColor = ogFColor;
	}
}