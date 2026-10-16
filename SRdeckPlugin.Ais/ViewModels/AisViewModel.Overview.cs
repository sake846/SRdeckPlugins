using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ais.ViewModels;

public sealed partial class AisViewModel
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
        int positioned = Targets.Count(item => item.HasPosition);
        int named = Targets.Count(item => !string.IsNullOrWhiteSpace(item.DisplayName) && item.DisplayName != item.MmsiText);
        int destination = Targets.Count(item => !string.IsNullOrWhiteSpace(item.Destination));
        int stations = Targets.Count(item => item.IsBaseStation);
        int aids = Targets.Count(item => item.IsAidToNavigation && !item.IsBaseStation);
        int navigation = Targets.Count(item => item.NavigationStatus is >= 0 and <= 8 or 14);
        return new(
            Targets.Count == 0 ? "船舶・基地局・航路標識を把握するためのAISデータを待っています。"
                : $"{Targets.Count:N0}局を把握。位置は {positioned:N0}局、名称または呼出符号は {named:N0}局で取得しています。",
            ReceptionOverview.DescribeScope("保持中の船舶・局（最終受信）", Targets.Select(item => item.LastSeen)),
            [new("把握した船舶・局", Targets.Count), new("位置取得局", positioned),
             new("名称・呼出符号取得局", named), new("目的地取得局", destination)],
            [new("基地局", $"{stations:N0}局"), new("航路標識", $"{aids:N0}局"),
             new("船舶等（上記以外）", $"{Targets.Count - stations - aids:N0}局"),
             new("航行状態を取得", $"{navigation:N0}局"),
             new("位置を未取得", $"{Targets.Count - positioned:N0}局")],
            "名称・呼出符号にMMSIだけの表示は含みません。航行状態は定義済みの値だけを集計します。地図は設定した保持期間内の位置を表示します。検索条件は集計に反映しません。",
            "船舶・局の情報を比較するなら一覧、通信内容を追うなら時系列、位置と航跡を見るなら地図へ進めます。");
    }
}
