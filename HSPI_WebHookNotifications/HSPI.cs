using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;
using HomeSeerAPI;
using Scheduler;

namespace HSPI_WebHookNotifications
{
	public class HSPI : HspiBase
	{
		private string webhookEndpoint = "http://192.168.1.10:19533/hs3_event";
		private readonly HttpClient httpClient;
		private readonly JavaScriptSerializer jsonSerializer;
		
		public HSPI() {
			Name = "WebHook Notifications";
			PluginIsFree = true;

			httpClient = new HttpClient();
			jsonSerializer = new JavaScriptSerializer();
		}

		public override string InitIO(string port) {
			Program.WriteLog("verbose", "InitIO");
			
			callbacks.RegisterEventCB(Enums.HSEvent.VALUE_SET, Name, InstanceFriendlyName());
			callbacks.RegisterEventCB(Enums.HSEvent.VALUE_CHANGE, Name, InstanceFriendlyName());
			callbacks.RegisterEventCB(Enums.HSEvent.STRING_CHANGE, Name, InstanceFriendlyName());

			return "";
		}

		public override void HSEvent(Enums.HSEvent eventType, object[] parameters) {
			Dictionary<string, object> dict = new Dictionary<string, object> {
				{"eventType", eventType.ToString()}
			};
			
			try {
				switch (eventType) {
					case Enums.HSEvent.VALUE_SET:
					case Enums.HSEvent.VALUE_CHANGE:
						dict.Add("address", (string) parameters[1]);
						dict.Add("newValue", ((double) parameters[2]).ToString(CultureInfo.InvariantCulture));
						dict.Add("oldValue", ((double) parameters[3]).ToString(CultureInfo.InvariantCulture));
						dict.Add("ref", (int) parameters[4]);
						break;

					case Enums.HSEvent.STRING_CHANGE:
						dict.Add("address", (string) parameters[1]);
						dict.Add("newValue", (string) parameters[2]);
						dict.Add("ref", (int) parameters[3]);
						break;
				}

				string json = jsonSerializer.Serialize(dict);
				Program.WriteLog("verbose", json);
				
				HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, webhookEndpoint) {
					Content = new StringContent(json, Encoding.UTF8, "application/json")
				};

				httpClient.SendAsync(req).ContinueWith((task) => { task.Result.Dispose(); });
			}
			catch (Exception ex) {
				Program.WriteLog("error", ex.ToString());
			}
		}
	}
}
