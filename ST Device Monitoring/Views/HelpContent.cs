namespace ST_Device_Monitoring.Views;

/// <summary>One page in the help window.</summary>
public sealed class HelpTopic
{
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public override string ToString() => Title;
}

/// <summary>The text shown in Help (F1). Kept here so it is easy to extend.</summary>
public static class HelpContent
{
    public static IReadOnlyList<HelpTopic> Topics { get; } = new List<HelpTopic>
    {
        new()
        {
            Title = "Getting started",
            Body =
                "ST Device Monitoring checks a list of devices continuously and tells you when one of them " +
                "stops answering.\n\n" +
                "1.  Add device - give it a name, an IP address or hostname and an interval.\n" +
                "2.  Press Start. The button is a toggle: the program always opens stopped, the first press " +
                "starts monitoring and the next press stops it again.\n" +
                "3.  Select a device in the list to see its history graph at the bottom of the window.\n\n" +
                "Everything is stored in devices.json next to the program file, and every check can be " +
                "written to CSV files in the Logs folder.\n\n" +
                "Each device runs on its own, so adding, editing or deleting one device never disturbs the " +
                "others - the rest keep being checked without a pause."
        },
        new()
        {
            Title = "Check types: ICMP, TCP and SNMP",
            Body =
                "Every device can be checked in one of three ways:\n\n" +
                "ICMP ping\n" +
                "The classic ping. Needs no port and no credentials, but many PLCs, switches and firewalls " +
                "block ICMP - such a device looks dead even though it works.\n\n" +
                "TCP port\n" +
                "Opens a TCP connection to a port and closes it again. The device answers if the service is " +
                "listening, which is often a better answer than ICMP: 502 = Modbus, 102 = Siemens S7, " +
                "80/443 = web interface, 22 = SSH. The response time is the time it took to connect.\n\n" +
                "SNMP\n" +
                "Sends an SNMP v2c GET for sysUpTime with the community you enter (default 'public'). " +
                "On top of answering yes/no, SNMP can tell you what the device is: the first time a check " +
                "succeeds the program reads sysDescr and stores it in the Description column - and uses " +
                "sysName as the device name if you left the name empty. The button 'Read from device (SNMP)' " +
                "in the device dialog does the same on demand."
        },
        new()
        {
            Title = "Intervals, timeout and alarm threshold",
            Body =
                "Interval\n" +
                "How often the device is checked while it is up. 100 ms gives a very fast reaction, 1000 ms " +
                "is plenty for most equipment. The loop is self-adjusting: the time the check itself takes " +
                "is subtracted from the wait, so the interval holds.\n\n" +
                "Interval while down\n" +
                "As soon as the device is counted as down, this interval is used instead. It stops a 100 ms " +
                "device from hammering the network while it is offline. 0 = keep the normal interval.\n\n" +
                "Timeout\n" +
                "How long to wait for an answer before the check counts as failed. Keep it shorter than or " +
                "equal to the interval - otherwise a slow device is checked more slowly than you asked for. " +
                "The program warns you when you save such a combination.\n\n" +
                "Failures before alarm\n" +
                "How many failures in a row are needed before the device is shown as FAIL and an alarm is " +
                "raised. 1 reacts instantly but is sensitive to a single lost packet; 3 is a good compromise " +
                "on a busy network."
        },
        new()
        {
            Title = "Status, counters and the history graph",
            Body =
                "Status column\n" +
                "OK (green), Unstable (amber - one or more failures in a row, but below the alarm " +
                "threshold), FAIL (red), Paused (blue - see 'Group master'), Stopped and Disabled.\n\n" +
                "Counters\n" +
                "Checks, failures, fail %, failures in a row, the highest number in a row, number of outages " +
                "and min/average/max response time - all since the program started or since the last " +
                "'Reset counters'.\n\n" +
                "Loss 60s and Jitter 60s\n" +
                "Packet loss and jitter over the last 60 seconds only. Use these when you want to see how the " +
                "connection behaves right now: the totals get diluted after a day of running, but the 60 " +
                "second figures react immediately.\n\n" +
                "History graph\n" +
                "The last 120 checks for the selected device. Each bar is one check: green = answered, the " +
                "height is the response time, amber = slow (more than half the timeout), red full-height " +
                "bar = failed. The scale (highest response time in the window) is written under the graph."
        },
        new()
        {
            Title = "Failure logging and log suppression",
            Body =
                "A device that is down would otherwise write one identical failure line per interval - " +
                "36,000 lines an hour at 100 ms.\n\n" +
                "Stop logging after (failures)\n" +
                "The first N failures in a row are logged normally. On failure number N an extra note is " +
                "written, and after that the device is still checked - the counters, the graph and the " +
                "status keep updating - but nothing more is written to the log for that outage. The " +
                "Logging column shows 'Paused (n)' with the number of failures that were not logged.\n\n" +
                "When the device answers again a RECOVERED line is written with the downtime, how many " +
                "checks failed and how many of them were not logged. 0 = log every single failure.\n\n" +
                "Log every check\n" +
                "When this is on, every successful check is logged with its response time as well. When it " +
                "is off only failures are logged - but the first successful check of each device is always " +
                "written, so the log shows when the device was first seen online."
        },
        new()
        {
            Title = "Group master (uplink dependency)",
            Body =
                "Give the devices a group name (a quay, a cabinet, a site) and mark one of them as the " +
                "group's master - typically the switch or router the whole group sits behind.\n\n" +
                "While the master answers, everything in the group is checked as normal.\n\n" +
                "When the master is down, the rest of the group is paused: no checks are sent at all, no " +
                "failures are counted, no alarms are raised and the log stays quiet. Each device writes one " +
                "PAUSED line, and the status turns blue with 'Paused'. That way a dead uplink gives you one " +
                "alarm - for the switch - instead of thirty.\n\n" +
                "When the master answers again, every device writes a RESUMED line and the checks continue.\n\n" +
                "A group can only have one master, and the master itself is always checked. If the master is " +
                "stopped or disabled the gate stays open, so the group is monitored normally."
        },
        new()
        {
            Title = "Group schedules (periodic checking)",
            Body =
                "Not every group needs to be watched every second. Under 'Schedules…' a group can be set to " +
                "run periodically instead: check for a short while, then rest until the next time.\n\n" +
                "Run every\n" +
                "The interval between two runs - from one minute up to a week. This is the number that " +
                "answers 'how often should this group be checked': every 15 minutes, every hour, every " +
                "24 hours.\n\n" +
                "Each run lasts\n" +
                "How long the group is checked each time, in seconds. During a run every device uses its own " +
                "interval and timeout exactly as usual, so a two minute run at 1000 ms gives about 120 " +
                "measurements per device - enough to see packet loss and jitter, not just yes/no.\n\n" +
                "Only between / on these days\n" +
                "An optional time window and a set of weekdays, for example 07:00-17:00 Monday to Friday. " +
                "Two identical times mean all day. An end time earlier than the start time means the window " +
                "crosses midnight, like 22:00 to 06:00 - such a window belongs to the day it started on, so " +
                "a window that starts Friday evening still finishes on Saturday morning.\n\n" +
                "What happens between two runs\n" +
                "The whole group is paused, master included: nothing is sent, no failures are counted, no " +
                "alarms are raised and the counters are frozen where they were. The log gets one line when " +
                "the run finishes and one when the next run starts, and the status column shows 'Paused' " +
                "with the time of the next run in the error text column.\n\n" +
                "Predictable times\n" +
                "The runs are anchored to the start of the active window, so 'every 30 minutes from 07:00' " +
                "always lands on 07:00, 07:30, 08:00 and so on - also after the program has been restarted. " +
                "They do not drift.\n\n" +
                "Groups that have no schedule, and groups whose schedule is switched off, keep being checked " +
                "continuously exactly as before. Changing a schedule never restarts a check loop: it only " +
                "opens or closes the gate in front of the group, so the other devices are not disturbed.\n\n" +
                "Typical uses: a remote site on a metered or slow connection that only needs an hourly health " +
                "check; a customer network you may only touch outside production hours; or a long unattended " +
                "test where a five minute sample every hour says as much as a week of continuous pinging - " +
                "and keeps the log a fraction of the size."
        },
        new()
        {
            Title = "Notifications and alarms",
            Body =
                "Settings -> Notifications. An alarm is raised when a device passes its 'failures before " +
                "alarm' threshold, and again when it comes back.\n\n" +
                "On screen: a balloon notification from the tray icon and a sound (a .wav file of your own " +
                "or the standard Windows sound).\n\n" +
                "E-mail: SMTP server, port, user, password, from and one or more recipients separated by ; " +
                "or ,. The password is encrypted with the Windows DPAPI for the user who entered it, so " +
                "devices.json never contains it in clear text.\n\n" +
                "Webhook: posts a JSON message (device, host, state, time, downtime) to a URL - use it for " +
                "Teams, Slack or a ticket system.\n\n" +
                "Throttling: the minimum number of seconds between two notifications for the same device. " +
                "A 'device is back' message is always sent.\n\n" +
                "Notifications run in the background, so a slow or dead mail server can never delay the " +
                "monitoring. Use 'Send test notification' to check the setup."
        },
        new()
        {
            Title = "Log files, size limit and zip",
            Body =
                "Three kinds of file are written in the Logs folder (Settings -> Logging):\n\n" +
                "ping_yyyyMMdd.csv - every check including response time (if 'Log every check' is on).\n" +
                "errors_yyyyMMdd.csv - failures, suppression notices, pauses and recoveries only.\n" +
                "summary_yyyyMMdd.csv - one line per device with uptime %, outages, longest outage and " +
                "total downtime.\n\n" +
                "The files use ; as separator and start with a sep=; line, so they open correctly in Excel. " +
                "Files older than the retention setting are deleted at startup - zip archives included.\n\n" +
                "Size limit per file\n" +
                "New file + zip: when the file passes the limit it is renamed with a timestamp " +
                "(ping_20260819_143012.csv), compressed to .zip in the background and the .csv is deleted. " +
                "Logging continues in a fresh file without losing an entry, and the whole history is kept.\n\n" +
                "Ring buffer: the file keeps its name and the oldest lines are removed when the limit is " +
                "reached, so it always holds the newest entries and never grows past the limit. A TRIMMED " +
                "line marks where older entries were cut away. Nothing is archived - choose zip if the " +
                "history has to be kept.\n\n" +
                "If the disk cannot keep up, log entries are dropped rather than slowing the checks down; " +
                "the number of dropped entries is shown in the status bar."
        },
        new()
        {
            Title = "Daily report",
            Body =
                "summary_yyyyMMdd.csv holds one line per device for the day: number of checks, failures, " +
                "uptime %, number of outages, the longest single outage, total downtime and average/maximum " +
                "response time.\n\n" +
                "It is written automatically just after midnight for the day that finished (can be turned " +
                "off in Settings -> Logging), and the 'Daily report' button writes the current day as it " +
                "stands right now. It is much faster to read than the raw logs when someone asks how the " +
                "network behaved yesterday."
        },
        new()
        {
            Title = "Groups, filters and the device list",
            Body =
                "Search filters on name, IP/hostname and group as you type. The group drop-down limits the " +
                "list to one group, and 'Only failures' shows only devices that are failing or unstable - " +
                "useful when the list is long. 'Clear' resets all three.\n\n" +
                "Every column sizes itself to its content and can be sorted by clicking its header and " +
                "resized by dragging.\n\n" +
                "Several rows can be selected with Ctrl-click or Shift-click. Delete or the Del key removes " +
                "all selected devices, and Enable/disable applies to all of them. Double-click a row to edit " +
                "the device."
        },
        new()
        {
            Title = "Import, export and IP range scan",
            Body =
                "Export writes the device list as a CSV file (name, host, group, check type, port, " +
                "intervals, timeout, thresholds, community, description and group master), and Import reads " +
                "the same format back. Devices that already exist - same host, port and check type - are " +
                "skipped, and lines with errors are reported instead of being imported silently.\n\n" +
                "Scan range asks for a from- and to-address and checks the whole range in parallel. Choose " +
                "ICMP, a TCP port or SNMP; in SNMP mode the description of every device that answers is " +
                "read at the same time. The result can be edited (names) and ticked off, and only the ticked " +
                "devices are added.\n\n" +
                "Both the address fields and the device dialog validate IPv4 strictly: 192.168.1.300 or " +
                "192.168.1 is rejected while you type instead of quietly being treated as a hostname."
        },
        new()
        {
            Title = "Run mode: application or Windows service",
            Body =
                "Settings -> Run mode.\n\n" +
                "Normal application: monitoring runs while the program is open. It can minimise to the " +
                "system tray, close to the tray and start with Windows for the current user.\n\n" +
                "Windows service: press 'Install service'. Windows then starts the program headless at " +
                "boot - before anyone logs in - and restarts it automatically if it stops. The service " +
                "writes the same CSV logs and daily summaries and sends the same e-mail/webhook alerts, but " +
                "has no user interface. Installing, starting, stopping and removing all need administrator " +
                "rights, so a UAC prompt appears.\n\n" +
                "Notes: the service reads devices.json when it starts, so restart it after changing the " +
                "device list. Avoid running the service and this window at the same time unless you want " +
                "both writing to the same log folder - the program warns you at startup when the service is " +
                "running. The service writes start/stop lines to service.log next to the program file, and " +
                "an SMTP password encrypted by your user cannot be read by a service running as LocalSystem."
        },
        new()
        {
            Title = "Finding a device without knowing its IP",
            Body =
                "Discover… finds devices that cannot be pinged, for example a PLC on 192.168.44.1 while this " +
                "machine is on 192.168.1.10. Broadcast traffic reaches the network card regardless of subnet.\n\n" +
                "It listens for what devices send by themselves (DHCP, NetBIOS, mDNS, LLMNR, SSDP, WS-Discovery, " +
                "BACnet, EtherNet/IP), sends broadcast queries that provoke an answer, and reads the Windows ARP " +
                "table. Each device is listed with the network adapter it was seen on, its MAC, and whether it is " +
                "in one of this machine's own subnets.\n\n" +
                "Three ways forward from there:\n\n" +
                "Match subnet - gives this machine an extra address in the device's subnet (netsh, administrator " +
                "rights). Then the device can be reached, typically to open its web page and set the address you " +
                "want on the device itself. 'Restore DHCP' puts the adapter back.\n\n" +
                "Give address (DHCP) - for a device that only asks by DHCP and gets no answer (it usually ends up " +
                "on 169.254.x). A small DHCP server runs on the one adapter you choose and, by default, answers " +
                "only the MAC of that device. Set the address the device should get, start the server, and the " +
                "device is added to the monitoring list when it accepts.\n\n" +
                "Manufacturer column - the name behind the MAC address, the same lookup Wireshark does. The names " +
                "come from an IEEE OUI list: press 'Download list' once (needs internet) to fetch oui.csv next to " +
                "the program, or copy in Wireshark's 'manuf' file. Without such a file the column stays empty.\n\n" +
                "A device that only sends ARP - a controller looking for its gateway - is only picked up while this " +
                "machine holds an address in its subnet; that is when Windows records it in the ARP table, which is " +
                "re-read every two seconds during discovery.\n\n" +
                "Profinet DCP and LLDP/CDP are not included: they run directly on Ethernet without IP and need a " +
                "packet driver (Npcap), which cannot be shipped with the application."
        },
        new()
        {
            Title = "Updating from GitHub",
            Body =
                "New versions are published as releases on GitHub, and the program can install them over " +
                "itself. Press 'Updates…' in the toolbar, or open Settings -> Updates.\n\n" +
                "What the check does\n" +
                "It reads the newest release from the public GitHub API and compares the tag (v1.30.0) with " +
                "the version this copy was built as. Nothing about the machine is sent, no account is needed " +
                "and nothing is installed on its own. With 'Look for a new version when the program starts' " +
                "the check runs once, a few seconds after start; if a newer release exists, a yellow note " +
                "appears in the status bar. If there is no internet the check fails silently.\n\n" +
                "Which file is downloaded\n" +
                "A release carries two exe files: the big one that runs on its own and the small one that " +
                "needs the .NET 8 Desktop Runtime. The program knows which of the two it was published as and " +
                "picks the matching file; a build straight from Visual Studio gets the big one, because that " +
                "one runs everywhere. You can choose the other one in the File box.\n\n" +
                "How the swap works\n" +
                "A running program holds its own exe locked, so it cannot overwrite itself. The new file is " +
                "downloaded to the temporary folder, its size and - when GitHub reports one - its SHA-256 " +
                "checksum are verified, and only then is a small script started. The script keeps trying to " +
                "copy the new file over the old one until the program has closed, then starts it again and " +
                "deletes itself. If the copy has not succeeded after 90 seconds it stops and tells you where " +
                "the downloaded file is, so nothing is left half-finished.\n\n" +
                "Going back\n" +
                "The version that was replaced is kept next to the program as " +
                "'ST Device Monitoring.exe.previous'. If a new version misbehaves, close the program, delete " +
                "the exe and rename that file back.\n\n" +
                "The Windows service\n" +
                "If the service is installed the script stops it before the swap and starts it again " +
                "afterwards. That needs administrator rights, so Windows shows a UAC prompt. The same happens " +
                "when the program sits in a folder you cannot write to, such as Program Files.\n\n" +
                "Skipping a version\n" +
                "'Skip this version' stops that one release from being announced again - a later one still " +
                "is. Settings -> Updates has a button to clear it.\n\n" +
                "Monitoring stops while the program restarts, so the update always asks first. Devices.json, " +
                "the logs and every setting are untouched by an update."
        },
        new()
        {
            Title = "Practical advice",
            Body =
                "· Use TCP or SNMP for equipment that blocks ICMP - a 'dead' PLC is often just a firewall.\n" +
                "· 100 ms is rarely needed for more than a couple of key devices. Every device at 100 ms " +
                "writes about 36,000 lines an hour when every check is logged.\n" +
                "· Set 'Interval while down' to 1000-5000 ms so a dead device does not flood the network.\n" +
                "· Mark the switch as the group master - it turns thirty alarms into one.\n" +
                "· Put the log folder on a local SSD rather than a network drive.\n" +
                "· Look at Loss 60s rather than Fail % when troubleshooting something happening right now.\n" +
                "· Reset counters before a test run, so the figures cover the test and nothing else."
        }
    };
}
