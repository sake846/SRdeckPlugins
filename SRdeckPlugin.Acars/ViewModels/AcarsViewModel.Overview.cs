using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Acars.ViewModels;

public sealed partial class AcarsViewModel
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
        int aircraft = Messages.Select(item => item.AircraftDisplay)
            .Where(id => id != "機体不明").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int positioned = Messages.Count(item => item.HasPosition);
        int frequencies = Messages.Select(item => item.FrequencyHz).Distinct().Count();
        return new(
            Messages.Count == 0 ? "機体や通信内容を把握するためのACARSデータを待っています。"
                : $"機体識別 {aircraft:N0}件から {Messages.Count:N0}件の通信を取得。位置を読み取れた通信は {positioned:N0}件です。",
            ReceptionOverview.DescribeScope("保持中のメッセージ", Messages.Select(item => item.ReceivedAt)),
            [new("機体識別", aircraft), new("メッセージ", Messages.Count),
             new("位置を含む通信", positioned), new("受信周波数の種類", frequencies)],
            ReceptionOverview.CountKinds(Messages.Select(item => item.CategoryDisplay)),
            "内訳はラベル・本文による表示上の分類です。識別不明の通信も件数に含みます。検索条件は集計に反映しません。",
            "機体ごとの通信は一覧、本文と受信順は時系列、位置を取得した機体は地図で確認できます。");
    }
}
