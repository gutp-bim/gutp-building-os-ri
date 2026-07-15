.PHONY: local-up-azure local-up-oss local-up-dual local-up-minimal local-up-dev \
        local-down-azure local-down-oss local-down-all local-down-minimal local-down-dev \
        demo demo-down test-oss-stack wait-oss-stack validate-oss-issues doctor mvp-test help

# ── Azure ローカル互換スタック (既存 docker-compose.yaml) ─────────────────────
local-up-azure:
	docker compose up -d

local-down-azure:
	docker compose down

# ── OSS スタック (docker-compose.oss.yaml) ────────────────────────────────
local-up-oss:
	docker compose -f docker-compose.oss.yaml up -d

local-down-oss:
	docker compose -f docker-compose.oss.yaml down

# ── ミニマルプロファイル (NATS + TimescaleDB + pgBouncer のみ) ────────────────
local-up-minimal:
	docker compose -f docker-compose.minimal.yaml up -d

local-down-minimal:
	docker compose -f docker-compose.minimal.yaml down

# ── Dev プロファイル (ローカルデバイスシミュレーター) ────────────────────────
# Scenario A (MQTT) のエンドツーエンド検証用。MQTT ブローカ (Mosquitto) は基本構成に
# 含まれない (#25) ため、ここで OSS スタックを `--profile mqtt` + MQTT_HOST 付きで（再）起動し、
# Mosquitto と MQTT_HOST を設定した connector-worker を立ち上げてから、シミュレーターデバイス
# (mqtt_edge_device) を追加する。冪等なので local-up-oss 済みでも安全に再 up できる。
local-up-dev:
	MQTT_HOST=building-os.mosquitto docker compose -f docker-compose.oss.yaml --profile mqtt up -d
	docker compose -f docker-compose.dev.yaml up -d

local-down-dev:
	docker compose -f docker-compose.dev.yaml down

# ── デモ (#155): 1コマンドで「データが流れている（見る→制御する）」状態へ ──────────
# OSS スタック + Web Client + テレメトリ feeder（GW-SOS-001 / SOS-PT-001..008 へ gRPC 周期投入）を
# 起動し、GW-SOS-001 の制御 binding を simulated に上書き（実ゲートウェイ無しでも制御が 200 で通る）。
# 既定 twin は #124 で自動シード済み。起動後 http://localhost:3000（admin/admin）で
# /resources → point 詳細に動く値が出て、書込可点（SOS-PT-004/006/007）の制御が実行できる。
demo:
	docker compose -f docker-compose.oss.yaml -f docker-compose.demo.yaml \
		--profile demo --profile webclient up -d --build
	@echo ""
	@echo "Demo up. Open http://localhost:3000 (admin/admin)."
	@echo "  /resources → SOS-PT-001..008 に動く値（feeder が GW-SOS-001 へ gRPC 投入中）"
	@echo "  制御は書込可の SOS-PT-004(照明)/006(設定温度)/007(ファン)で実行 → 200 が返る"
	@echo "  停止: make demo-down"

demo-down:
	docker compose -f docker-compose.oss.yaml -f docker-compose.demo.yaml \
		--profile demo --profile webclient down

# ── デュアルモード (両方同時起動) ────────────────────────────────────────────
local-up-dual: local-up-azure local-up-oss

local-down-all: local-down-azure local-down-oss

