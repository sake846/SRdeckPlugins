using System.Globalization;
using System.Text;

namespace SRdeckPlugin.Acars.Protocols;

public static partial class AcarsMessageInterpreter
{
    private static readonly Dictionary<string, string> AirportNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Japan Main Airports
        ["RJFF"] = "福岡", ["FUK"] = "福岡", ["FUKJ"] = "福岡", ["FUKJJYA"] = "福岡", ["FUKJ1YA"] = "福岡",
        ["RJAA"] = "成田", ["NRT"] = "成田", ["NRTJ"] = "成田", ["NRTJJYA"] = "成田",
        ["RJTT"] = "羽田", ["HND"] = "羽田", ["HNDJ"] = "羽田", ["HNDJJYA"] = "羽田",
        ["RJBB"] = "関空", ["KIX"] = "関空", ["KIXJ"] = "関空", ["KIXJJYA"] = "関空",
        ["RJGG"] = "中部", ["NGO"] = "中部", ["NGOJ"] = "中部", ["NGOJJYA"] = "中部",
        ["RJCC"] = "新千歳", ["CTS"] = "新千歳", ["CTSJ"] = "新千歳", ["CTSJJYA"] = "新千歳",
        ["ROAH"] = "那覇", ["OKA"] = "那覇", ["OKAJ"] = "那覇", ["OKAJJYA"] = "那覇",
        ["RJTY"] = "横田", ["RJTA"] = "厚木", ["RJSF"] = "仙台", ["SDJ"] = "仙台",
        ["RJFU"] = "長崎", ["NGS"] = "長崎",
        ["RJNK"] = "小松", ["KMQ"] = "小松", ["RJFO"] = "大分", ["OIT"] = "大分",
        ["RJFT"] = "熊本", ["KMJ"] = "熊本", ["RJFK"] = "鹿児島", ["KOJ"] = "鹿児島",
        ["RJSN"] = "新潟", ["KIJ"] = "新潟", ["RJOW"] = "岩国", ["RJOA"] = "広島",
        ["HIJ"] = "広島", ["RJFM"] = "宮崎", ["KMI"] = "宮崎", ["RODN"] = "嘉手納",
        ["RJOY"] = "八尾", ["RJOE"] = "明野", ["RJCH"] = "函館", ["HKD"] = "函館",
        ["RJEC"] = "旭川", ["AKJ"] = "旭川", ["RJCB"] = "帯広", ["OBO"] = "帯広",
        ["RJFR"] = "北九州", ["KKJ"] = "北九州", ["RJFM"] = "宮崎", ["ROIG"] = "石垣",
        ["ISG"] = "石垣", ["ROMY"] = "宮古", ["MMY"] = "宮古",

        // Major International Airports - Asia / Pacific
        ["VHHH"] = "香港", ["HKG"] = "香港",
        ["RCTP"] = "桃園/台北", ["TPE"] = "桃園/台北",
        ["RCSS"] = "松山/台北", ["TSA"] = "松山/台北",
        ["RCKH"] = "高雄", ["KHH"] = "高雄",
        ["RKSI"] = "仁川/ソウル", ["ICN"] = "仁川/ソウル",
        ["RKSS"] = "金浦/ソウル", ["GMP"] = "金浦/ソウル",
        ["RKTU"] = "清州", ["CJJ"] = "清州",
        ["RKPK"] = "金海/釜山", ["PUS"] = "金海/釜山",
        ["ZSPD"] = "浦東/上海", ["PVG"] = "浦東/上海",
        ["ZSSS"] = "虹橋/上海", ["SHA"] = "虹橋/上海",
        ["ZBAA"] = "北京首都", ["PEK"] = "北京首都",
        ["ZBAD"] = "北京大興", ["PKX"] = "北京大興",
        ["ZGGG"] = "広州", ["CAN"] = "広州",
        ["ZGSZ"] = "深圳", ["SZX"] = "深圳",
        ["ZSNT"] = "南通興東", ["NTG"] = "南通興東",
        ["VMMC"] = "マカオ", ["MFM"] = "マカオ",
        ["VTBS"] = "バンコク", ["BKK"] = "バンコク",
        ["VTBD"] = "ドンムアン/バンコク", ["DMK"] = "ドンムアン/バンコク",
        ["VTSP"] = "プーケット", ["HKT"] = "プーケット",
        ["VTCC"] = "チェンマイ", ["CNX"] = "チェンマイ",
        ["WSSS"] = "シンガポール", ["SIN"] = "シンガポール",
        ["WMKK"] = "クアラルンプール", ["KUL"] = "クアラルンプール",
        ["VVTS"] = "ホーチミン", ["SGN"] = "ホーチミン",
        ["VVNB"] = "ハノイ", ["HAN"] = "ハノイ",
        ["VVDN"] = "ダナン", ["DAD"] = "ダナン",
        ["RPLL"] = "マニラ", ["MNL"] = "マニラ",
        ["RPVM"] = "セブ", ["CEB"] = "セブ",
        ["WIII"] = "ジャカルタ", ["CGK"] = "ジャカルタ",
        ["WADD"] = "バリ/デンパサール", ["DPS"] = "バリ/デンパサール",
        ["VIDP"] = "デリー", ["DEL"] = "デリー",
        ["VABB"] = "ムンバイ", ["BOM"] = "ムンバイ",
        ["YSSY"] = "シドニー", ["SYD"] = "シドニー",
        ["YMML"] = "メルボルン", ["MEL"] = "メルボルン",
        ["YBBN"] = "ブリスベン", ["BNE"] = "ブリスベン",
        ["YPPH"] = "パース", ["PER"] = "パース",
        ["NZAK"] = "オークランド", ["AKL"] = "オークランド",

