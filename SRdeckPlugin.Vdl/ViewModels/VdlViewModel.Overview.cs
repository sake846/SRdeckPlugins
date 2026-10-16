using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Vdl.ViewModels;

public sealed partial class VdlViewModel
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
        AvlcAddress[] addresses = frames.Where(item => item.Avlc is not null)
            .SelectMany(item => new[] { item.Avlc!.Source, item.Avlc.Destination }).ToArray();
        int aircraft = addresses.Where(item => item.Type == AvlcAddressType.Aircraft)
            .Select(item => item.Value).Distinct().Count();
        int stations = addresses.Where(item => item.Type is AvlcAddressType.GroundStationAdministrative or AvlcAddressType.GroundStationDelivery)
            .Select(item => item.Value).Distinct().Count();
        int acars = frames.Count(item => item.Acars is not null);
        int x25 = frames.Count(item => item.X25 is not null);
        return new(
            frames.Count == 0 ? "機体・地上局と上位通信の内訳を把握するためのVDL2データを待っています。"
                : $"機体アドレス {aircraft:N0}件、地上局アドレス {stations:N0}件を把握。ACARSを解析できた通信は {acars:N0}件、X.25は {x25:N0}件です。",
            ReceptionOverview.DescribeScope("保持中のフレーム", frames.Select(item => item.ReceivedAt)),
            [new("機体アドレス", aircraft), new("地上局アドレス", stations),
             new("ACARS解析済み", acars), new("X.25解析済み", x25)],
            ReceptionOverview.CountKinds(frames.Select(item => item.Protocol)),
            "送信元・宛先の両方を集計し、予約・全局宛アドレスは対象数に含みません。プロトコルの表示と上位内容の解析成功は別です。検索条件は集計に反映しません。",
            "機体識別ごとの通信は一覧、プロトコルと本文は時系列、位置を取得した機体は地図で確認できます。");
    }
}
