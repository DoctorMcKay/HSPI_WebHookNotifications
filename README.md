# WebHook Notifications for HomeSeer HS3

This is a very simple free, open-source plugin to send HTTP POST requests to a configured URL whenever
a device in HS3 has its value changed or set.

This might be useful if you want to control some other system based on changes in HS3 devices.

# Installation

Until this plugin is included in the HomeSeer plugin updater, you will need to install it manually.
Download HSPI_WebHookNotifications.exe from the [latest release](https://github.com/DoctorMcKay/HSPI_WebHookNotifications/releases/latest)
and drop it into your HS3 directory (where HomeSeerAPI.dll is located). Then restart HS3.

**Make sure you don't change the filename or the plugin won't work.** No additional DLLs are required.

# Configuration

Enable the plugin from the `Plug-Ins > Manage` page. Once enabled, it should warn you that no WebHook URL is configured.
Click on the plugin's name to access the settings page, where you should enter the URL you want the plugin to make an
HTTP POST request to whenever an HS3 device has its value set or changed.

# WebHook Format

WebHook requests are HTTP POST requests with a `Content-Type` of `application/json` and a JSON-encoded request body. Every
request contains an `eventType` property, which is a string indicating what type of event just happened. The rest of the
JSON object contains different properties depending on what type of event happened. Possible events and their associated data:

- `VALUE_SET` - Seemingly happens whenever a device's value is set to its current value (the value didn't change)
	- `address` - The string address of the device in question. If the device has a code set, then this will be the device's address with the code appended.
		- Identical to the "Address" column in HS3
	- `newValue` - The value this device just had set, as a string
	- `oldValue` - The value this device had previously, as a string (same as `newValue` for this event)
	- `ref` - The ref number of the device that got set
- `VALUE_CHANGE` - Seemingly happens whenever a device's value is set to a new, different value
	- `address` - The string address of the device in question. If the device has a code set, then this will be the device's address with the code appended.
		- Identical to the "Address" column in HS3
	- `newValue` - The value this device just had set, as a string
	- `oldValue` - The value this device had previously, as a string
	- `ref` - The ref number of the device that got set
- `STRING_CHANGE` - Seemingly happens whenever a device's string value is set to a new, different value
	- `address` - The string address of the device in question. If the device has a code set, then this will be the device's address with the code appended.
		- Identical to the "Address" column in HS3
	- `newValue` - The value this device just had set, as a string
	- `ref` - The ref number of the device that got set

# Software Support

This plugin is tested and works under:

- Windows (Windows 10 version 1803)
- Linux (Raspbian 9 Stretch)
- Mono version 4.6.2
