namespace HSPI_WebHookNotifications
{
	public class HSPI : HspiBase
	{
		public HSPI() {
			Name = "WebHook Notifications";
			PluginIsFree = true;
		}

		public override string InitIO(string port) {
			Program.WriteLog("verbose", "InitIO");

			return "";
		}
	}
}
