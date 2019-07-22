using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using HomeSeerAPI;
using Scheduler;
using Scheduler.Classes;

namespace HSPI_WebHookNotifications
{
	public class HSPI : HspiBase
	{
		public const string PLUGIN_NAME = "WebHook Notifications";
		
		private readonly JavaScriptSerializer jsonSerializer;
		private readonly Dictionary<int, bool> deviceRefTimerState;
		private bool ignoreTimerEvents;
		private bool ignoringInvalidCertificates;
		
		private readonly WebHook[] webHooks;

		private const byte TOTAL_WEBHOOK_SLOTS = 5;
		
		public HSPI() {
			Name = PLUGIN_NAME;
			PluginIsFree = true;
			
			jsonSerializer = new JavaScriptSerializer();
			deviceRefTimerState = new Dictionary<int, bool>();

			webHooks = new WebHook[TOTAL_WEBHOOK_SLOTS];
		}

		public override string InitIO(string port) {
			Program.WriteLog(LogType.Verbose, "InitIO");

			ignoringInvalidCertificates = hs.GetINISetting("Config", "ignore_invalid_certificates", "0", IniFilename) == "1";
			for (byte i = 1; i <= TOTAL_WEBHOOK_SLOTS; i++) {
				string url = hs.GetINISetting("Config", "webhook_endpoint" + (i == 1 ? "" : i.ToString()), "", IniFilename);
				webHooks[i - 1] = url.Length > 0
					? new WebHook(url) { CheckServerCertificate = !ignoringInvalidCertificates }
					: null;
			}
			
			ignoreTimerEvents = hs.GetINISetting("Config", "ignore_timer_events", "0", IniFilename) == "1";
			
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
				Program.WriteLog(LogType.Debug,
					"Ignoring event " + eventType + " because no webhook endpoint is configured.");
				return;
			}

			Dictionary<string, object> dict = new Dictionary<string, object> {
				{"eventType", eventType.ToString()}
			};
			
			try {
				int devRef;
				
				switch (eventType) {
					case Enums.HSEvent.VALUE_SET:
					case Enums.HSEvent.VALUE_CHANGE:
						devRef = (int) parameters[4];
						dict.Add("address", (string) parameters[1]);
						dict.Add("newValue", ((double) parameters[2]).ToString(CultureInfo.InvariantCulture));
						dict.Add("oldValue", ((double) parameters[3]).ToString(CultureInfo.InvariantCulture));
						dict.Add("ref", devRef);
						break;

					case Enums.HSEvent.STRING_CHANGE:
						devRef = (int) parameters[3];
						dict.Add("address", (string) parameters[1]);
						dict.Add("newValue", (string) parameters[2]);
						dict.Add("ref", devRef);
						break;
					
					default:
						Program.WriteLog(LogType.Warn, "Unknown event type " + eventType);
						return;
				}
				
				if (ignoreTimerEvents) {
					if (!deviceRefTimerState.ContainsKey(devRef)) {
						// We need to check if this is a timer
						DeviceClass device = (DeviceClass) hs.GetDeviceByRef(devRef);
						PlugExtraData.clsPlugExtraData deviceData = device.get_PlugExtraData_Get(hs);
						deviceRefTimerState[devRef] = deviceData.GetNamed("timername") != null && device.get_Interface(hs) == "";
					}

					if (deviceRefTimerState[devRef]) {
						// This is a timer.
						Program.WriteLog(LogType.Verbose,
							"Suppressing " + eventType + " for device " + devRef + " because it's a timer.");
						return;
					}
				}

				string json = jsonSerializer.Serialize(dict);
				Program.WriteLog(LogType.Verbose, json);

				for (byte i = 0; i < TOTAL_WEBHOOK_SLOTS; i++) {
					if (webHooks[i] == null) {
						continue;
					}

					WebHook webHook = webHooks[i];
					
					webHook.Execute(new StringContent(json, Encoding.UTF8, "application/json")).ContinueWith((task) => {
						Program.WriteLog(LogType.Verbose, "Sent WebHook " + webHook + " with status code " + task.Result.StatusCode);
						if (!task.Result.IsSuccessStatusCode) {
							Program.WriteLog(LogType.Warn,
								"Got non-successful response code from WebHook " + webHook + ": " + task.Result.StatusCode);
						}

						task.Result.Dispose();
					}).ContinueWith((task) => {
						if (task.Exception?.InnerException != null) {
							Program.WriteLog(LogType.Error, string.Format(
								"Unable to send WebHook {0}: {1}",
								webHook,
								getInnerExceptionMessage(task.Exception)
							));
						}
					}, TaskContinuationOptions.OnlyOnFaulted);
				}
			}
			catch (Exception ex) {
				Program.WriteLog(LogType.Error, ex.ToString());
			}
		}

