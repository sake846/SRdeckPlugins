using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Hfdl.ViewModels;

public sealed partial class HfdlViewModel
{
    private ReceptionOverview? overview;
    public ReceptionOverview Overview => overview ??= BuildOverview();

    private void RefreshOverview()
    {
        overview = null;
        OnPropertyChanged(nameof(Overview));
    }

    private ReceptionOverview BuildOverview()
    {
        var aircraftMessages = Messages.Where(item => !item.IsGroundStationMessage).ToArray();
        int aircraft = aircraftMessages.Select(item => item.AircraftDisplay)
            .Where(id => id != "機体不明").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int flights = aircraftMessages.Select(item => item.FlightId.Trim())
            .Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int groundMessages = Messages.Count(item => item.IsGroundStationMessage);
        return new(
            Messages.Count == 0 ? "機体と地上局の通信状況を把握するためのHFDLデータを待っています。"
                : $"機体側 {aircraftMessages.Length:N0}件、地上局側 {groundMessages:N0}件の通信を取得。機体識別 {aircraft:N0}件、Flight ID {flights:N0}件を把握しています。",
            ReceptionOverview.DescribeScope("保持中のメッセージ", Messages.Select(item => item.ReceivedAt)),
            [new("機体識別", aircraft), new("Flight ID", flights),
             new("機体側の通信", aircraftMessages.Length), new("地上局側の通信", groundMessages)],
            ReceptionOverview.CountKinds(Messages.Select(item => item.Kind)),
            "機体識別には登録記号・ICAO・地上局内の機体IDも含みます。同じ機体の別識別は統合しません。検索条件は集計に反映しません。",
            "機体識別ごとの通信は一覧、受信種別と順序は時系列、選択中の地上局の所在地は地図で確認できます。");
    }
}
