using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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
		private bool ignoreUnchangedEvents;
		
		private readonly WebHook[] webHooks;
		private int[] ignoredDeviceRefs;

		private const byte TOTAL_WEBHOOK_SLOTS = 5;

		public HSPI() {
			Name = PLUGIN_NAME;
			PluginIsFree = true;
			PluginSupportsConfigDevice = true;
			PluginSupportsConfigDeviceAll = true;
			
			jsonSerializer = new JavaScriptSerializer();
			deviceRefTimerState = new Dictionary<int, bool>();

			webHooks = new WebHook[TOTAL_WEBHOOK_SLOTS];
		}

		public override string InitIO(string port) {
			Program.WriteLog(LogType.Verbose, "InitIO");

			bool ignoreCertificateLegacy = hs.GetINISetting("Config", "ignore_invalid_certificates", "0", IniFilename) == "1";
			if (ignoreCertificateLegacy) {
				hs.SaveINISetting("Config", "ignore_invalid_certificates", "", IniFilename);
			}

			for (byte i = 1; i <= TOTAL_WEBHOOK_SLOTS; i++) {
				if (ignoreCertificateLegacy) {
					hs.SaveINISetting("IgnoreCertificate", "webhook" + i, "1", IniFilename);
				}

				string url = hs.GetINISetting("Config", "webhook_endpoint" + (i == 1 ? "" : i.ToString()), "", IniFilename);
				bool ignoreCertificate = hs.GetINISetting("IgnoreCertificate", "webhook" + i, "0", IniFilename) == "1";
				webHooks[i - 1] = url.Length > 0
					? new WebHook(url) { CheckServerCertificate = !ignoreCertificate }
					: null;
			}

			ignoreTimerEvents = hs.GetINISetting("Config", "ignore_timer_events", "0", IniFilename) == "1";
			ignoreUnchangedEvents = hs.GetINISetting("Config", "ignore_unchanged_events", "0", IniFilename) == "1";
			_updateIgnoredDeviceRefs();

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
				Program.WriteLog(LogType.Debug, "Ignoring event " + eventType + " because no webhook endpoint is configured.");
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
						if (ignoreUnchangedEvents && (double)parameters[2] == (double)parameters[3]) {
							Program.WriteLog(LogType.Verbose, $"Suppressing {eventType} for device {devRef} because its value did not change ({(double)parameters[2]} == {(double)parameters[3]})");
							return;
						}
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

				if (ignoredDeviceRefs.Contains(devRef)) {
					Program.WriteLog(LogType.Verbose, $"Suppressing {eventType} for device {devRef} because it is ignored.");
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
						Program.WriteLog(LogType.Verbose, $"Suppressing {eventType} for device {devRef} because it's a timer.");
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
							Program.WriteLog(LogType.Warn, "Got non-successful response code from WebHook " + webHook + ": " + task.Result.StatusCode);
						}

						task.Result.Dispose();
					}).ContinueWith((task) => {
						if (task.Exception?.InnerException != null) {
							Program.WriteLog(LogType.Error, $"Unable to send WebHook {webHook}: {getInnerExceptionMessage(task.Exception)}");
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
		
		public override string ConfigDevice(int @ref, string user, int userRights, bool newDevice) {
			if ((userRights & 2) != 2) {
				// User is not an admin
				return "<p><strong>Access Denied:</strong> You are not an administrative user.</p>";
			}
			
			bool deviceIsExempt = hs.GetINISetting("IgnoreDeviceRefs", @ref.ToString(), "", IniFilename) == "1";
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append(PageBuilderAndMenu.clsPageBuilder.FormStart("whn_device_config_form", "whn_device_config_form", "post"));

			clsJQuery.jqCheckBox checkBox = new clsJQuery.jqCheckBox("ignore_device", "Don't send webhook notifications for this feature", "deviceutility", true, true);
			checkBox.@checked = deviceIsExempt;
			stringBuilder.Append(checkBox.Build());

			stringBuilder.Append(PageBuilderAndMenu.clsPageBuilder.FormEnd());

			return stringBuilder.ToString();
		}

		public override Enums.ConfigDevicePostReturn ConfigDevicePost(int @ref, string data, string user, int userRights) {
			if ((userRights & 2) != 2) {
				// User is not an admin
				return Enums.ConfigDevicePostReturn.DoneAndCancel;
			}

			try {
				NameValueCollection postData = HttpUtility.ParseQueryString(data);
				bool deviceIsExempt = postData.Get("ignore_device") == "checked";
				hs.SaveINISetting("IgnoreDeviceRefs", @ref.ToString(), deviceIsExempt ? "1" : "", IniFilename);
				_updateIgnoredDeviceRefs();
				return Enums.ConfigDevicePostReturn.DoneAndCancelAndStay;
			} catch (Exception ex) {
				Program.WriteLog(LogType.Warn, ex.ToString());
				return Enums.ConfigDevicePostReturn.DoneAndCancelAndStay;
			}
		}

		private void _updateIgnoredDeviceRefs() {
			string[] ignoredRefs = hs.GetINISectionEx("IgnoreDeviceRefs", IniFilename);
			ignoredDeviceRefs = new int[ignoredRefs.Length];
			for (int i = 0; i < ignoredRefs.Length; i++) {
				ignoredDeviceRefs[i] = int.Parse(ignoredRefs[i].Split('=')[0]);
			}
			
			Program.WriteLog(LogType.Verbose, $"Loaded {ignoredDeviceRefs.Length} ignored devices");
		}

		public override string GetPagePlugin(string pageName, string user, int userRights, string queryString) {
			Program.WriteLog(LogType.Debug, "Requested page name " + pageName + " by user " + user + " with rights " + userRights);
			if (pageName != "WebHookNotificationConfig") {
				return "Unknown page " + pageName;
			}

			PageBuilderAndMenu.clsPageBuilder pageBuilder = new PageBuilderAndMenu.clsPageBuilder(pageName);
			
			if ((userRights & 2) != 2) {
				// User is not an admin
				pageBuilder.reset();
				pageBuilder.AddHeader(hs.GetPageHeader(pageName, "WebHook Notifications Settings", "", "", false, true));
				pageBuilder.AddBody("<p><strong>Access Denied:</strong> You are not an administrative user.</p>");
				pageBuilder.AddFooter(hs.GetPageFooter());
				pageBuilder.suppressDefaultFooter = true;

				return pageBuilder.BuildPage();
			}

			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append(
				PageBuilderAndMenu.clsPageBuilder.FormStart("whn_config_form", "whn_config_form", "post"));

			stringBuilder.Append(
				"<table width=\"1000px\" cellspacing=\"0\"><tr><td class=\"tableheader\" colspan=\"3\">Settings</td></tr>");

			clsJQuery.jqCheckBox checkBox;
			for (byte i = 1; i <= TOTAL_WEBHOOK_SLOTS; i++) {
				stringBuilder.Append(
					"<tr><td class=\"tablecell\" style=\"width:200px\" align=\"left\">WebHook URL " + i + ":</td>");
				stringBuilder.Append("<td class=\"tablecell\">");

				clsJQuery.jqTextBox textBox =
					new clsJQuery.jqTextBox("WebHookUrl" + i, "text", webHooks[i - 1]?.ToString(), pageName, 100, true);
				stringBuilder.Append(textBox.Build());
				stringBuilder.Append("</td><td class=\"tablecell\" style=\"width:300px\">");
				
				checkBox = new clsJQuery.jqCheckBox("IgnoreInvalidCertificate" + i, "Ignore invalid HTTPS certificate", pageName, true, true);
				checkBox.@checked = hs.GetINISetting("IgnoreCertificate", "webhook" + i, "0", IniFilename) == "1";
				stringBuilder.Append(checkBox.Build());
				
				stringBuilder.Append("</td></tr>");
			}

			stringBuilder.Append("<tr><td class=\"tablecell\" style=\"width:200px\" align=\"left\"></td>");
			stringBuilder.Append("<td class=\"tablecell\" colspan=\"2\">");
			checkBox = new clsJQuery.jqCheckBox(
				"IgnoreTimerEvents",
				"Suppress WebHooks for HS3 timers",
				pageName,
				true,
				true
			);
			checkBox.@checked = ignoreTimerEvents;
			stringBuilder.Append(checkBox.Build());
			stringBuilder.Append("</td></tr>");

			stringBuilder.Append("<tr><td class=\"tablecell\" style=\"width:250px\" align=\"left\"></td>");
			stringBuilder.Append("<td class=\"tablecell\" colspan=\"2\">");
			checkBox = new clsJQuery.jqCheckBox(
				"IgnoreUnchangedEvents",
				"Suppress WebHooks for events in which a device's value did not change (not effective on STRING_CHANGE events)",
				pageName,
				true,
				true
			);
			checkBox.@checked = ignoreUnchangedEvents;
			stringBuilder.Append(checkBox.Build());
			stringBuilder.Append("</td></tr>");
			
			stringBuilder.Append("</table>");

			clsJQuery.jqButton doneBtn = new clsJQuery.jqButton("DoneBtn", "Done", pageName, false);
			doneBtn.url = "/";
			stringBuilder.Append("<br />");
			stringBuilder.Append(doneBtn.Build());
			stringBuilder.Append("<br /><br />");
			
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
				
				for (byte i = 1; i <= TOTAL_WEBHOOK_SLOTS; i++) {
					string url = postData.Get("WebHookUrl" + i);
					if (webHooks[i - 1] != null) {
						webHooks[i - 1].Dispose();
						webHooks[i - 1] = null;
					}
					
					bool ignoreCertificate = postData.Get("IgnoreInvalidCertificate" + i) == "checked";
					hs.SaveINISetting("IgnoreCertificate", "webhook" + i, ignoreCertificate ? "1" : "0", IniFilename);
					
					if (url?.Length > 0) {
						webHooks[i - 1] = new WebHook(url) {
							CheckServerCertificate = !ignoreCertificate
						};
						hs.SaveINISetting("Config", "webhook_endpoint" + (i == 1 ? "" : i.ToString()), url, IniFilename);
						Program.WriteLog(LogType.Info, "Saved new WebHook URL " + i + ": " + url + " (ignore certificate: " + ignoreCertificate + ")");
					}
				}

				ignoreTimerEvents = postData.Get("IgnoreTimerEvents") == "checked";
				ignoreUnchangedEvents = postData.Get("IgnoreUnchangedEvents") == "checked";
				
				hs.SaveINISetting("Config", "ignore_timer_events", ignoreTimerEvents ? "1" : "0", IniFilename);
				hs.SaveINISetting("Config", "ignore_unchanged_events", ignoreUnchangedEvents ? "1" : "0", IniFilename);
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
