using System;
using System.Collections.Generic;

namespace Nexus.Service.Calendar.Ics;

/// <summary>
/// Windows time zone id to IANA ids, for feeds that name a zone the host cannot
/// look up. Outlook feeds name Windows zones (Linux and macOS cannot resolve those
/// in invariant globalization mode) and Google feeds name IANA zones (Windows
/// cannot resolve those in invariant mode, which drops the ICU conversion).
/// The first IANA id of each row is the primary one. Hand-maintained subset of the
/// CLDR windowsZones table covering the territories people actually subscribe from.
/// </summary>
internal static class WindowsZoneMap
{
    private static readonly string[] Rows =
    {
        "Dateline Standard Time|Etc/GMT+12",
        "UTC-11|Etc/GMT+11,Pacific/Pago_Pago,Pacific/Niue,Pacific/Midway",
        "Aleutian Standard Time|America/Adak",
        "Hawaiian Standard Time|Pacific/Honolulu,Pacific/Johnston",
        "Marquesas Standard Time|Pacific/Marquesas",
        "Alaskan Standard Time|America/Anchorage,America/Juneau,America/Nome,America/Sitka,America/Yakutat,America/Metlakatla",
        "UTC-09|Etc/GMT+9,Pacific/Gambier",
        "Pacific Standard Time (Mexico)|America/Tijuana,America/Santa_Isabel",
        "UTC-08|Etc/GMT+8,Pacific/Pitcairn",
        "Pacific Standard Time|America/Los_Angeles,America/Vancouver,US/Pacific,PST8PDT",
        "US Mountain Standard Time|America/Phoenix,America/Creston,America/Dawson_Creek,America/Fort_Nelson,America/Hermosillo,Etc/GMT+7,US/Arizona",
        "Mountain Standard Time (Mexico)|America/Mazatlan,America/Chihuahua,America/Ciudad_Juarez",
        "Mountain Standard Time|America/Denver,America/Edmonton,America/Boise,America/Cambridge_Bay,America/Inuvik,America/Yellowknife,America/Ojinaga,US/Mountain,MST7MDT",
        "Yukon Standard Time|America/Whitehorse,America/Dawson",
        "Central America Standard Time|America/Guatemala,America/Belize,America/Costa_Rica,America/El_Salvador,America/Tegucigalpa,America/Managua,Etc/GMT+6,Pacific/Galapagos",
        "Central Standard Time|America/Chicago,America/Winnipeg,America/Menominee,America/Indiana/Knox,America/Indiana/Tell_City,America/North_Dakota/Beulah,America/North_Dakota/Center,America/North_Dakota/New_Salem,America/Matamoros,America/Rainy_River,America/Rankin_Inlet,America/Resolute,US/Central,CST6CDT",
        "Easter Island Standard Time|Pacific/Easter",
        "Central Standard Time (Mexico)|America/Mexico_City,America/Bahia_Banderas,America/Merida,America/Monterrey",
        "Canada Central Standard Time|America/Regina,America/Swift_Current",
        "SA Pacific Standard Time|America/Bogota,America/Rio_Branco,America/Eirunepe,America/Guayaquil,America/Jamaica,America/Cayman,America/Panama,America/Lima,Etc/GMT+5",
        "Eastern Standard Time (Mexico)|America/Cancun",
        "Eastern Standard Time|America/New_York,America/Nassau,America/Toronto,America/Detroit,America/Indiana/Petersburg,America/Kentucky/Louisville,America/Kentucky/Monticello,America/Iqaluit,America/Nipigon,America/Thunder_Bay,US/Eastern,EST5EDT",
        "Haiti Standard Time|America/Port-au-Prince",
        "Cuba Standard Time|America/Havana",
        "US Eastern Standard Time|America/Indianapolis,America/Indiana/Indianapolis,America/Indiana/Marengo,America/Indiana/Vevay,America/Indiana/Vincennes,America/Indiana/Winamac",
        "Turks And Caicos Standard Time|America/Grand_Turk",
        "Paraguay Standard Time|America/Asuncion",
        "Atlantic Standard Time|America/Halifax,Atlantic/Bermuda,America/Glace_Bay,America/Goose_Bay,America/Moncton,America/Thule",
        "Venezuela Standard Time|America/Caracas",
        "Central Brazilian Standard Time|America/Cuiaba,America/Campo_Grande",
        "SA Western Standard Time|America/La_Paz,America/Manaus,America/Puerto_Rico,America/Barbados,America/Santo_Domingo,America/Port_of_Spain,America/Martinique,America/Guyana,America/Anguilla,America/Antigua,America/Aruba,America/Curacao,America/Dominica,America/Grenada,America/Guadeloupe,America/St_Kitts,America/St_Lucia,America/St_Thomas,America/St_Vincent,America/Tortola,Etc/GMT+4",
        "Pacific SA Standard Time|America/Santiago",
        "Newfoundland Standard Time|America/St_Johns",
        "Tocantins Standard Time|America/Araguaina",
        "E. South America Standard Time|America/Sao_Paulo",
        "SA Eastern Standard Time|America/Cayenne,America/Fortaleza,America/Belem,America/Maceio,America/Recife,America/Santarem,Antarctica/Rothera,America/Paramaribo,Atlantic/Stanley,Etc/GMT+3",
        "Argentina Standard Time|America/Argentina/Buenos_Aires,America/Buenos_Aires,America/Argentina/Catamarca,America/Argentina/Cordoba,America/Argentina/Jujuy,America/Argentina/La_Rioja,America/Argentina/Mendoza,America/Argentina/Rio_Gallegos,America/Argentina/Salta,America/Argentina/San_Juan,America/Argentina/San_Luis,America/Argentina/Tucuman,America/Argentina/Ushuaia",
        "Greenland Standard Time|America/Nuuk,America/Godthab",
        "Montevideo Standard Time|America/Montevideo",
        "Magallanes Standard Time|America/Punta_Arenas",
        "Saint Pierre Standard Time|America/Miquelon",
        "Bahia Standard Time|America/Bahia",
        "UTC-02|Etc/GMT+2,America/Noronha,Atlantic/South_Georgia",
        "Azores Standard Time|Atlantic/Azores,America/Scoresbysund",
        "Cape Verde Standard Time|Atlantic/Cape_Verde,Etc/GMT+1",
        "UTC|Etc/UTC,Etc/GMT,UTC,GMT,Etc/UCT,Etc/Universal,Etc/Zulu",
        "GMT Standard Time|Europe/London,Europe/Dublin,Europe/Lisbon,Atlantic/Canary,Atlantic/Madeira,Atlantic/Faeroe,Atlantic/Faroe,Europe/Isle_of_Man,Europe/Jersey,Europe/Guernsey,GB",
        "Greenwich Standard Time|Atlantic/Reykjavik,Africa/Abidjan,Africa/Accra,Africa/Bamako,Africa/Banjul,Africa/Bissau,Africa/Conakry,Africa/Dakar,Africa/Freetown,Africa/Lome,Africa/Monrovia,Africa/Nouakchott,Africa/Ouagadougou,Atlantic/St_Helena",
        "Sao Tome Standard Time|Africa/Sao_Tome",
        "Morocco Standard Time|Africa/Casablanca,Africa/El_Aaiun",
        "W. Europe Standard Time|Europe/Berlin,Europe/Andorra,Europe/Vienna,Europe/Zurich,Europe/Busingen,Europe/Gibraltar,Europe/Rome,Europe/Vaduz,Europe/Luxembourg,Europe/Monaco,Europe/Malta,Europe/Amsterdam,Europe/Oslo,Europe/Stockholm,Arctic/Longyearbyen,Europe/San_Marino,Europe/Vatican",
        "Central Europe Standard Time|Europe/Budapest,Europe/Tirane,Europe/Prague,Europe/Podgorica,Europe/Belgrade,Europe/Ljubljana,Europe/Bratislava",
        "Romance Standard Time|Europe/Paris,Europe/Brussels,Europe/Copenhagen,Europe/Madrid,Africa/Ceuta",
        "Central European Standard Time|Europe/Warsaw,Europe/Sarajevo,Europe/Zagreb,Europe/Skopje",
        "W. Central Africa Standard Time|Africa/Lagos,Africa/Algiers,Africa/Bangui,Africa/Brazzaville,Africa/Douala,Africa/Kinshasa,Africa/Libreville,Africa/Luanda,Africa/Malabo,Africa/Ndjamena,Africa/Niamey,Africa/Porto-Novo,Africa/Tunis,Etc/GMT-1",
        "GTB Standard Time|Europe/Bucharest,Asia/Nicosia,Asia/Famagusta,Europe/Athens",
        "Middle East Standard Time|Asia/Beirut",
        "Egypt Standard Time|Africa/Cairo",
        "E. Europe Standard Time|Europe/Chisinau",
        "Syria Standard Time|Asia/Damascus",
        "West Bank Standard Time|Asia/Hebron,Asia/Gaza",
        "South Africa Standard Time|Africa/Johannesburg,Africa/Maputo,Africa/Harare,Africa/Lusaka,Africa/Gaborone,Africa/Bujumbura,Africa/Kigali,Africa/Lubumbashi,Africa/Blantyre,Africa/Maseru,Africa/Mbabane,Etc/GMT-2",
        "FLE Standard Time|Europe/Kiev,Europe/Kyiv,Europe/Helsinki,Europe/Riga,Europe/Sofia,Europe/Tallinn,Europe/Vilnius,Europe/Mariehamn,Europe/Uzhgorod,Europe/Zaporozhye",
        "Israel Standard Time|Asia/Jerusalem,Asia/Tel_Aviv",
        "South Sudan Standard Time|Africa/Juba",
        "Kaliningrad Standard Time|Europe/Kaliningrad",
        "Sudan Standard Time|Africa/Khartoum",
        "Libya Standard Time|Africa/Tripoli",
        "Namibia Standard Time|Africa/Windhoek",
        "Jordan Standard Time|Asia/Amman",
        "Arabic Standard Time|Asia/Baghdad",
        "Turkey Standard Time|Europe/Istanbul,Asia/Istanbul",
        "Arab Standard Time|Asia/Riyadh,Asia/Bahrain,Asia/Kuwait,Asia/Qatar,Asia/Aden",
        "Belarus Standard Time|Europe/Minsk",
        "Russian Standard Time|Europe/Moscow,Europe/Kirov,Europe/Simferopol",
        "E. Africa Standard Time|Africa/Nairobi,Africa/Djibouti,Antarctica/Syowa,Africa/Asmara,Africa/Addis_Ababa,Indian/Antananarivo,Indian/Comoro,Africa/Mogadishu,Africa/Dar_es_Salaam,Africa/Kampala,Indian/Mayotte,Etc/GMT-3",
        "Iran Standard Time|Asia/Tehran",
        "Arabian Standard Time|Asia/Dubai,Asia/Muscat,Etc/GMT-4",
        "Astrakhan Standard Time|Europe/Astrakhan,Europe/Ulyanovsk",
        "Azerbaijan Standard Time|Asia/Baku",
        "Russia Time Zone 3|Europe/Samara",
        "Mauritius Standard Time|Indian/Mauritius,Indian/Reunion,Indian/Mahe",
        "Saratov Standard Time|Europe/Saratov",
        "Georgian Standard Time|Asia/Tbilisi",
        "Volgograd Standard Time|Europe/Volgograd",
        "Caucasus Standard Time|Asia/Yerevan",
        "Afghanistan Standard Time|Asia/Kabul",
        "West Asia Standard Time|Asia/Tashkent,Antarctica/Mawson,Asia/Oral,Asia/Aqtau,Asia/Aqtobe,Asia/Atyrau,Indian/Maldives,Indian/Kerguelen,Asia/Dushanbe,Asia/Ashgabat,Asia/Samarkand,Etc/GMT-5",
        "Ekaterinburg Standard Time|Asia/Yekaterinburg",
        "Pakistan Standard Time|Asia/Karachi",
        "Qyzylorda Standard Time|Asia/Qyzylorda",
        "India Standard Time|Asia/Kolkata,Asia/Calcutta",
        "Sri Lanka Standard Time|Asia/Colombo",
        "Nepal Standard Time|Asia/Katmandu,Asia/Kathmandu",
        "Central Asia Standard Time|Asia/Almaty,Antarctica/Vostok,Asia/Urumqi,Asia/Kashgar,Indian/Chagos,Asia/Bishkek,Etc/GMT-6",
        "Bangladesh Standard Time|Asia/Dhaka,Asia/Thimphu",
        "Omsk Standard Time|Asia/Omsk",
        "Myanmar Standard Time|Asia/Rangoon,Asia/Yangon,Indian/Cocos",
        "SE Asia Standard Time|Asia/Bangkok,Antarctica/Davis,Indian/Christmas,Asia/Jakarta,Asia/Pontianak,Asia/Phnom_Penh,Asia/Vientiane,Asia/Saigon,Asia/Ho_Chi_Minh,Etc/GMT-7",
        "Altai Standard Time|Asia/Barnaul",
        "W. Mongolia Standard Time|Asia/Hovd",
        "North Asia Standard Time|Asia/Krasnoyarsk,Asia/Novokuznetsk",
        "N. Central Asia Standard Time|Asia/Novosibirsk",
        "Tomsk Standard Time|Asia/Tomsk",
        "China Standard Time|Asia/Shanghai,Asia/Hong_Kong,Asia/Macau,PRC,Asia/Chongqing,Asia/Harbin",
        "North Asia East Standard Time|Asia/Irkutsk",
        "Singapore Standard Time|Asia/Singapore,Asia/Brunei,Asia/Makassar,Asia/Kuala_Lumpur,Asia/Kuching,Asia/Manila,Etc/GMT-8",
        "W. Australia Standard Time|Australia/Perth",
        "Taipei Standard Time|Asia/Taipei",
        "Ulaanbaatar Standard Time|Asia/Ulaanbaatar,Asia/Choibalsan",
        "Aus Central W. Standard Time|Australia/Eucla",
        "Transbaikal Standard Time|Asia/Chita",
        "Tokyo Standard Time|Asia/Tokyo,Asia/Jayapura,Pacific/Palau,Asia/Dili,Etc/GMT-9",
        "North Korea Standard Time|Asia/Pyongyang",
        "Korea Standard Time|Asia/Seoul",
        "Yakutsk Standard Time|Asia/Yakutsk,Asia/Khandyga",
        "Cen. Australia Standard Time|Australia/Adelaide,Australia/Broken_Hill",
        "AUS Central Standard Time|Australia/Darwin",
        "E. Australia Standard Time|Australia/Brisbane,Australia/Lindeman",
        "AUS Eastern Standard Time|Australia/Sydney,Australia/Melbourne,Australia/Canberra,Australia/ACT,Australia/NSW,Australia/Victoria",
        "West Pacific Standard Time|Pacific/Port_Moresby,Antarctica/DumontDUrville,Pacific/Chuuk,Pacific/Guam,Pacific/Saipan,Etc/GMT-10",
        "Tasmania Standard Time|Australia/Hobart,Australia/Currie,Antarctica/Macquarie",
        "Vladivostok Standard Time|Asia/Vladivostok,Asia/Ust-Nera",
        "Lord Howe Standard Time|Australia/Lord_Howe",
        "Bougainville Standard Time|Pacific/Bougainville",
        "Russia Time Zone 10|Asia/Srednekolymsk",
        "Magadan Standard Time|Asia/Magadan",
        "Norfolk Standard Time|Pacific/Norfolk",
        "Sakhalin Standard Time|Asia/Sakhalin",
        "Central Pacific Standard Time|Pacific/Guadalcanal,Pacific/Noumea,Pacific/Efate,Pacific/Pohnpei,Pacific/Kosrae,Etc/GMT-11",
        "Russia Time Zone 11|Asia/Kamchatka,Asia/Anadyr",
        "New Zealand Standard Time|Pacific/Auckland,Antarctica/McMurdo,NZ",
        "UTC+12|Etc/GMT-12,Pacific/Tarawa,Pacific/Majuro,Pacific/Funafuti,Pacific/Wake,Pacific/Wallis,Pacific/Nauru",
        "Fiji Standard Time|Pacific/Fiji",
        "Chatham Islands Standard Time|Pacific/Chatham",
        "UTC+13|Etc/GMT-13,Pacific/Fakaofo,Pacific/Enderbury,Pacific/Kanton",
        "Tonga Standard Time|Pacific/Tongatapu",
        "Samoa Standard Time|Pacific/Apia",
        "Line Islands Standard Time|Pacific/Kiritimati,Etc/GMT-14",
    };

    private static readonly Lazy<(Dictionary<string, string> WindowsToIana, Dictionary<string, string> IanaToWindows)> Tables = new(Build);

    /// <summary>Primary IANA id for a Windows zone id, or null.</summary>
    public static string? ToIana(string windowsId) =>
        Tables.Value.WindowsToIana.TryGetValue(windowsId, out var v) ? v : null;

    /// <summary>Windows zone id for an IANA id, or null.</summary>
    public static string? ToWindows(string ianaId) =>
        Tables.Value.IanaToWindows.TryGetValue(ianaId, out var v) ? v : null;

    internal static IEnumerable<string> WindowsIds => Tables.Value.WindowsToIana.Keys;

    private static (Dictionary<string, string>, Dictionary<string, string>) Build()
    {
        var w2i = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i2w = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            var bar = row.IndexOf('|');
            var win = row.Substring(0, bar);
            var ianas = row.Substring(bar + 1).Split(',');
            w2i[win] = ianas[0];
            foreach (var iana in ianas)
            {
                i2w.TryAdd(iana, win);
            }
        }
        return (w2i, i2w);
    }
}
