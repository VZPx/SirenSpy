using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;

namespace SirenSpy.Controllers
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

			using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
			{
				requestBody = await reader.ReadToEndAsync();
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

		private const string SignatureKey = "6FEC09F2E2B1F69C6F4A" +
			"E9C7674EE5AAE734F7EFDA484DAAF22728355128F2440F4925F7" +
			"5663116BC7194DE318D4812A2CDEC371670BF7EEE06F43A21BEB" +
			"5867F79D40CAAA91D5A87E3A09097BE88F52AD47ED651889B65D" +
			"71875F61E7C34B43BD8EC2F52FF3166EC61D897A3427A1FA32F3" +
			"F12A987796E1A28FB942E96079FF";

		private const string SignatureExp = "010001";

		public async Task<IActionResult> LoginPs3CertWithGameId(string requestBody)
		{
			string xmlResponse = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<SOAP-ENV:Envelope xmlns:SOAP-ENV=""http://schemas.xmlsoap.org/soap/envelope/""
                   xmlns:SOAP-ENC=""http://schemas.xmlsoap.org/soap/encoding/""
                   xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
                   xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
                   xmlns:ns1=""http://gamespy.net/AuthService/"">
    <SOAP-ENV:Body>
        <ns1:LoginPs3CertWithGameIdResult>
            <ns1:responseCode>0</ns1:responseCode>
            <ns1:authToken>12345678</ns1:authToken>
            <ns1:partnerChallenge>test</ns1:partnerChallenge>
        </ns1:LoginPs3CertWithGameIdResult>
    </SOAP-ENV:Body>
</SOAP-ENV:Envelope>";

			Console.WriteLine("\nResponding with:\n");
			Console.WriteLine(xmlResponse);

			return Content(xmlResponse, "text/xml");
		}

		//rsaprivatekey(hex):3082025b0201000281806fec09f2e2b1f69c6f4ae9c7674ee5aae734f7efda484daaf22728355128f2440f4925f75663116bc7194de318d4812a2cdec371670bf7eee06f43a21beb5867f79d40caaa91d5a87e3a09097be88f52ad47ed651889b65d71875f61e7c34b43bd8ec2f52ff3166ec61d897a3427a1fa32f3f12a987796e1a28fb942e96079ff0203010001028180277a7e03e3bcdc7d4fb08007eb43e435457ae49e014411c963c33626a06b2e119dc6b292ad3122bafeaec94f7ab9b299fadbd4dad27b61482c7872c5e2cd40106b15f2ab42a646a889087465adbd18ae7cb05ae51afbcde7f8700ae54f0799419020409f5545369bbc5a68523a4cafd808fb3b4bf4cad4e04be5170b77b14621024100c8ea74106c0112992a98201febdc5abe399bf5433c6751508c49b8f7ecdac95acb90bab235adcd85415ce46f3ecd2363e5d4de3146e6820109f518ac66c2562f0241008e9b6fa2e58bc0c7db9c88bb7884882bfc599da403e15b95a27994537a75f42bb4f15dc4f49de6af7ac8c3ffbbc4e49253dcd08f78abf64a0bd63a912fbdf531024040f319e61d2413a11415ed7ca440adcd04ce97f0ce5c0ffb5bfe911f04a08dab1e5781230f1b4a6f237c26149856b4741cde3d9dec6fa3e33616d78d14030add0241008aefb101f9b600aa36b1f91cfcbbd29758124f5d7e524f1227eb5fb13cfc32596abe45672013beae7467a95d3c2905aff2788dd159f5dfcc2060254b524235e102404f14430b82d693ab1e4a8ffeee8fed4f6d42ffc01668c98861c7b6c6ebb88dc4dc6d1d1299ec820a8b48922752214a2230c986a4cb6336d554ac66b95ed640b7

		private const string RSA_PRIVATE_KEY_HEX = @"3082025b0201000281806fec09f2e2b1f69c6f4ae9c7674ee5aae734f7efda484daaf22728355128f2440f4925f75663116bc7194de318d4812a2cdec371670bf7eee06f43a21beb5867f79d40caaa91d5a87e3a09097be88f52ad47ed651889b65d71875f61e7c34b43bd8ec2f52ff3166ec61d897a3427a1fa32f3f12a987796e1a28fb942e96079ff0203010001028180277a7e03e3bcdc7d4fb08007eb43e435457ae49e014411c963c33626a06b2e119dc6b292ad3122bafeaec94f7ab9b299fadbd4dad27b61482c7872c5e2cd40106b15f2ab42a646a889087465adbd18ae7cb05ae51afbcde7f8700ae54f0799419020409f5545369bbc5a68523a4cafd808fb3b4bf4cad4e04be5170b77b14621024100c8ea74106c0112992a98201febdc5abe399bf5433c6751508c49b8f7ecdac95acb90bab235adcd85415ce46f3ecd2363e5d4de3146e6820109f518ac66c2562f0241008e9b6fa2e58bc0c7db9c88bb7884882bfc599da403e15b95a27994537a75f42bb4f15dc4f49de6af7ac8c3ffbbc4e49253dcd08f78abf64a0bd63a912fbdf531024040f319e61d2413a11415ed7ca440adcd04ce97f0ce5c0ffb5bfe911f04a08dab1e5781230f1b4a6f237c26149856b4741cde3d9dec6fa3e33616d78d14030add0241008aefb101f9b600aa36b1f91cfcbbd29758124f5d7e524f1227eb5fb13cfc32596abe45672013beae7467a95d3c2905aff2788dd159f5dfcc2060254b524235e102404f14430b82d693ab1e4a8ffeee8fed4f6d42ffc01668c98861c7b6c6ebb88dc4dc6d1d1299ec820a8b48922752214a2230c986a4cb6336d554ac66b95ed640b7";

		private const string RSA_PRIVATE_KEY = @"-----BEGIN RSA PRIVATE KEY-----
MIICWwIBAAKBgG/sCfLisfacb0rpx2dO5arnNPfv2khNqvInKDVRKPJED0kl91Zj
EWvHGU3jGNSBKizew3FnC/fu4G9DohvrWGf3nUDKqpHVqH46CQl76I9SrUftZRiJ
tl1xh19h58NLQ72OwvUv8xZuxh2JejQnofoy8/EqmHeW4aKPuULpYHn/AgMBAAEC
gYAnen4D47zcfU+wgAfrQ+Q1RXrkngFEEcljwzYmoGsuEZ3GspKtMSK6/q7JT3q5
spn629Ta0nthSCx4csXizUAQaxXyq0KmRqiJCHRlrb0YrnywWuUa+83n+HAK5U8H
mUGQIECfVUU2m7xaaFI6TK/YCPs7S/TK1OBL5RcLd7FGIQJBAMjqdBBsARKZKpgg
H+vcWr45m/VDPGdRUIxJuPfs2slay5C6sjWtzYVBXORvPs0jY+XU3jFG5oIBCfUY
rGbCVi8CQQCOm2+i5YvAx9uciLt4hIgr/FmdpAPhW5WieZRTenX0K7TxXcT0neav
esjD/7vE5JJT3NCPeKv2SgvWOpEvvfUxAkBA8xnmHSQToRQV7XykQK3NBM6X8M5c
D/tb/pEfBKCNqx5XgSMPG0pvI3wmFJhWtHQc3j2d7G+j4zYW140UAwrdAkEAiu+x
Afm2AKo2sfkc/LvSl1gST11+Uk8SJ+tfsTz8MllqvkVnIBO+rnRnqV08KQWv8niN
0Vn138wgYCVLUkI14QJATxRDC4LWk6seSo/+7o/tT21C/8AWaMmIYce2xuu4jcTc
bR0SmeyCCotIkidSIUoiMMmGpMtjNtVUrGa5XtZAtw==
-----END RSA PRIVATE KEY-----
";

		public async Task<IActionResult> LoginRemoteAuthWithGameId(string requestBody)
		{
			string xmlResponse = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<SOAP-ENV:Envelope xmlns:SOAP-ENV=""http://schemas.xmlsoap.org/soap/envelope/""
                   xmlns:SOAP-ENC=""http://schemas.xmlsoap.org/soap/encoding/""
                   xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
                   xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
                   xmlns:ns1=""http://gamespy.net/AuthService/"">
    <SOAP-ENV:Body>
        <ns1:LoginRemoteAuthWithGameIdResult>
            <ns1:responseCode>0</ns1:responseCode>
            <ns1:certificate>{RSA_PRIVATE_KEY}</ns1:certificate>
            <ns1:peerkeyprivate>{RSA_PRIVATE_KEY_HEX}</ns1:peerkeyprivate>
        </ns1:LoginRemoteAuthWithGameIdResult>
    </SOAP-ENV:Body>
</SOAP-ENV:Envelope>";

			Console.WriteLine("\nResponding with:\n");
			Console.WriteLine(xmlResponse);

			return Content(xmlResponse, "text/xml");
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