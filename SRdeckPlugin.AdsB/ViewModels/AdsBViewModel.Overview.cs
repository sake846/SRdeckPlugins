using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.AdsB.ViewModels;

public sealed partial class AdsBViewModel
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
        int positioned = Aircraft.Count(item => item.HasPosition);
        int identified = Aircraft.Count(item => item.HasCallsign);
        int altitude = Aircraft.Count(item => item.AltitudeFeet.HasValue);
        int speed = Aircraft.Count(item => item.SpeedKnots.HasValue);
        int emergency = Aircraft.Count(item => item.HasEmergency);
        return new(
            Aircraft.Count == 0 ? "機体の識別・位置・飛行情報を把握するためのADS-Bデータを待っています。"
                : $"{Aircraft.Count:N0}機を把握。位置は {positioned:N0}機、コールサインは {identified:N0}機で取得しています。",
            ReceptionOverview.DescribeScope("保持中の機体（最終受信）", Aircraft.Select(item => item.LastSeen)),
            [new("把握した機体", Aircraft.Count), new("位置取得機", positioned),
             new("コールサイン取得機", identified), new("高度取得機", altitude)],
            [new("速力を取得", $"{speed:N0} / {Aircraft.Count:N0}機"),
             new("位置を未取得", $"{Aircraft.Count - positioned:N0}機"),
             new("コールサインを未取得", $"{Aircraft.Count - identified:N0}機"),
             new("緊急状態の通知を保持", $"{emergency:N0}機（最終受信時点）")],
            "取得済みの値は保持中の情報です。未取得の値から状態は判定しません。地図は設定した保持期間内の位置を表示します。検索条件は集計に反映しません。",
            "機体を比較するなら一覧、受信した通知を追うなら時系列、位置と航跡を見るなら地図へ進めます。");
    }
}
