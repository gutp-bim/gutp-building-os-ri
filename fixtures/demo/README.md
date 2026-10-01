# デモデータセット（#458）— 東京デモビル

`make demo` が起動時にシードする twin と、demo feeder が流すテレメトリの計画です。

| ファイル | 種別 | 内容 |
|---|---|---|
| [`manifest.toml`](manifest.toml) | **正本（手で編集する）** | フロア・機器構成、Point テンプレート（期待周期・単位・警報閾値・制御スキーマ）、タグの付与条件、`degraded` シナリオ |
| [`twin.ttl`](twin.ttl) | 生成物 | デモビル（1,190 Point）+ `fixtures/e2e/twin.ttl` をそのまま同梱（制御デモ用の 8 Point） |
| [`pointlist.csv`](pointlist.csv) | 生成物 | デモビルの Point List（`fixtures/e2e/pointlist.csv` と同じ 30 列） |
| [`feeder-plan.json`](feeder-plan.json) | 生成物 | feeder が Point ごとに送る値のモデルと、シナリオ上の役割（normal / stale / missing / alarm） |

生成物は手で編集しないでください。`manifest.toml` を変えたら再生成して、生成物も一緒にコミットします。

```bash
python3 Tools/demo-fixture-generator/generate.py          # 再生成
python3 Tools/demo-fixture-generator/generate.py --check  # 差分があれば exit 1（CI と同じ検査）
python3 -m pytest Tools/demo-fixture-generator            # 生成器のテスト
```

`fixtures/e2e/` は変更しません。デモの見せ方は [`docs/guides/demo-walkthrough.md`](../../docs/guides/demo-walkthrough.md) を参照してください。
