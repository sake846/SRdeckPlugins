namespace SRdeckPlugin.Meshtastic;

public static class MeshtasticMapPresentation
{
    public const string NewNodeColor = "#FFB74D";
    public const string ActiveNodeColor = "#4DD0E1";
    public const string RecentNodeColor = "#26A69A";
    public const string DormantNodeColor = "#B8B8B8";

    public const string LegendHtml =
        "<i style=\"background:__SERIES_3__\"></i>新規 " +
        "<i style=\"background:__SERIES_1__\"></i>活動中 " +
        "<i style=\"background:__SERIES_4__\"></i>最近 " +
        "<i style=\"background:__TEXT_DIM__\"></i>休止";

    public static string GetNodeColor(string activityStatus) => activityStatus switch
    {
        "新規" => NewNodeColor,
        "活動中" => ActiveNodeColor,
        "最近" => RecentNodeColor,
        _ => DormantNodeColor
    };
}
