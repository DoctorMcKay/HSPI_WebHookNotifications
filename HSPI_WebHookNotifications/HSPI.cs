using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;
using HomeSeerAPI;
using Scheduler;

namespace HSPI_WebHookNotifications
{
	public class HSPI : HspiBase
	{
		private readonly string[] webhookEndpoints;
		private readonly HttpClient httpClient;
		private readonly JavaScriptSerializer jsonSerializer;

		private const byte TOTAL_WEBHOOK_SLOTS = 5;
		
		public HSPI() {
			Name = "WebHook Notifications";
			PluginIsFree = true;

			httpClient = new HttpClient();
			jsonSerializer = new JavaScriptSerializer();

			webhookEndpoints = new string[5];
		}

		public override string InitIO(string port) {
			Program.WriteLog("verbose", "InitIO");

			for (byte i = 1; i <= TOTAL_WEBHOOK_SLOTS; i++) {
				webhookEndpoints[i - 1] = hs.GetINISetting("Config", "webhook_endpoint" + (i == 1 ? "" : i.ToString()), "", IniFilename);
			}
			callbacks.RegisterEventCB(Enums.HSEvent.VALUE_SET, Name, InstanceFriendlyName());
			callbacks.RegisterEventCB(Enums.HSEvent.VALUE_CHANGE, Name, InstanceFriendlyName());
			callbacks.RegisterEventCB(Enums.HSEvent.STRING_CHANGE, Name, InstanceFriendlyName());
			
			// Config page
			hs.RegisterPage("WebHookNotificationConfig", Name, InstanceFriendlyName());
			WebPageDesc configLink = new WebPageDesc {
				plugInName = Name,
				plugInInstance = InstanceFriendlyName(),
				link = "WebHookNotificationConfig",
				linktext = "Settings",
				order = 1,
				page_title = "WebHook Notifications Settings"
			};
			callbacks.RegisterConfigLink(configLink);
			callbacks.RegisterLink(configLink);

			return "";
		}

		public override IPlugInAPI.strInterfaceStatus InterfaceStatus() {
			if (!IsAnyWebHookConfigured()) {
				return new IPlugInAPI.strInterfaceStatus {
					intStatus = IPlugInAPI.enumInterfaceStatus.WARNING,
					sStatus = "No WebHook URL is configured"
				};
			}
			
			// all gud
			return new IPlugInAPI.strInterfaceStatus {
				intStatus = IPlugInAPI.enumInterfaceStatus.OK
			};
		}

		public override void HSEvent(Enums.HSEvent eventType, object[] parameters) {
			if (!IsAnyWebHookConfigured()) {
				Program.WriteLog("debug",
					"Ignoring event " + eventType + " because no webhook endpoint is configured.");
				return;
			}
			
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

				for (byte i = 0; i < TOTAL_WEBHOOK_SLOTS; i++) {
					if (string.IsNullOrEmpty(webhookEndpoints[i])) {
						continue;
					}

					string endpoint = webhookEndpoints[i];
					HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint) {
						Content = new StringContent(json, Encoding.UTF8, "application/json")
					};

					httpClient.SendAsync(req).ContinueWith((task) => {
						if (!task.Result.IsSuccessStatusCode) {
							Program.WriteLog("warn",
								"Got non-successful response code from WebHook " + endpoint + ": " + task.Result.StatusCode);
						}

						task.Result.Dispose();
					});
				}
			}
			catch (Exception ex) {
				Program.WriteLog("error", ex.ToString());
			}
		}

		public override string GetPagePlugin(string pageName, string user, int userRights, string queryString) {
			Program.WriteLog("debug", "Requested page name " + pageName + " by user " + user + " with rights " + userRights);
			if (pageName != "WebHookNotificationConfig") {
				return "Unknown page " + pageName;
			}

			if ((userRights & 2) != 2) {
				// User is not an admin
				return "Access denied: you are not an administrative user.";
			}

			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append(
				PageBuilderAndMenu.clsPageBuilder.FormStart("whn_config_form", "whn_config_form", "post"));

			stringBuilder.Append(
				"<table width=\"1000px\" cellspacing=\"0\"><tr><td class=\"tableheader\" colspan=\"2\">Settings</td></tr>");

			for (byte i = 1; i <= TOTAL_WEBHOOK_SLOTS; i++) {
				stringBuilder.Append(
					"<tr><td class=\"tablecell\" colspan=\"1\" style=\"width:300px\" align=\"left\">WebHook URL " + i + ":</td>");
				stringBuilder.Append("<td class=\"tablecell\" colspan=\"1\">");

				clsJQuery.jqTextBox textBox =
					new clsJQuery.jqTextBox("WebHookUrl" + i, "text", webhookEndpoints[i - 1], pageName, 100, true);
				stringBuilder.Append(textBox.Build());
				stringBuilder.Append("</td></tr>");
			}

			stringBuilder.Append("</table>");

			clsJQuery.jqButton doneBtn = new clsJQuery.jqButton("DoneBtn", "Done", pageName, false);
			doneBtn.url = "/";
			stringBuilder.Append("<br />");
			stringBuilder.Append(doneBtn.Build());
			stringBuilder.Append("<br /><br />");
			
			PageBuilderAndMenu.clsPageBuilder pageBuilder = new PageBuilderAndMenu.clsPageBuilder(pageName);
			pageBuilder.reset();
			pageBuilder.AddHeader(hs.GetPageHeader(pageName, "WebHook Notifications Settings", "", "", false, true));
			pageBuilder.AddBody(stringBuilder.ToString());
			pageBuilder.AddFooter(hs.GetPageFooter());
			pageBuilder.suppressDefaultFooter = true;

			return pageBuilder.BuildPage();
		}
		
		public override string PostBackProc(string pageName, string data, string user, int userRights) {
			Program.WriteLog("debug", "PostBackProc page name " + pageName + " by user " + user + " with rights " + userRights);
			if (pageName != "WebHookNotificationConfig") {
				return "Unknown page " + pageName;
			}

			if ((userRights & 2) != 2) {
				// User is not an admin
				return "Access denied: you are not an administrative user.";
			}

			try {
				NameValueCollection postData = HttpUtility.ParseQueryString(data);

				for (byte i = 1; i <= TOTAL_WEBHOOK_SLOTS; i++) {
					string url = postData.Get("WebHookUrl" + i);
					if (url != null) {
						webhookEndpoints[i - 1] = url;
						hs.SaveINISetting("Config", "webhook_endpoint" + (i == 1 ? "" : i.ToString()), url, IniFilename);
						Program.WriteLog("info", "Saved new WebHook URL " + i + ": " + url);
					}
				}
			} catch (Exception ex) {
				Program.WriteLog("warn", ex.ToString());
			}

			return "";
		}

		private bool IsAnyWebHookConfigured() {
			for (byte i = 0; i < TOTAL_WEBHOOK_SLOTS; i++) {
				if (!string.IsNullOrEmpty(webhookEndpoints[i])) {
					return true;
				}
			}

			return false;
		}
	}
}