        // North America
        ["KLAX"] = "ロサンゼルス", ["LAX"] = "ロサンゼルス",
        ["KJFK"] = "ニューヨークJFK", ["JFK"] = "ニューヨークJFK",
        ["KEWR"] = "ニューアーク", ["EWR"] = "ニューアーク",
        ["KLGA"] = "ラガーディア", ["LGA"] = "ラガーディア",
        ["KSFO"] = "サンフランシスコ", ["SFO"] = "サンフランシスコ",
        ["KSEA"] = "シアトル", ["SEA"] = "シアトル",
        ["KORD"] = "シカゴ", ["ORD"] = "シカゴ",
        ["KDFW"] = "ダラス", ["DFW"] = "ダラス",
        ["KATL"] = "アトランタ", ["ATL"] = "アトランタ",
        ["KDEN"] = "デンバー", ["DEN"] = "デンバー",
        ["KMIA"] = "マイアミ", ["MIA"] = "マイアミ",
        ["KLAS"] = "ラスベガス", ["LAS"] = "ラスベガス",
        ["KPHX"] = "フェニックス", ["PHX"] = "フェニックス",
        ["KBOS"] = "ボストン", ["BOS"] = "ボストン",
        ["KMSP"] = "ミネアポリス", ["MSP"] = "ミネアポリス",
        ["KDTW"] = "デトロイト", ["DTW"] = "デトロイト",
        ["PANC"] = "アンカレジ", ["ANC"] = "アンカレジ", ["PAMC"] = "アンカレジ",
        ["PHNL"] = "ホノルル", ["HNL"] = "ホノルル",
        ["PHOG"] = "マウイ", ["OGG"] = "マウイ",
        ["CYVR"] = "バンクーバー", ["YVR"] = "バンクーバー",
        ["CYYZ"] = "トロント", ["YYZ"] = "トロント",
        ["CYUL"] = "モントリオール", ["YUL"] = "モントリオール",
        ["CYYC"] = "カルガリー", ["YYC"] = "カルガリー",
        ["MMMX"] = "メキシコシティ", ["MEX"] = "メキシコシティ",
        ["MMUN"] = "カンクン", ["CUN"] = "カンクン",

        // Europe
        ["EGLL"] = "ロンドン", ["LHR"] = "ロンドン",
        ["EGKK"] = "ガトウィック", ["LGW"] = "ガトウィック",
        ["LFPG"] = "パリCDG", ["CDG"] = "パリCDG",
        ["LFPO"] = "オルリー", ["ORY"] = "オルリー",
        ["EDDF"] = "フランクフルト", ["FRA"] = "フランクフルト",
        ["EDDM"] = "ミュンヘン", ["MUC"] = "ミュンヘン",
        ["EHAM"] = "アムステルダム", ["AMS"] = "アムステルダム",
        ["EBBR"] = "ブリュッセル", ["BRU"] = "ブリュッセル",
        ["LSZH"] = "チューリッヒ", ["ZRH"] = "チューリッヒ",
        ["LSGG"] = "ジュネーヴ", ["GVA"] = "ジュネーヴ",
        ["LEMD"] = "マドリード", ["MAD"] = "マドリード",
        ["LEBL"] = "バルセロナ", ["BCN"] = "バルセロナ",
        ["LIRF"] = "ローマ", ["FCO"] = "ローマ",
        ["LIMC"] = "ミラノ", ["MXP"] = "ミラノ",
        ["LOWW"] = "ウィーン", ["VIE"] = "ウィーン",
        ["EFHK"] = "ヘルシンキ", ["HEL"] = "ヘルシンキ",
        ["EKCH"] = "コペンハーゲン", ["CPH"] = "コペンハーゲン",
        ["ENGM"] = "オスロ", ["OSL"] = "オスロ",
        ["ESSA"] = "ストックホルム", ["ARN"] = "ストックホルム",
        ["EPWA"] = "ワルシャワ", ["WAW"] = "ワルシャワ",
        ["UUEE"] = "モスクワ", ["SVO"] = "モスクワ",

