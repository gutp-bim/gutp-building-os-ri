#!/usr/bin/env bash
# verify-keda-scaling.sh — Issue #116
#
# k3d + KEDA で ConnectorWorker の自動スケールを検証する。
#
# 前提:
#   - k3d, kubectl, helm, python3 がインストール済みであること
#   - Building OS イメージが Harbor もしくはローカルレジストリに存在すること
#
# 使い方:
#   bash scripts/verify-keda-scaling.sh
#
# 環境変数:
#   IMAGE_REGISTRY   コンテナイメージのレジストリ（デフォルト: harbor.eng2.buildingos.local）
#   CLUSTER_NAME     k3d クラスタ名（デフォルト: building-os-keda-test）
#   NAMESPACE        K8s namespace（デフォルト: building-os）

set -euo pipefail

IMAGE_REGISTRY="${IMAGE_REGISTRY:-harbor.eng2.buildingos.local}"
CLUSTER_NAME="${CLUSTER_NAME:-building-os-keda-test}"
NAMESPACE="${NAMESPACE:-building-os}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
ok()   { echo -e "${GREEN}[OK]${NC} $*"; }
fail() { echo -e "${RED}[FAIL]${NC} $*"; exit 1; }
info() { echo -e "${YELLOW}[INFO]${NC} $*"; }

# ── Step 0: 前提チェック ──────────────────────────────────────────────────────
for cmd in k3d kubectl helm python3; do
  command -v "$cmd" &>/dev/null || fail "$cmd が見つかりません。インストールしてください。"
done
ok "前提コマンド確認"

# ── Step 1: k3d クラスタ作成 ──────────────────────────────────────────────────
info "k3d クラスタ '$CLUSTER_NAME' を作成しています..."
k3d cluster create "$CLUSTER_NAME" --agents 2
kubectl config use-context "k3d-${CLUSTER_NAME}"
ok "k3d クラスタ作成"

cleanup() {
  info "クラスタを削除しています..."
  k3d cluster delete "$CLUSTER_NAME" || true
}
trap cleanup EXIT

# ── Step 2: KEDA インストール ─────────────────────────────────────────────────
info "KEDA をインストールしています..."
helm repo add kedacore https://kedacore.github.io/charts
helm repo update
helm install keda kedacore/keda --namespace keda --create-namespace --wait
ok "KEDA インストール"

# ── Step 3: Building OS デプロイ (dev profile) ────────────────────────────────
info "Building OS を dev profile でデプロイしています..."
kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -
helm upgrade --install building-os kubernetes/helm/building-os \
  -f kubernetes/helm/building-os/values.yaml \
  -f kubernetes/helm/building-os/values-dev.yaml \
  --namespace "$NAMESPACE" \
  --set "global.imageRegistry=${IMAGE_REGISTRY}" \
  --wait --timeout 120s
ok "Building OS デプロイ"

# ── Step 4: KEDA ScaledObject 適用 ───────────────────────────────────────────
info "KEDA ScaledObject を適用しています..."
kubectl apply -f kubernetes/keda/connector-worker-scaledobject.yaml
sleep 5
kubectl describe scaledobject connector-worker-scaler -n "$NAMESPACE"
ok "KEDA ScaledObject 適用"

# ── Step 5: 初期レプリカ数を確認 ─────────────────────────────────────────────
INITIAL_REPLICAS=$(kubectl get deployment connector-worker -n "$NAMESPACE" \
  -o jsonpath='{.status.readyReplicas}')
info "初期レプリカ数: ${INITIAL_REPLICAS}"
[[ "${INITIAL_REPLICAS}" -ge 1 ]] || fail "ConnectorWorker が起動していません"

# ── Step 6: 負荷投入 ─────────────────────────────────────────────────────────
info "負荷を投入しています (50 devices)..."
NATS_URL="nats://$(kubectl get svc building-os-nats -n "$NAMESPACE" \
  -o jsonpath='{.spec.clusterIP}'):4222" \
python3 Tools/e2e-performance/device_load_generator.py --devices 50 &
LOAD_PID=$!

# ── Step 7: スケールアウト確認（最大 120 秒待機）────────────────────────────
info "スケールアウトを監視しています（lagThreshold=50、最大 120s）..."
SCALED=false
for i in $(seq 1 24); do
  sleep 5
  CURRENT=$(kubectl get deployment connector-worker -n "$NAMESPACE" \
    -o jsonpath='{.status.readyReplicas}' 2>/dev/null || echo 0)
  info "  ${i}回目チェック: レプリカ数=${CURRENT}"
  if [[ "${CURRENT:-0}" -gt "${INITIAL_REPLICAS}" ]]; then
    SCALED=true
    ok "スケールアウト確認: ${INITIAL_REPLICAS} → ${CURRENT} レプリカ"
    break
  fi
done

kill "$LOAD_PID" 2>/dev/null || true

$SCALED || fail "lagThreshold=50 を超えてもスケールアウトしませんでした"

# ── Step 8: スケールイン確認（cooldownPeriod=60s 後）─────────────────────────
info "スケールイン待機中（cooldownPeriod=60s + バッファ 30s）..."
sleep 90
FINAL=$(kubectl get deployment connector-worker -n "$NAMESPACE" \
  -o jsonpath='{.status.readyReplicas}' 2>/dev/null || echo 0)
info "スケールイン後のレプリカ数: ${FINAL}"
[[ "${FINAL}" -le "${INITIAL_REPLICAS}" ]] \
  && ok "スケールイン確認: ${FINAL} レプリカ" \
  || info "警告: スケールインが完了していません（${FINAL} レプリカ）— cooldownPeriod を延長して再確認してください"

ok "=== KEDA スケール検証 完了 ==="
