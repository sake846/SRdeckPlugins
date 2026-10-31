# STD-T98の軽量化（2026-09-30）

## 変更

基準は `985ebf12ac1a71698a77f0803137400c28ed81f8`。
4FSKの321タップFIRとPSKのI/Q各161タップFIRで共用する
`AribT98FirKernel.Dot` の加算を4本に分け、CPUが並行して処理できるようにした。
入力長を一度検査し、その範囲内でベクトルを読み込むことで、ループ内の反復する範囲検査も減らした。
係数、タップ数、倍精度演算、群遅延、同期・復号条件は維持する。
加算順序は変わるため、浮動小数点値の完全一致は要求しない。
SIMD非対応時は従来どおりスカラーで処理する。

`AribT98Voice.TryRender` の状態表示は、毎フレームの文字列補間を定数文字列の選択に置き換えた。
表示文言とドングル失敗時の状態は維持する。新しい依存関係・設定・公開APIは追加していない。

## 計測

Windows x64、AMD Ryzen 5 8640U、.NET SDK 10.0.401 / ランタイム10.0.12、Release。
通常測定の `DOTNET_*` / `SRDECK_TEST_*` 上書きは未指定。
FIRは同一プロセス内で変更前の保存実装と本番実装を交互に測定。
各20万回、8回ウォームアップ後の9回の中央値で、`Vector<double>.Count=4`。

| FIR | 変更前 ns/出力 | 変更後 ns/出力 | 処理時間の削減 |
|---|---:|---:|---:|
| PSK・161タップ（IまたはQ） | 25.50 | 16.56 | 35.1% |
| 4FSK・321タップ | 51.54 | 29.57 | 42.6% |

測定ループの割り当てはどちらも0バイト。
別起動の既存受信器試験では4FSKが152.7→177.1倍実時間、PSKが114.7→126.8倍実時間だった。
これは合成IQを処理する受信器の測定で、FIR単体の削減率をアプリ全体のCPU削減率として扱わない。
USB入力、ホストの広帯域処理、描画、音声デバイス、実機・低性能CPUでの長時間運用は測定に含まない。

既存の音声性能試験は変更前に2,000回で128,000バイト（1回64バイト）の定常割り当てがあり、
0バイトを要求するアサートに失敗していた。変更後は0バイトで成功した。
この試験はウォームアップ済みの同一フレームを使い、AMBEのFECキャッシュを再利用する。
新しい受信フレームの生成・初回FECを含めて0バイトになるという意味ではない。
4FSK/PSK待受の定常割り当ても0バイトを維持した。

既存の `--benchmark-arib-t98` を前後それぞれ3試行実行した。
AWGN 6段階、フェード、インパルス、隣接波、雑音のみ、隣接波のみの11条件、
2入力経路、4受信方式で計264測定。
受信数・誤受理数・AMBEビット誤り数・復号試行数・回復数・欠落時間・初回受信位置など、
時間計測と割り当て量を除く全出力指標が一致した。
割り当て量には共有FECキャッシュの管理領域拡張に伴う揺らぎがあり、同一値は要求していない。

## 再実行

```powershell
dotnet build SRdeck.Tests/SRdeck.Tests.csproj -c Release
dotnet test SRdeck.Tests/SRdeck.Tests.csproj -c Release --no-build --filter 'DisplayName~ARIB STD-T98|DisplayName=Regression catalog completeness'
dotnet run --project SRdeck.Tests/SRdeck.Tests.csproj -c Release --no-build -- --benchmark-t98-fir
dotnet run --project SRdeck.Tests/SRdeck.Tests.csproj -c Release --no-build -- --benchmark-arib-t98 temp/t98-performance 3 --receive-only
```

FIR試験と4FSK/PSKストリーミング試験は、次の選択に
`--environment DOTNET_EnableAVX=0` または `--environment DOTNET_EnableHWIntrinsic=0` を付けても実行できる。

```powershell
dotnet test SRdeck.Tests/SRdeck.Tests.csproj -c Release --no-build --filter 'DisplayName=ARIB STD-T98 FIR equivalence|DisplayName=ARIB STD-T98 streaming IQ|DisplayName=ARIB STD-T98 PSK streaming IQ' --environment DOTNET_EnableHWIntrinsic=0
```

## 回帰試験と変更レビュー

Releaseビルドは警告0・エラー0。STD-T98の35件と登録完全性1件の計36件中35件が成功した。
失敗1件は既存の `ARIB STD-T98 views` で、設定タブ名の期待値との不一致
（`RegressionTests.AribT98.Collapse.cs:60`）。製品側の変更2ファイルを基準版へ戻して
ビルドした比較試験でも同じ失敗を確認した。今回の処理変更では画面・UI試験を変更していない。
AVX無効時と全SIMD無効時は、それぞれ上記3件すべて成功した。
性能の時間判定は無効化していない。

新規の `ARIB STD-T98 FIR equivalence` はseed98321を使用し、空入力、
SIMD幅・4倍展開幅の前後と全端数、161/321タップ、8通りの開始位置、
振幅1e-6/1/1000、各係数位置のインパルスを検証する。
独立したスカラー積和に対する許容差は `sum(abs(product)) * 2e-14 + 1e-25`。
インパルスは完全一致、入力より短い係数配列は例外、定常割り当ては0バイトを要求する。
通常SIMD時の最大絶対誤差は6.366e-12、SIMD無効時は0だった。
試験一覧への検出と登録完全性も確認済み。

適用観点は試験仕様4.1の互換性、4.2/4.3のリセットと分割IQ、4.6/4.8の音声・復号、
4.10の試験登録、5の性能。4.4/4.5/4.7/4.9の設定・保存・UI・ホスト入力処理は製品差分なし。
6の実機・低性能CPUでの受入は未実行。
生値とTRXはローカルの `temp/t98-performance-20260930/` に保存した。
最終結果は `t98-final.trx`、`t98-sse-final.trx`、`t98-scalar-final.trx`、
基準版のUI失敗は `t98-baseline-views.trx`、FIR生値は `fir.txt`、
受信比較は `before/reception.json`、`after/reception.json`、`comparison.json`。
