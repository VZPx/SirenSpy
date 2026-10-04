using Microsoft.AspNetCore.Mvc;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SirenSpy.Controllers.Gamespy
{
	// GameSpy Sake storage (SOAP). Gotham City Impostors keeps the player profile in table "Player_Profile"
	// as binary fields DataBlock_0..DataBlock_7; "Loading Profile" waits for GetMyRecords to answer.
	[ApiController]
	[Route("/SakeStorageServer/")]
	public class SakeController : ControllerBase
	{
		private static readonly XNamespace Sake = "http://gamespy.net/sake";
		private static readonly object Lock = new();
		private static readonly string StorePath = Path.Combine(AppContext.BaseDirectory, "sake_data.json");
		private static SakeStore? _store;

		public class SakeValue
		{
			public string Type { get; set; } = "";  // element name, e.g. binaryDataValue, intValue
			public string Value { get; set; } = "";
		}

		public class SakeRecord
		{
			public int RecordId { get; set; }
			public int OwnerId { get; set; }
			public string Table { get; set; } = "";
			public Dictionary<string, SakeValue> Fields { get; set; } = new();
		}

		public class SakeStore
		{
			public int NextRecordId { get; set; } = 1;
			public List<SakeRecord> Records { get; set; } = new();
		}

		private static SakeStore Store
		{
			get
			{
				if (_store == null)
				{
					try { _store = System.IO.File.Exists(StorePath) ? JsonSerializer.Deserialize<SakeStore>(System.IO.File.ReadAllText(StorePath)) : null; }
					catch (Exception ex) { Siren.Log($"[Sake] Failed to load {StorePath}: {ex.Message}", ConsoleColor.Red); }
					_store ??= new SakeStore();
				}
				return _store;
			}
		}

		private static void Save() =>
			System.IO.File.WriteAllText(StorePath, JsonSerializer.Serialize(Store, new JsonSerializerOptions { WriteIndented = true }));

		[HttpPost("Public/StorageServer.asmx")]
		[HttpPost("StorageServer.asmx")]
		public async Task<IActionResult> Handle()
		{
			string body;
			try
			{
				using var reader = new StreamReader(Request.Body, Encoding.UTF8);
				body = await reader.ReadToEndAsync();
			}
			catch (Exception ex) when (ex is OperationCanceledException or IOException)
			{
				// The game closed the connection mid-request (e.g. it crashed); nothing to answer
				return new EmptyResult();
			}

			var action = Request.Headers["SOAPAction"].ToString().Trim('"');
			var method = action[(action.LastIndexOf('/') + 1)..];

			XElement? call;
			try
			{
				call = XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == method);
			}
			catch (Exception ex)
			{
				Siren.Log($"[Sake] Bad request body for {method}: {ex.Message}\n{body}", ConsoleColor.Red);
				return SoapResponse(method, "Error", "");
			}

			if (call == null)
				return SoapResponse(method, "Error", "");

			var table = Child(call, "tableid")?.Value ?? "";
			var owner = OwnerId(call);
			Siren.Log($"[Sake] {method} table={table} profile={owner}", ConsoleColor.Cyan);

			lock (Lock)
			{
				switch (method)
				{
					case "GetMyRecords":
					{
						var fields = FieldList(call);
						var records = Store.Records.Where(r => r.Table == table && r.OwnerId == owner).ToList();
						Siren.Log($"[Sake]    {records.Count} record(s), fields: {string.Join(", ", fields)}", ConsoleColor.DarkCyan);
						return SoapResponse(method, "Success", ValuesXml(records, fields));
					}

					// The profile download uses this: ownerids=[profileid], max=1, fields DataBlock_0..7
					case "SearchForRecords":
					{
						var fields = FieldList(call);
						var owners = Child(call, "ownerids")?.Elements().Select(e => int.TryParse(e.Value, out var i) ? i : -1).ToHashSet() ?? new();
						int.TryParse(Child(call, "offset")?.Value, out var offset);
						if (!int.TryParse(Child(call, "max")?.Value, out var max) || max <= 0) max = int.MaxValue;

						var records = Store.Records
							.Where(r => r.Table == table && (owners.Count == 0 || owners.Contains(r.OwnerId)))
							.OrderBy(r => r.RecordId).Skip(Math.Max(0, offset)).Take(max).ToList();
						Siren.Log($"[Sake]    {records.Count} record(s) for owners [{string.Join(",", owners)}]", ConsoleColor.DarkCyan);
						return SoapResponse(method, "Success", ValuesXml(records, fields));
					}

					case "GetSpecificRecords":
					{
						var fields = FieldList(call);
						var ids = Child(call, "recordids")?.Elements().Select(e => int.TryParse(e.Value, out var i) ? i : -1).ToHashSet() ?? new();
						var records = Store.Records.Where(r => r.Table == table && ids.Contains(r.RecordId)).ToList();
						return SoapResponse(method, "Success", ValuesXml(records, fields));
					}

					case "CreateRecord":
					{
						var record = new SakeRecord { RecordId = Store.NextRecordId++, OwnerId = owner, Table = table };
						ApplyValues(record, call);
						Store.Records.Add(record);
						Save();
						Siren.Log($"[Sake]    created record {record.RecordId} ({record.Fields.Count} fields)", ConsoleColor.Green);
						return SoapResponse(method, "Success", $"<recordid>{record.RecordId}</recordid>");
					}

					case "UpdateRecord":
					{
						int.TryParse(Child(call, "recordid")?.Value, out var id);
						var record = Store.Records.FirstOrDefault(r => r.Table == table && r.RecordId == id);
						if (record == null)
							return SoapResponse(method, "RecordNotFound", "");
						ApplyValues(record, call);
						Save();
						Siren.Log($"[Sake]    updated record {record.RecordId}", ConsoleColor.Green);
						return SoapResponse(method, "Success", "");
					}

					case "DeleteRecord":
					{
						int.TryParse(Child(call, "recordid")?.Value, out var id);
						Store.Records.RemoveAll(r => r.Table == table && r.RecordId == id && r.OwnerId == owner);
						Save();
						return SoapResponse(method, "Success", "");
					}

					case "GetRecordCount":
						return SoapResponse(method, "Success", "<count>" +
							Store.Records.Count(r => r.Table == table) + "</count>");

					case "GetRecordLimit":
						return SoapResponse(method, "Success", "<limitPerOwner>100</limitPerOwner><numOwned>" +
							Store.Records.Count(r => r.Table == table && r.OwnerId == owner) + "</numOwned>");

					default:
						// Answer with an error code rather than a bare "Success": the GameSpy SDK reads output
						// fields (e.g. <values>) for successful requests and crashes on missing ones.
						Siren.Log($"[Sake] Unimplemented method {method}\n{body}", ConsoleColor.Yellow);
						return SoapResponse(method, "ServiceDisabled", "");
				}
			}
		}

		private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

		// Requests carry the login certificate; records are owned by its profileid.
		private static int OwnerId(XElement call)
		{
			var pid = call.Descendants().FirstOrDefault(e => e.Name.LocalName == "profileid")?.Value;
			return int.TryParse(pid, out var id) ? id : 0;
		}

		private static List<string> FieldList(XElement call) =>
			Child(call, "fields")?.Elements().Select(e => e.Value).ToList() ?? new();

		// <values><RecordField><name>X</name><value><binaryDataValue><value>...</value></binaryDataValue></value></RecordField>...
		private static void ApplyValues(SakeRecord record, XElement call)
		{
			foreach (var field in Child(call, "values")?.Elements() ?? Enumerable.Empty<XElement>())
			{
				var name = Child(field, "name")?.Value;
				var typed = Child(field, "value")?.Elements().FirstOrDefault();
				if (name == null || typed == null) continue;
				record.Fields[name] = new SakeValue
				{
					Type = typed.Name.LocalName,
					Value = Child(typed, "value")?.Value ?? "",
				};
			}
		}

		// One ArrayOfRecordValue per record, one RecordValue per requested field (in request order).
		private static string ValuesXml(List<SakeRecord> records, List<string> fields)
		{
			if (records.Count == 0) return "<values />";

			var sb = new StringBuilder("<values>");
			foreach (var r in records)
			{
				sb.Append("<ArrayOfRecordValue>");
				foreach (var f in fields)
				{
					SakeValue v = f switch
					{
						"recordid" => new SakeValue { Type = "intValue", Value = r.RecordId.ToString() },
						"ownerid" => new SakeValue { Type = "intValue", Value = r.OwnerId.ToString() },
						_ => r.Fields.TryGetValue(f, out var stored) ? stored : new SakeValue { Type = "binaryDataValue", Value = "" },
					};
					sb.Append($"<RecordValue><{v.Type}><value>{SecurityElement.Escape(v.Value)}</value></{v.Type}></RecordValue>");
				}
				sb.Append("</ArrayOfRecordValue>");
			}
			return sb.Append("</values>").ToString();
		}

		private ContentResult SoapResponse(string method, string result, string inner)
		{
			var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
				"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
				$"<soap:Body><{method}Response xmlns=\"{Sake}\"><{method}Result>{result}</{method}Result>{inner}</{method}Response></soap:Body></soap:Envelope>";
			return Content(xml, "text/xml; charset=utf-8");
		}
	}
}