		private string getInnerExceptionMessage(Exception ex) {
			if (ex.InnerException != null) {
				return getInnerExceptionMessage(ex.InnerException);
			}

			return ex.Message;
		}

		public override string GetPagePlugin(string pageName, string user, int userRights, string queryString) {
			Program.WriteLog(LogType.Debug, "Requested page name " + pageName + " by user " + user + " with rights " + userRights);
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
					new clsJQuery.jqTextBox("WebHookUrl" + i, "text", webHooks[i - 1].ToString(), pageName, 100, true);
				stringBuilder.Append(textBox.Build());
				stringBuilder.Append("</td></tr>");
			}

			stringBuilder.Append(
				"<tr><td class=\"tablecell\" colspan=\"1\" style=\"width:300px\" align=\"left\">Ignore Timers:</td>");
			stringBuilder.Append("<td class=\"tablecell\" colspan=\"1\">");
			clsJQuery.jqCheckBox checkBox = new clsJQuery.jqCheckBox("IgnoreTimerEvents",
				"Suppress WebHooks for HS3 timers", pageName, true, true);
			stringBuilder.Append(checkBox.Build());
			stringBuilder.Append("</td></tr>");
			
			stringBuilder.Append("<tr><td class=\"tablecell\" colspan=\"1\" style=\"width:300px\" align=\"left\">Ignore Invalid HTTPS Certificates:</td>");
			stringBuilder.Append("<td class=\"tablecell\" colspan=\"1\">");
			checkBox = new clsJQuery.jqCheckBox("IgnoreInvalidCertificates",
				"Don't fail requests if an invalid certificate is encountered", pageName, true, true);
			stringBuilder.Append(checkBox.Build());
			stringBuilder.Append("</td></tr>");

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
			Program.WriteLog(LogType.Debug, "PostBackProc page name " + pageName + " by user " + user + " with rights " + userRights);
			if (pageName != "WebHookNotificationConfig") {
				return "Unknown page " + pageName;
			}

			if ((userRights & 2) != 2) {
				// User is not an admin
				return "Access denied: you are not an administrative user.";
			}

			try {
				NameValueCollection postData = HttpUtility.ParseQueryString(data);

				ignoringInvalidCertificates = postData.Get("IgnoreInvalidCertificates") == "checked";
				
				for (byte i = 1; i <= TOTAL_WEBHOOK_SLOTS; i++) {
					string url = postData.Get("WebHookUrl" + i);
					if (webHooks[i - 1] != null) {
						webHooks[i - 1].Dispose();
						webHooks[i - 1] = null;
					}
					
					if (url?.Length > 0) {
						webHooks[i - 1] = new WebHook(url) {
							CheckServerCertificate = !ignoringInvalidCertificates
						};
						hs.SaveINISetting("Config", "webhook_endpoint" + (i == 1 ? "" : i.ToString()), url, IniFilename);
						Program.WriteLog(LogType.Info, "Saved new WebHook URL " + i + ": " + url);
					}
				}

				ignoreTimerEvents = postData.Get("IgnoreTimerEvents") == "checked";
				
				hs.SaveINISetting("Config", "ignore_timer_events", ignoreTimerEvents ? "1" : "0", IniFilename);
				hs.SaveINISetting("Config", "ignore_invalid_certificates", ignoringInvalidCertificates ? "1" : "0", IniFilename);
			} catch (Exception ex) {
				Program.WriteLog(LogType.Warn, ex.ToString());
			}

			return "";
		}

		private bool IsAnyWebHookConfigured() {
			for (byte i = 0; i < TOTAL_WEBHOOK_SLOTS; i++) {
				if (webHooks[i] != null) {
					return true;
				}
			}

			return false;
		}
	}
}