        // Middle East / Africa / Latin America
        ["OMDB"] = "ドバイ", ["DXB"] = "ドバイ",
        ["OMAA"] = "アブダビ", ["AUH"] = "アブダビ",
        ["OTHH"] = "ドーハ", ["DOH"] = "ドーハ",
        ["LTFM"] = "イスタンブール", ["IST"] = "イスタンブール",
        ["OERK"] = "リヤド", ["RUH"] = "リヤド",
        ["LLBG"] = "テルアビブ", ["TLV"] = "テルアビブ",
        ["FAOR"] = "ヨハネスブルグ", ["JNB"] = "ヨハネスブルグ",
        ["HECA"] = "カイロ", ["CAI"] = "カイロ",
        ["SBGR"] = "サンパウロ", ["GRU"] = "サンパウロ",
        ["SAEZ"] = "ブエノスアイレス", ["EZE"] = "ブエノスアイレス",
        ["SKBO"] = "ボゴタ", ["BOG"] = "ボゴタ"
    };

    private static readonly Dictionary<string, string> AirlineNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Japan Domestic & Regional
        ["JAL"] = "日本航空 (Japan Airlines)", ["JL"] = "日本航空 (Japan Airlines)",
        ["ANA"] = "全日本空輸 (All Nippon Airways)", ["NH"] = "全日本空輸 (All Nippon Airways)",
        ["JTA"] = "日本トランスオーシャン航空", ["RAC"] = "琉球エアーコミューター",
        ["JAIR"] = "ジェイエア (J-AIR)", ["XM"] = "ジェイエア (J-AIR)",
        ["HAC"] = "北海道エアシステム", ["FDA"] = "フジドリームエアラインズ",
        ["NKM"] = "スカイマーク (Skymark)", ["SKY"] = "スカイマーク (Skymark)",
        ["SFJ"] = "スターフライヤー (StarFlyer)", ["ADO"] = "AIRDO",
        ["SNJ"] = "ソラシドエア (Solaseed Air)", ["APJ"] = "ピーチ・アビエーション",
        ["JJP"] = "ジェットスター・ジャパン", ["GK"] = "ジェットスター・ジャパン",
        ["TZP"] = "ZIPAIR Tokyo", ["ZG"] = "ZIPAIR Tokyo",
        ["SJO"] = "スプリング・ジャパン", ["IJ"] = "スプリング・ジャパン",
        ["ANAW"] = "ANAウイングス", ["AKX"] = "ANAウイングス",
        ["AJX"] = "エアジャパン (Air Japan)",

        // Korea, Taiwan, China, Hong Kong
        ["KAL"] = "大韓航空 (Korean Air)", ["KE"] = "大韓航空 (Korean Air)",
        ["AAR"] = "アシアナ航空 (Asiana Airlines)", ["OZ"] = "アシアナ航空 (Asiana Airlines)",
        ["JJA"] = "チェジュ航空 (Jeju Air)", ["7C"] = "チェジュ航空 (Jeju Air)",
        ["ABL"] = "エアプサン (Air Busan)", ["BX"] = "エアプサン (Air Busan)",
        ["JNA"] = "ジンエアー (Jin Air)", ["LJ"] = "ジンエアー (Jin Air)",
        ["TWB"] = "ティーウェイ航空 (T-Way Air)", ["TW"] = "ティーウェイ航空 (T-Way Air)",
        ["ESR"] = "イースター航空 (Eastar Jet)", ["ZE"] = "イースター航空 (Eastar Jet)",
        ["EVA"] = "エバー航空 (EVA Air)", ["ABR"] = "エバー航空 (EVA Air)", ["BR"] = "エバー航空 (EVA Air)",
        ["CAL"] = "中華航空 (China Airlines)", ["CI"] = "中華航空 (China Airlines)",
        ["MDA"] = "華信航空 (Mandarin Airlines)", ["AE"] = "華信航空 (Mandarin Airlines)",
        ["UIA"] = "立栄航空 (UNI Air)", ["B7"] = "立栄航空 (UNI Air)",
        ["SJX"] = "スターラックス航空 (STARLUX)", ["JX"] = "スターラックス航空 (STARLUX)",
        ["CPA"] = "キャセイパシフィック航空 (Cathay Pacific)", ["CX"] = "キャセイパシフィック航空 (Cathay Pacific)",
        ["HDA"] = "キャセイドラゴン", ["KA"] = "キャセイドラゴン",
        ["HKE"] = "香港エクスプレス (HK Express)", ["UO"] = "香港エクスプレス (HK Express)",
        ["CRK"] = "香港航空 (Hong Kong Airlines)", ["HX"] = "香港航空 (Hong Kong Airlines)",
        ["CCA"] = "中国国際航空 (Air China)", ["CA"] = "中国国際航空 (Air China)",
        ["CES"] = "中国東方航空 (China Eastern)", ["MU"] = "中国東方航空 (China Eastern)",
        ["CSN"] = "中国南方航空 (China Southern)", ["CZ"] = "中国南方航空 (China Southern)",
        ["CHH"] = "海南航空 (Hainan Airlines)", ["HU"] = "海南航空 (Hainan Airlines)",
        ["CSZ"] = "深圳航空 (Shenzhen Airlines)", ["ZH"] = "深圳航空 (Shenzhen Airlines)",
        ["CXA"] = "厦門航空 (XiamenAir)", ["MF"] = "厦門航空 (XiamenAir)",
        ["CQH"] = "春秋航空 (Spring Airlines)", ["9C"] = "春秋航空 (Spring Airlines)",
        ["CSC"] = "四川航空 (Sichuan Airlines)", ["3U"] = "四川航空 (Sichuan Airlines)",
        ["DKH"] = "吉祥航空 (Juneyao Air)", ["HO"] = "吉祥航空 (Juneyao Air)",

        // SE Asia, South Asia, Oceania
        ["SIA"] = "シンガポール航空 (Singapore Airlines)", ["SQ"] = "シンガポール航空 (Singapore Airlines)",
        ["SCO"] = "スクート (Scoot)", ["TR"] = "スクート (Scoot)",
        ["MAS"] = "マレーシア航空 (Malaysia Airlines)", ["MH"] = "マレーシア航空 (Malaysia Airlines)",
        ["AXM"] = "エアアジア (AirAsia)", ["AK"] = "エアアジア (AirAsia)",
        ["XAX"] = "エアアジア X", ["D7"] = "エアアジア X",
        ["THA"] = "タイ国際航空 (Thai Airways)", ["TG"] = "タイ国際航空 (Thai Airways)",
        ["AIQ"] = "タイ・エアアジア", ["FD"] = "タイ・エアアジア",
        ["TLM"] = "タイ・ライオン・エア", ["SL"] = "タイ・ライオン・エア",
        ["HVN"] = "ベトナム航空 (Vietnam Airlines)", ["VN"] = "ベトナム航空 (Vietnam Airlines)",
        ["VJC"] = "ベトジェットエア (VietJet Air)", ["VJ"] = "ベトジェットエア (VietJet Air)",
        ["BAV"] = "バンブー・エアウェイズ", ["QH"] = "バンブー・エアウェイズ",
        ["PAL"] = "フィリピン航空 (Philippine Airlines)", ["PR"] = "フィリピン航空 (Philippine Airlines)",
        ["CEB"] = "セブパシフィック航空", ["5J"] = "セブパシフィック航空",
        ["GIA"] = "ガルーダ・インドネシア航空", ["GA"] = "ガルーダ・インドネシア航空",
        ["LION"] = "ライオン・エア (Lion Air)", ["JT"] = "ライオン・エア (Lion Air)",
        ["AIC"] = "インド航空 (Air India)", ["AI"] = "インド航空 (Air India)",
        ["IGO"] = "インディゴ (IndiGo)", ["6E"] = "インディゴ (IndiGo)",
        ["QFA"] = "カンタス航空 (Qantas)", ["QF"] = "カンタス航空 (Qantas)",
        ["VOZ"] = "ヴァージン・オーストラリア", ["VA"] = "ヴァージン・オーストラリア",
        ["JST"] = "ジェットスター航空", ["JQ"] = "ジェットスター航空",
        ["ANZ"] = "ニュージーランド航空 (Air New Zealand)", ["NZ"] = "ニュージーランド航空 (Air New Zealand)",
        ["FJI"] = "フィジー・エアウェイズ", ["FJ"] = "フィジー・エアウェイズ",

        // North America (Passenger & Cargo)
        ["DAL"] = "デルタ航空 (Delta Air Lines)", ["DL"] = "デルタ航空 (Delta Air Lines)",
        ["UAL"] = "ユナイテッド航空 (United Airlines)", ["UA"] = "ユナイテッド航空 (United Airlines)",
        ["AAL"] = "アメリカン航空 (American Airlines)", ["AA"] = "アメリカン航空 (American Airlines)",
        ["SWA"] = "サウスウエスト航空 (Southwest)", ["WN"] = "サウスウエスト航空 (Southwest)",
        ["ASA"] = "アラスカ航空 (Alaska Airlines)", ["AS"] = "アラスカ航空 (Alaska Airlines)",
        ["JBU"] = "ジェットブルー航空", ["B6"] = "ジェットブルー航空",
        ["HAL"] = "ハワイアン航空 (Hawaiian)", ["HA"] = "ハワイアン航空 (Hawaiian)",
        ["FFT"] = "フロンティア航空", ["F9"] = "フロンティア航空",
        ["NKS"] = "スピリット航空", ["NK"] = "スピリット航空",
        ["ACA"] = "エア・カナダ (Air Canada)", ["AC"] = "エア・カナダ (Air Canada)",
        ["WJA"] = "ウェストジェット (WestJet)", ["WS"] = "ウェストジェット (WestJet)",
        ["AMX"] = "アエロメヒコ航空", ["AM"] = "アエロメヒコ航空",
        ["FDX"] = "フェデックス・エクスプレス (FedEx)", ["FX"] = "フェデックス・エクスプレス (FedEx)",
        ["UPS"] = "UPS航空 (UPS Airlines)",
        ["CKS"] = "カリッタエア (Kalitta Air)", ["K4"] = "カリッタエア (Kalitta Air)",
        ["GTI"] = "アトラス航空 (Atlas Air)", ["5Y"] = "アトラス航空 (Atlas Air)",
        ["PAC"] = "ポーラ・エア・カーゴ (Polar)", ["PO"] = "ポーラ・エア・カーゴ (Polar)",
        ["ABX"] = "ABXエア (ABX Air)", ["GB"] = "ABXエア (ABX Air)",
        ["ATI"] = "エア・トランスポート・インターナショナル", ["8C"] = "エア・トランスポート・インターナショナル",

        // Europe
        ["DLH"] = "ルフトハンザドイツ航空 (Lufthansa)", ["LH"] = "ルフトハンザドイツ航空 (Lufthansa)",
        ["BAW"] = "ブリティッシュ・エアウェイズ", ["BA"] = "ブリティッシュ・エアウェイズ",
        ["AFR"] = "エールフランス (Air France)", ["AF"] = "エールフランス (Air France)",
        ["KLM"] = "KLMオランダ航空", ["KL"] = "KLMオランダ航空",
        ["SWR"] = "スイスインターナショナルエアラインズ", ["LX"] = "スイスインターナショナルエアラインズ",
        ["AUA"] = "オーストリア航空", ["OS"] = "オーストリア航空",
        ["FIN"] = "フィンエアー (Finnair)", ["AY"] = "フィンエアー (Finnair)",
        ["SAS"] = "スカンジナビア航空 (SAS)", ["SK"] = "スカンジナビア航空 (SAS)",
        ["IBE"] = "イベリア航空 (Iberia)", ["IB"] = "イベリア航空 (Iberia)",
        ["AZA"] = "ITAエアウェイズ", ["AZ"] = "ITAエアウェイズ", ["ITY"] = "ITAエアウェイズ",
        ["TAP"] = "TAPポルトガル航空", ["TP"] = "TAPポルトガル航空",
        ["VIR"] = "ヴァージン・アトランティック航空", ["VS"] = "ヴァージン・アトランティック航空",
        ["EZY"] = "イージージェット (easyJet)", ["U2"] = "イージージェット (easyJet)",
        ["RYR"] = "ライアンエアー (Ryanair)", ["FR"] = "ライアンエアー (Ryanair)",
        ["WZZ"] = "ウィズエアー (Wizz Air)", ["W6"] = "ウィズエアー (Wizz Air)",
        ["LOT"] = "LOTポーランド航空", ["LO"] = "LOTポーランド航空",
        ["THY"] = "ターキッシュ エアラインズ (Turkish)", ["TK"] = "ターキッシュ エアラインズ (Turkish)",
        ["AFL"] = "アエロフロート・ロシア航空", ["SU"] = "アエロフロート・ロシア航空",
        ["CLX"] = "カーゴルックス航空 (Cargolux)", ["CV"] = "カーゴルックス航空 (Cargolux)",
        ["DHL"] = "DHL航空 (DHL Aviation)", ["BCS"] = "DHL航空 (DHL Aviation)",

        // Middle East, Africa, Latin America
        ["UAE"] = "エミレーツ航空 (Emirates)", ["EK"] = "エミレーツ航空 (Emirates)",
        ["ETD"] = "エティハド航空 (Etihad)", ["EY"] = "エティハド航空 (Etihad)",
        ["QTR"] = "カタール航空 (Qatar Airways)", ["QR"] = "カタール航空 (Qatar Airways)",
        ["SUD"] = "サウディア (Saudia)", ["SV"] = "サウディア (Saudia)",
        ["ELY"] = "エル・アル航空 (El Al)", ["LY"] = "エル・アル航空 (El Al)",
        ["OAL"] = "オマーン・エア (Oman Air)", ["WY"] = "オマーン・エア (Oman Air)",
        ["ETH"] = "エチオピア航空 (Ethiopian)", ["ET"] = "エチオピア航空 (Ethiopian)",
        ["RAM"] = "ロイヤル・エア・モロッコ", ["AT"] = "ロイヤル・エア・モロッコ",
        ["KQA"] = "ケニア航空 (Kenya Airways)", ["KQ"] = "ケニア航空 (Kenya Airways)",
        ["TAM"] = "LATAM航空 (LATAM)", ["LAT"] = "LATAM航空 (LATAM)", ["LA"] = "LATAM航空 (LATAM)",
        ["GLO"] = "ゴル航空 (GOL)", ["G3"] = "ゴル航空 (GOL)",
        ["AZU"] = "アズールブラジル航空", ["AD"] = "アズールブラジル航空",
        ["AVA"] = "アビアンカ航空 (Avianca)", ["AV"] = "アビアンカ航空 (Avianca)"
    };

    private static string FormatAirport(string code)
    {
        code = code.ToUpperInvariant();
        if (code.Length == 4 && code.EndsWith('J') && AirportNames.TryGetValue(code[..3], out string? iataName))
        {
            return $"{iataName} ({code[..3]})";
        }
        if (AirportNames.TryGetValue(code, out string? name))
        {
            return $"{name} ({code})";
        }

        // Dynamic ICAO 4-letter prefix fallback for any airport worldwide
        if (code.Length == 4)
        {
            string countryOrRegion = code[0] switch
            {
                'K' => "米国",
                'C' => "カナダ",
                'P' => "米国/太平洋",
                'E' => "欧州(北・西)",
                'L' => "欧州(南・地中海)",
                'Z' => "中国",
                'V' => "アジア(南・東南)",
                'W' => "インドネシア・東南アジア",
                'Y' => "オーストラリア",
                'M' => "中米・メキシコ",
                'S' => "南米",
                'F' or 'H' or 'D' => "アフリカ",
                'O' => "中東",
                'B' => "アイスランド・グリーンランド",
                _ => string.Empty
            };

            if (code.StartsWith("NZ", StringComparison.OrdinalIgnoreCase)) countryOrRegion = "ニュージーランド";
            if (code.StartsWith("RK", StringComparison.OrdinalIgnoreCase)) countryOrRegion = "韓国";
            if (code.StartsWith("RC", StringComparison.OrdinalIgnoreCase)) countryOrRegion = "台湾";
            if (code.StartsWith("VH", StringComparison.OrdinalIgnoreCase)) countryOrRegion = "香港";

            if (!string.IsNullOrEmpty(countryOrRegion))
            {
                return $"{countryOrRegion} ({code})";
            }
        }

        return code;
    }

    private static string FormatAirline(string code)
    {
        code = code.ToUpperInvariant();
        return AirlineNames.TryGetValue(code, out string? name) ? name : code;
    }
}