# ── テスト ───────────────────────────────────────────────────────────────────
# (#127) 2 段階: ①Docker Health 宣言サービス(nats/postgres/oxigraph/minio/keycloak 等)の
# 待機、②HTTP readiness(api/connector-worker — chiseled image のため Docker HEALTHCHECK 自体が
# 宣言できず ①は「Health 未宣言 = 健全扱い」で見逃す)。どちらのフェーズもタイムアウトで
# exit 1 する(以前は両分岐とも echo で終わり、常に exit 0 になっていたバグの修正)。
# GatewayBridge は HTTP health 自体が未実装のため対象外(#127 の既知の残課題)。
wait-oss-stack:
	@echo "Waiting for OSS stack to be healthy (max 120s)..."
	@timeout 120 bash -c '\
		until docker compose -f docker-compose.oss.yaml ps --format json \
		  | python3 -c "import sys,json; data=sys.stdin.read(); \
		    services=[json.loads(l) for l in data.splitlines() if l.strip()]; \
		    unhealthy=[s[\"Name\"] for s in services if s.get(\"Health\") not in (\"healthy\",\"\")]; \
		    exit(1 if unhealthy else 0)" 2>/dev/null; \
		do sleep 3; done' \
	  || { echo "Timed out waiting for OSS stack (Docker Health)"; exit 1; }
	@echo "Waiting for application readiness (API /health, ConnectorWorker /health/ready, max 60s)..."
	@timeout 60 bash -c '\
		until curl -sf http://localhost:5000/health >/dev/null 2>&1 \
		   && curl -sf http://localhost:8081/health/ready >/dev/null 2>&1; \
		do sleep 3; done' \
	  || { echo "Timed out waiting for application readiness — API or ConnectorWorker may be crash-looping"; exit 1; }
	@echo "OSS stack is healthy!"

test-oss-stack:
	@bash scripts/test-oss-stack.sh

# ── 起動失敗の自己診断 (#157) ─────────────────────────────────────────────────
# 起動が通らないときに「次に何をすべきか」を各チェックの失敗ごとに提示する診断コマンド。
# 依存サービスが多いため、ポート競合 / seed 失敗 / API クラッシュループ等を切り分けて対処
# ヒントを出す。scripts/test-oss-stack.sh と同じエンドポイントを使い、対処ヒントを追加。
doctor:
	@bash scripts/doctor.sh

validate-oss-issues:
	@bash scripts/validate-oss-issue-readiness.sh

# ── MVP ゲート ────────────────────────────────────────────────────────────────
# MVP として出せる状態かを一発で検証する集約ゲート（#304 Phase 0）。ローカル一次ゲート
# （CI テストは手動起動のみ）。各段の失敗で即時に非ゼロ終了する。
#   1. .NET unit テスト（統合テストを除く）
#   2. web-client の typecheck + build
#   3. OSS スタック起動 → health 待ち
#   4. E2E 評価ランナー（軸別 → KPI ゲート）
mvp-test:
	@echo "== [1/4] .NET unit tests =="
	cd DotNet && dotnet test --filter "FullyQualifiedName!~IntegrationTest"
	@echo "== [2/4] web-client typecheck + build =="
	cd web-client && yarn install --frozen-lockfile && yarn typecheck && yarn build
	@echo "== [3/4] OSS stack up + health =="
	$(MAKE) local-up-oss
	$(MAKE) wait-oss-stack
	@echo "== [4/4] E2E evaluation runner =="
	bash e2e/runner/run-all.sh
	@echo "✅ mvp-test complete"

# ── ヘルプ ───────────────────────────────────────────────────────────────────
help:
	@echo "Building OS — Makefile targets"
	@echo ""
	@echo "  make local-up-azure    Start Azure-compatible local stack"
	@echo "  make local-up-oss      Start OSS stack"
	@echo "  make demo              One-command demo: OSS + Web + live telemetry feeder + control (see→control)"
	@echo "  make demo-down         Stop the demo stack"
	@echo "  make local-up-dual     Start both stacks simultaneously"
	@echo "  make local-up-minimal  Start minimal stack (NATS + TimescaleDB + pgBouncer only)"
	@echo "  make local-down-azure  Stop Azure stack"
	@echo "  make local-down-oss    Stop OSS stack"
	@echo "  make local-down-all    Stop all local stacks"
	@echo "  make local-down-minimal Stop minimal stack"
	@echo "  make local-up-dev      Start Scenario A: (re-)up OSS stack with --profile mqtt + Mosquitto, then the device simulator"
	@echo "  make local-down-dev    Stop device simulator"
	@echo "  make wait-oss-stack    Wait until OSS stack is healthy"
	@echo "  make test-oss-stack    Run health-check tests against OSS stack"
	@echo "  make doctor            Diagnose a running stack; print a fix hint for each failed check"
	@echo "  make validate-oss-issues Validate OSS issue readiness checks"
	@echo "  make mvp-test          MVP gate: dotnet test → web typecheck/build → stack health → E2E runner"
