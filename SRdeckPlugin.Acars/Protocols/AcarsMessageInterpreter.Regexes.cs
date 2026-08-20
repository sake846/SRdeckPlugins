using System.Text.RegularExpressions;

namespace SRdeckPlugin.Acars.Protocols;

public static partial class AcarsMessageInterpreter
{
    [GeneratedRegex(@"(?:[FL](?<rpt>\d{2})(?<airline>[A-Z]{3})\d{4}/)?\s*\.?(?<station>[A-Z]{7,8})\.\s*(?:ADSB|AT1B|AT1|ADSC|POS)-(?<reg>\d{5}|[A-Z0-9]{4,6})(?<date>\d{4})?(?<hex>[A-F0-9]{10,})", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AdsbAcarsRegex();

    [GeneratedRegex(@"^\s*(?:(?<series>[FL])(?<sequence>\d{2})(?<flight>[A-Z0-9]{7})/)?\s*[/\.]?(?<atsu>[A-Z0-9]{7})\.AT1(?<registration>.{7})(?<hex>(?:[A-F0-9]{2}){5,})\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Arinc622CpdlcRegex();

    [GeneratedRegex(@"^\s*(?:(?<series>[FL])(?<sequence>\d{2})(?<flight>[A-Z0-9]{7})/)?\s*[/\.]?(?<atsu>[A-Z0-9]{7})\.CC1\.(?<registration>JA\d{3,4}[A-Z]?|[A-Z]{1,2}-[A-Z0-9]{3,5}|[A-Z0-9.]{7})(?<hex>(?:[A-F0-9]{2}){4,})\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Arinc622ConnectionConfirmRegex();

    [GeneratedRegex(@"\bOUT[:/ ]*(?<time>\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex OooiOutRegex();

    [GeneratedRegex(@"\bOFF[:/ ]*(?<time>\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex OooiOffRegex();

    [GeneratedRegex(@"\bON[:/ ]*(?<time>\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex OooiOnRegex();

    [GeneratedRegex(@"\bIN[:/ ]*(?<time>\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex OooiInRegex();

    [GeneratedRegex(@"(?:\bFLT\b|\bFLIGHT\b|\bF/N\b)\s*[:=]?\s*(?<flt>[A-Z0-9]{3,8})|\b(?<flt>(?!RPT\d|PG\d)[A-Z]{2,3}\d{1,4}[A-Z]?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FlightNumberRegex();

    [GeneratedRegex(@"\b(?<dep>RJ[A-Z]{2}|RO[A-Z]{2}|VK[A-Z]{2}|RK[A-Z]{2}|RC[A-Z]{2}|VH[A-Z]{2}|Z[A-Z]{3}|K[A-Z]{3})\b(?:/(?<arr>[A-Z0-9]{4}))?|\b(?<dep>[A-Z]{4})(?<arr>[A-Z]{4})\d{1,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex AirportPairRegex();

    [GeneratedRegex(@"\b(?<reg>N\d{3,5}[A-Z]{0,2}|JA\d{4}[A-Z]?|B-\d{4,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex RegNumberRegex();

    [GeneratedRegex(@"\b(?<day>\d{1,2})(?<mon>[A-Z]{3})\d{2}\s+(?<time>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateUtcRegex();

    [GeneratedRegex(@"\b(?<eng>GE-\d{3}|CFM\d{2}|PW\d{4}|RR\d{3}|TRENT\d{3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EngineTypeRegex();

    [GeneratedRegex(@"^1\s+L\s+(?<l>[\d.]+)\s+R\s+(?<r>[\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AileronParamRegex();

    [GeneratedRegex(@"^2\s+(?<dir>[LR])\s+(?<val>[\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RudderParamRegex();

    [GeneratedRegex(@"^4\s+(?<val1>[\d.]+)\s+(?<val2>[\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ElevatorParamRegex();

    [GeneratedRegex(@"\b(?<name>L-FLIGHT CONTROL[A-Z0-9 ]*|ENGINE[A-Z0-9 ]*|APU[A-Z0-9 ]*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReportNameRegex();

    [GeneratedRegex(@"(?:\bALT\b|\bFL\b)\s*(?<alt>\d{3,5})", RegexOptions.IgnoreCase)]
    private static partial Regex AltitudeRegex();

    [GeneratedRegex(@"(?:\bSPD\b|\bGS\b|\bIAS\b)\s*(?<spd>\d{2,4})", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedRegex();

    [GeneratedRegex(@"(?:\bMETAR\b|\bTAF\b)?\s*\b(?<station>[A-Z]{4})\s+\d{6}Z", RegexOptions.IgnoreCase)]
    private static partial Regex MetarRegex();

    [GeneratedRegex(@"\b(?<dir>\d{3}|VRB)(?<spd>\d{2,3})(?:G(?<gst>\d{2,3}))?KT\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindRegex();

    [GeneratedRegex(@"\b(?<temp>M?\d{2})/(?<dp>M?\d{2})?\b")]
    private static partial Regex TempDpRegex();

    [GeneratedRegex(@"\bQ(?<qnh>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex QnhRegex();

    // Airline application envelopes often place ETA immediately after the
    // flight identifier (for example, "M91ABC0719ETA0701"), so ETA does not
    // necessarily begin at a word boundary.
    [GeneratedRegex(@"ETA\s*(?<time>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EtaRegex();

    [GeneratedRegex(@"(?:^|\s)[MS]\d{2}(?<flight>[A-Z]{2,3}\d{3,4})\s+\d{2}\s+WXRQ\s+\d{3,4}/(?<day>\d{2})\s+(?<dep>[A-Z]{4})/(?<arr>[A-Z]{4})\s+\.?(?<reg>[A-Z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WeatherRequestHeaderRegex();

    [GeneratedRegex(@"/TYP\s+(?<type>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WeatherRequestTypeRegex();

    [GeneratedRegex(@"/STA\s*(?<station>[A-Z]{4})?", RegexOptions.IgnoreCase)]
    private static partial Regex WeatherRequestStationRegex();

    [GeneratedRegex(@"^\s*(?<series>[MS])(?<sequence>\d{2})(?<flight>[A-Z0-9]{3}\d{4})(?<payload>[\s\S]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MSeriesEnvelopeRegex();

    [GeneratedRegex(@"(?:^|/)TAC/(?<airport>[A-Z]{3,4})/(?<time>\d{4})(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PilotLoginDetailsRegex();

    [GeneratedRegex(@"(?:^|/)\d?EV(?<event>\d{2})(?<value>[A-Z0-9]*)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SSeriesEventRegex();

    [GeneratedRegex(@"(?:^|[\s/#,])(?<imi>WXRQ|AEP|FPR|LIF|PWD|POS)(?=$|[\s/,]|[NS]\d)", RegexOptions.IgnoreCase)]
    private static partial Regex StandardMessageIdentifierRegex();

    [GeneratedRegex(@"/(?<key>STA|DA|DS|AN|CL|AL|CI|CR|RT|RW|TO|TA|WV|CW|ZF|BF|TG|CG|PF)\s*(?<value>[^/\r\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex StandardMessageElementRegex();

    [GeneratedRegex(@"^[MS]\d{2}[A-Z0-9]{3}\d{4}(?<airport>[A-Z]{3})[A-Z]$", RegexOptions.IgnoreCase)]
    private static partial Regex CompactAtisRequestRegex();

    [GeneratedRegex(@"/(?<station>[A-Z]{4})\.TI(?<version>\d)/[A-Z0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex Ti2AtisRequestRegex();

    [GeneratedRegex(@"^\s*(?<key>[A-Z0-9_\-]{2,10})\s*[:=]\s*(?<val>[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"#(?<format>DF[A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex CompactAcmsFormatRegex();

    [GeneratedRegex(@"(?<message>[A-Z]\d{2})(?<part>[A-Z])(?<flight>[A-Z0-9]{6,8})#(?<format>[A-Z0-9]{3})", RegexOptions.IgnoreCase)]
    private static partial Regex AirlineSegmentHeaderRegex();

    [GeneratedRegex(@"#CFBFLR/FR(?<date>\d{6})(?<time>\d{6})", RegexOptions.IgnoreCase)]
    private static partial Regex CfbFaultDateTimeRegex();

    [GeneratedRegex(@"(?<system>ATC\d|TCAS)(?:\((?<status>[^)]+)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex CfbFaultSystemRegex();

    [GeneratedRegex(@"/ID(?<id>[A-Z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CfbFaultIdentifierRegex();

    [GeneratedRegex(@"RPT(?<report>\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedReportRegex();

    [GeneratedRegex(@"PG(?<page>\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedPageRegex();

    [GeneratedRegex(@";(?<day>\d{2});(?<mon>[A-Z]{3});(?<year>\d{2});(?<time>\d{2}:\d{2}:\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedTimestampRegex();

    [GeneratedRegex(@"(?:^|/)REP(?:ORT)?(?<report>\d{1,4})(?:[,/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CompactAcmsReportRegex();

    [GeneratedRegex(@"(?:^|[/\r\n])C[A-Z0-9]*?(?<reg>B-?\d{4,5}),(?<mon>[A-Z]{3})(?<day>\d{2}),(?<time>\d{6}),(?<dep>[A-Z]{4}),(?<arr>[A-Z]{4}),(?<flight>\d{1,6})(?:/|,)", RegexOptions.IgnoreCase)]
    private static partial Regex CompactAcmsFlightRegex();

    [GeneratedRegex(@"^[A-F0-9]{12,}$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneHexRegex();

    [GeneratedRegex(@"/TB\s+(?<val>[A-Z0-9\- ]+?)(?=/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TurbRegex();

    [GeneratedRegex(@"/IC\s+(?<val>[A-Z0-9\- ]+?)(?=/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex IceRegex();

    [GeneratedRegex(@"\b(?<lat>\d{5})\s+(?<lon>\d{6})(?<time>\d{4})\s+(?<alt>\d{5})(?<temp>[-+]\d{2})(?<winddir>\d{3})(?<windspd>\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex TranspacificWeatherRegex();

    [GeneratedRegex(@"(?:[FL](?<rpt>\d{2})?(?<flt>[A-Z0-9]{3,8})?/)?\s*(?<station>[A-Z]{7,8})[.\s]+(?:ADSB|AT1B|AT1|ADSC|POS|ADS)[-.\s]+[A-Z0-9]{0,4}(?<hex>[A-F0-9]{10,})", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AdscHeaderRegex();

    [GeneratedRegex(@"(?:M\d{2}(?<airline>[A-Z]{3})\d{4}[A-Z0-9]*,?\s*)?(?:V\d+,?\s*)?(?:(?<flt>[A-Z0-9 ]+),?\s*)?(?:\d{8}\s*\d*,?\s*)?(?<dep>[A-Z]{4}),?\s*(?<arr>[A-Z]{4}),?\s*(?<latHem>[NS])(?<latDeg>\d{3})[.,\s]+(?<latMin>\d{3,4})[.,\s]+(?<lonHem>[EW])(?<lonDeg>\d{3})[.,\s]+(?<lonMin>\d{3,4})[.,\s]+(?<alt>\d{4,5})[.,\s]+(?<spd>\d{3,4})", RegexOptions.IgnoreCase)]
    private static partial Regex Arinc620OceanicRegex();

    [GeneratedRegex(@"(?:[FL](?<rpt>\d{2})?(?<flt>[A-Z0-9]{3,8})?/)?\s*(?:(?<station>[A-Z]{4,8})\.)?\s*(?:AT1B|AT1|ADSB|ADSC|POS|ADS)?[\.\s\-]*(?:(?<reg>B-[A-Z0-9]{3,5}|N\d{3,5}[A-Z]{0,2}|JA\d{4}[A-Z]?))?[\.\s\-]*\s*(?<hex>[A-F0-9]{10,})", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UniversalHeaderPayloadRegex();

    /// <summary>D-ATIS header: optional ICAO + info letter. e.g. "ATIS A", "D-ATIS RJTT INFO B", "ATIS RJGG C".</summary>
    [GeneratedRegex(@"(?:^|\b)(?:D-)?ATIS\s*(?:(?<icao>[A-Z]{4})\s+)?(?:INFO\s+)?(?<info>[A-Z])\b", RegexOptions.IgnoreCase)]
    private static partial Regex AtisHeaderRegex();

    /// <summary>Active runway(s): "RWY 34L", "RWY16/34".</summary>
    [GeneratedRegex(@"\bRWY\s*(?<rwy>\d{1,2}[LCR]?(?:/\d{1,2}[LCR]?)*)", RegexOptions.IgnoreCase)]
    private static partial Regex RunwayRegex();

    /// <summary>Visibility in km or statute miles: "VIS 10", "VISIBILITY 5".</summary>
    [GeneratedRegex(@"\bVIS(?:IBILITY)?\s+(?<vis>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex VisibilityRegex();

    /// <summary>Gate assignment: "GATE B22", "GTR A5".</summary>
    [GeneratedRegex(@"\b(?:GATE|GTR)\s+(?<gate>[A-Z]?\d{1,3}[A-Z]?)", RegexOptions.IgnoreCase)]
    private static partial Regex GateNumberRegex();

    /// <summary>Parking stand / bay: "STAND 42", "PARK C3", "BAY 7".</summary>
    [GeneratedRegex(@"\b(?:STAND|PARK|BAY)\s+(?<stand>[A-Z]?\d{1,3}[A-Z]?)", RegexOptions.IgnoreCase)]
    private static partial Regex ParkingStandRegex();

    /// <summary>Trigger for gate/ground-ops messages without a specific ARINC label.</summary>
    [GeneratedRegex(@"\b(?:GATE|STAND|PARK|BAY|RAMP|PUSHBACK|BOARDING|CATERING|DEICE|DE-ICE|CLEARANCE)", RegexOptions.IgnoreCase)]
    private static partial Regex GateKeywordRegex();

    /// <summary>Fuel quantity with unit: "FOB 14500 KG", "FUEL 8.5T", "UPLIFT 3200LB".</summary>
    [GeneratedRegex(@"\b(?:FOB|FUEL|UPLIFT)\s*[:=]?\s*(?<val>\d+(?:[.,]\d+)?)\s*(?<unit>KGS?|LBS?|T)?", RegexOptions.IgnoreCase)]
    private static partial Regex FuelValueRegex();

    /// <summary>Zero Fuel Weight: "ZFW 62.5" (tonnes).</summary>
    [GeneratedRegex(@"\bZFW\s*[:=]?\s*(?<val>\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ZfwRegex();

    /// <summary>Takeoff Weight: "TOW 75.2" (tonnes).</summary>
    [GeneratedRegex(@"\bTOW\s*[:=]?\s*(?<val>\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex TowRegex();

    /// <summary>Landing Weight: "LDW 68.0" (tonnes).</summary>
    [GeneratedRegex(@"\bLDW\s*[:=]?\s*(?<val>\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex LdwRegex();

    /// <summary>Trigger for fuel/weight messages without a specific ARINC label.</summary>
    [GeneratedRegex(@"\b(?:FOB|ZFW|TOW|LDW|UPLIFT|ENDURANCE|ALTN\s+FUEL)", RegexOptions.IgnoreCase)]
    private static partial Regex FuelKeywordRegex();

    /// <summary>VHF frequency mentioned in CPDLC: e.g. "119.100", "132.600".</summary>
    [GeneratedRegex(@"\b(?<freq>1[12]\d\.\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex VhfFrequencyRegex();
}
